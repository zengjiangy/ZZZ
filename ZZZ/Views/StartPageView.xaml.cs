using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZZZ.Services;
using ZZZ.ViewModels;
using ZZZ.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZZZ.Views;

public partial class StartPageView : UserControl
{
    private readonly BrowserTabViewModel _tab;
    private DispatcherTimer? _animationTimer;
    private Stream? _animationStream;
    private IReadOnlyList<BitmapSource> _frames = [];
    private IReadOnlyList<TimeSpan> _delays = [];
    private int _frameIndex;
    private bool _isLoaded;
    private int _backgroundLoadRevision;
    private readonly HashSet<string> _faviconLoads = new(StringComparer.OrdinalIgnoreCase);

    public StartPageView(BrowserTabViewModel tab)
    {
        InitializeComponent();
        _tab = tab;
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        ApplySettings();
        App.Services.Bookmarks.Changed += Bookmarks_Changed;
        SearchBox.Focus();
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = false;
        _backgroundLoadRevision++;
        App.Services.Bookmarks.Changed -= Bookmarks_Changed;
        StopAnimation();
    }

    private void ApplySettings()
    {
        var settings = App.Services.Settings.Current.StartPage;
        Color background;
        try { background = (Color)ColorConverter.ConvertFromString(settings.BackgroundColor); }
        catch { background = Color.FromRgb(16, 24, 38); }
        if (App.Services.Settings.Current.Ui.GrayscaleMode) background = ToGrayscale(background);
        Root.Background = new SolidColorBrush(background);
        BackgroundImage.Opacity = Math.Max(0.1, Math.Min(1, settings.BackgroundOpacity));
        BookmarksList.Visibility = settings.ShowBookmarks ? Visibility.Visible : Visibility.Collapsed;
        RefreshBookmarks();
        StopAnimation();
        BackgroundImage.Source = null;
        ApplyContrast(background, false, 0);
        var path = AppPaths.ResolveDataFile(settings.BackgroundImage);
        var revision = ++_backgroundLoadRevision;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isLoaded || revision != _backgroundLoadRevision) return;
            var hasImage = LoadBackground(path, out var imageLuminance);
            ApplyContrast(background, hasImage, imageLuminance);
        }), DispatcherPriority.ApplicationIdle);
    }

    private bool LoadBackground(string path, out double imageLuminance)
    {
        imageLuminance = 0;
        StopAnimation();
        BackgroundImage.Source = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                _animationStream = File.OpenRead(path);
                var decoder = new GifBitmapDecoder(_animationStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                if (decoder.Frames.Count == 0) return false;
                var first = decoder.Frames[0];
                var scale = Math.Min(1d, 1280d / Math.Max(1, first.PixelWidth));
                var pixelsPerFrame = Math.Max(1d, first.PixelWidth * scale * first.PixelHeight * scale);
                var frameBudget = Math.Max(1, Math.Min(90, (int)(24_000_000d / pixelsPerFrame)));
                _frames = decoder.Frames.Take(frameBudget).Select(x => PrepareForDisplay(ScaleForDisplay(x, 1280))).ToArray();
                _delays = decoder.Frames.Take(_frames.Count).Select(ReadDelay).ToArray();
                if (_frames.Count == 0) return false;
                imageLuminance = EstimateLuminance(_frames[0]);
                BackgroundImage.Source = _frames[0];
                if (_frames.Count > 1) StartAnimation();
                return true;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1920;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            var prepared = PrepareForDisplay(bitmap);
            imageLuminance = EstimateLuminance(prepared);
            BackgroundImage.Source = prepared;
            return true;
        }
        catch
        {
            BackgroundImage.Source = null;
            _animationStream?.Dispose();
            _animationStream = null;
            return false;
        }
    }

    private void ApplyContrast(Color background, bool hasImage, double imageLuminance)
    {
        var useDarkText = hasImage ? imageLuminance > 0.62 : RelativeLuminance(background) > 0.42;
        Resources["StartPageForegroundBrush"] = new SolidColorBrush(useDarkText ? Color.FromRgb(16, 24, 38) : Colors.White);
        Resources["StartPagePanelBrush"] = new SolidColorBrush(useDarkText ? Color.FromArgb(224, 255, 255, 255) : Color.FromArgb(217, 32, 39, 51));
        Resources["StartPageBorderBrush"] = new SolidColorBrush(useDarkText ? Color.FromArgb(72, 16, 24, 38) : Color.FromArgb(68, 255, 255, 255));
        BackgroundScrim.Background = new SolidColorBrush(hasImage
            ? useDarkText ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(76, 0, 0, 0)
            : Colors.Transparent);
    }

    private static BitmapSource PrepareForDisplay(BitmapSource source)
    {
        if (!App.Services.Settings.Current.Ui.GrayscaleMode) return source;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var gray = (byte)Math.Round(0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2]);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
        }
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static double EstimateLuminance(BitmapSource source)
    {
        var scale = Math.Min(1d, 64d / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource sample = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        var converted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        if (pixels.Length == 0) return 0;
        double total = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            total += (0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2]) / 255d;
        return total / (pixels.Length / 4d);
    }

    private static Color ToGrayscale(Color color)
    {
        var gray = (byte)Math.Round(0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B);
        return Color.FromArgb(color.A, gray, gray, gray);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static BitmapSource ScaleForDisplay(BitmapSource source, int maximumWidth)
    {
        BitmapSource result = source;
        if (source.PixelWidth > maximumWidth)
        {
            var scale = maximumWidth / (double)source.PixelWidth;
            result = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        if (result.CanFreeze) result.Freeze();
        return result;
    }

    private static TimeSpan ReadDelay(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/grctlext/Delay") is ushort delay)
                return TimeSpan.FromMilliseconds(Math.Max(20, delay * 10));
        }
        catch { }
        return TimeSpan.FromMilliseconds(100);
    }

    private void StartAnimation()
    {
        _frameIndex = 0;
        _animationTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = _delays[0] };
        _animationTimer.Tick += AnimationTimer_Tick;
        _animationTimer.Start();
    }

    private void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (_frames.Count < 2 || _animationTimer is null) return;
        _frameIndex = (_frameIndex + 1) % _frames.Count;
        BackgroundImage.Source = _frames[_frameIndex];
        _animationTimer.Interval = _delays[_frameIndex];
    }

    private void StopAnimation()
    {
        if (_animationTimer is not null)
        {
            _animationTimer.Stop();
            _animationTimer.Tick -= AnimationTimer_Tick;
            _animationTimer = null;
        }
        _frames = [];
        _delays = [];
        _animationStream?.Dispose();
        _animationStream = null;
    }

    private void Bookmarks_Changed(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(RefreshBookmarks));
    private void RefreshBookmarks()
    {
        var settings = App.Services.Settings.Current.StartPage;
        var width = Math.Max(100, Math.Min(260, settings.BookmarkTileWidth));
        var groups = App.Services.Bookmarks.Items.Where(x => x.ShowOnStartPage).Take(24)
            .Select(x => new StartBookmark
            {
                Group = string.IsNullOrWhiteSpace(x.Group) ? LocalizationService.Text("Ungrouped") : x.Group.Trim(),
                Title = x.Title,
                Url = x.Url,
                DisplayUrl = Uri.TryCreate(x.Url, UriKind.Absolute, out var uri) ? uri.Host : x.Url,
                Favicon = App.Services.Favicons.GetCached(x.Url),
                TileWidth = width,
                TileHeight = settings.BookmarkStyle == BookmarkTileStyle.Card ? 72 : settings.BookmarkStyle == BookmarkTileStyle.Compact ? 38 : 48,
                TilePadding = settings.BookmarkStyle == BookmarkTileStyle.Compact ? new Thickness(9, 4, 9, 4) : new Thickness(14, 8, 14, 8),
                TitleSize = settings.BookmarkStyle == BookmarkTileStyle.Card ? 14 : 12,
                ShowUrl = settings.BookmarkStyle == BookmarkTileStyle.Card
            })
            .GroupBy(x => x.Group, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => new StartBookmarkGroup { Name = x.Key, Items = x.ToArray() })
            .ToArray();
        BookmarksList.ItemsSource = groups;
        foreach (var bookmark in groups.SelectMany(x => x.Items).Where(x => x.Favicon is null && _faviconLoads.Add(x.Url)))
            _ = LoadFaviconAsync(bookmark);
    }

    private async Task LoadFaviconAsync(StartBookmark bookmark)
    {
        var image = await App.Services.Favicons.GetOrFetchAsync(bookmark.Url);
        if (_isLoaded && image is not null) bookmark.Favicon = image;
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        _tab.NavigateText(SearchBox.Text);
        e.Handled = true;
    }

    private void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) _tab.NavigateText(url);
    }

    private sealed class StartBookmark : ObservableObject
    {
        private ImageSource? _favicon;
        public string Group { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string DisplayUrl { get; set; } = string.Empty;
        public ImageSource? Favicon { get => _favicon; set => SetProperty(ref _favicon, value); }
        public string FaviconFallback => FaviconCacheService.FallbackLetter(Title, Url);
        public double TileWidth { get; set; }
        public double TileHeight { get; set; }
        public Thickness TilePadding { get; set; }
        public double TitleSize { get; set; }
        public bool ShowUrl { get; set; }
    }

    private sealed class StartBookmarkGroup
    {
        public string Name { get; set; } = string.Empty;
        public IReadOnlyList<StartBookmark> Items { get; set; } = [];
    }
}
