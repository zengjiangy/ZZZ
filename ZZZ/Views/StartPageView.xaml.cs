using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZZZ.Services;
using ZZZ.ViewModels;

namespace ZZZ.Views;

public partial class StartPageView : UserControl
{
    private readonly BrowserTabViewModel _tab;
    private DispatcherTimer? _animationTimer;
    private Stream? _animationStream;
    private IReadOnlyList<BitmapSource> _frames = [];
    private IReadOnlyList<TimeSpan> _delays = [];
    private int _frameIndex;

    public StartPageView(BrowserTabViewModel tab)
    {
        InitializeComponent();
        _tab = tab;
    }

    private void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        App.Services.Bookmarks.Changed += Bookmarks_Changed;
        SearchBox.Focus();
    }

    private void UserControl_Unloaded(object sender, RoutedEventArgs e)
    {
        App.Services.Bookmarks.Changed -= Bookmarks_Changed;
        StopAnimation();
    }

    private void ApplySettings()
    {
        var settings = App.Services.Settings.Current.StartPage;
        try { Root.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(settings.BackgroundColor)); }
        catch { Root.Background = new SolidColorBrush(Color.FromRgb(16, 24, 38)); }
        BackgroundImage.Opacity = Math.Max(0.1, Math.Min(1, settings.BackgroundOpacity));
        BookmarksList.Visibility = settings.ShowBookmarks ? Visibility.Visible : Visibility.Collapsed;
        RefreshBookmarks();
        LoadBackground(AppPaths.ResolveDataFile(settings.BackgroundImage));
    }

    private void LoadBackground(string path)
    {
        StopAnimation();
        BackgroundImage.Source = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
            {
                _animationStream = File.OpenRead(path);
                var decoder = new GifBitmapDecoder(_animationStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                if (decoder.Frames.Count == 0) return;
                var first = decoder.Frames[0];
                var scale = Math.Min(1d, 1280d / Math.Max(1, first.PixelWidth));
                var pixelsPerFrame = Math.Max(1d, first.PixelWidth * scale * first.PixelHeight * scale);
                var frameBudget = Math.Max(1, Math.Min(90, (int)(24_000_000d / pixelsPerFrame)));
                _frames = decoder.Frames.Take(frameBudget).Select(x => ScaleForDisplay(x, 1280)).ToArray();
                _delays = decoder.Frames.Take(_frames.Count).Select(ReadDelay).ToArray();
                if (_frames.Count == 0) return;
                BackgroundImage.Source = _frames[0];
                if (_frames.Count > 1) StartAnimation();
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 1920;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            BackgroundImage.Source = bitmap;
        }
        catch
        {
            BackgroundImage.Source = null;
            _animationStream?.Dispose();
            _animationStream = null;
        }
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
    private void RefreshBookmarks() => BookmarksList.ItemsSource = App.Services.Bookmarks.Items.Take(24).ToArray();

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
}
