using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace ZZZ.Views;

public partial class RegionCaptureWindow : Window
{
    private readonly BitmapSource _source;
    private Point _start;
    private bool _dragging;
    public string? SavedPath { get; private set; }

    public RegionCaptureWindow(byte[] pngBytes)
    {
        InitializeComponent();
        using var stream = new MemoryStream(pngBytes);
        _source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        _source.Freeze();
        Preview.Source = _source;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(_source.PixelWidth, area.Width * 0.94);
        Height = Math.Min(_source.PixelHeight, area.Height * 0.90);
    }

    private void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = ClampToImage(e.GetPosition(Overlay));
        _dragging = true;
        Selection.Visibility = Visibility.Visible;
        Canvas.SetLeft(Selection, _start.X); Canvas.SetTop(Selection, _start.Y);
        Selection.Width = Selection.Height = 0;
        Overlay.CaptureMouse();
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var point = ClampToImage(e.GetPosition(Overlay));
        var rect = new Rect(_start, point);
        Canvas.SetLeft(Selection, rect.Left); Canvas.SetTop(Selection, rect.Top);
        Selection.Width = rect.Width; Selection.Height = rect.Height;
    }

    private void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Overlay.ReleaseMouseCapture();
        var end = ClampToImage(e.GetPosition(Overlay));
        var selection = new Rect(_start, end);
        if (selection.Width < 5 || selection.Height < 5) { Selection.Visibility = Visibility.Collapsed; return; }
        SaveSelection(selection);
    }

    private void SaveSelection(Rect selection)
    {
        var image = ImageBounds();
        var scale = image.Width / _source.PixelWidth;
        var x = Clamp((int)Math.Round((selection.Left - image.Left) / scale), 0, _source.PixelWidth - 1);
        var y = Clamp((int)Math.Round((selection.Top - image.Top) / scale), 0, _source.PixelHeight - 1);
        var width = Clamp((int)Math.Round(selection.Width / scale), 1, _source.PixelWidth - x);
        var height = Clamp((int)Math.Round(selection.Height / scale), 1, _source.PixelHeight - y);
        var cropped = new CroppedBitmap(_source, new Int32Rect(x, y, width, height));
        cropped.Freeze();

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ZZZ Captures");
        Directory.CreateDirectory(folder);
        SavedPath = Path.Combine(folder, $"ZZZ-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        using (var file = File.Create(SavedPath))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            encoder.Save(file);
        }
        try { Clipboard.SetImage(cropped); } catch { }
        DialogResult = true;
    }

    private Rect ImageBounds()
    {
        var scale = Math.Min(Overlay.ActualWidth / _source.PixelWidth, Overlay.ActualHeight / _source.PixelHeight);
        var width = _source.PixelWidth * scale;
        var height = _source.PixelHeight * scale;
        return new Rect((Overlay.ActualWidth - width) / 2, (Overlay.ActualHeight - height) / 2, width, height);
    }

    private Point ClampToImage(Point point)
    {
        var bounds = ImageBounds();
        return new Point(Clamp(point.X, bounds.Left, bounds.Right), Clamp(point.Y, bounds.Top, bounds.Bottom));
    }

    private static int Clamp(int value, int minimum, int maximum) => Math.Min(Math.Max(value, minimum), maximum);
    private static double Clamp(double value, double minimum, double maximum) => Math.Min(Math.Max(value, minimum), maximum);

    private void Window_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) DialogResult = false; }
    private void Hint_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement hint) Canvas.SetLeft(hint, Math.Max(12, (Overlay.ActualWidth - hint.ActualWidth) / 2));
    }
}
