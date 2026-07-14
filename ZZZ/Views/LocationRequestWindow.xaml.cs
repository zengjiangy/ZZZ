using System.Globalization;
using System.Windows;
using ZZZ.Services;

namespace ZZZ.Views;

public partial class LocationRequestWindow : Window
{
    public bool AllowLocation { get; private set; }
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public double Accuracy { get; private set; }

    public LocationRequestWindow(string title, string url, double latitude, double longitude, double accuracy)
    {
        InitializeComponent();
        SiteText.Text = string.IsNullOrWhiteSpace(title) ? url : $"{title} · {url}";
        LatitudeBox.Text = latitude.ToString("0.######", CultureInfo.InvariantCulture);
        LongitudeBox.Text = longitude.ToString("0.######", CultureInfo.InvariantCulture);
        AccuracyBox.Text = accuracy.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        if (!TryNumber(LatitudeBox.Text, out var latitude) || latitude < -90 || latitude > 90 ||
            !TryNumber(LongitudeBox.Text, out var longitude) || longitude < -180 || longitude > 180 ||
            !TryNumber(AccuracyBox.Text, out var accuracy) || accuracy <= 0)
        {
            SiteText.Text = LocalizationService.Text("InvalidCoordinates");
            return;
        }
        Latitude = latitude; Longitude = longitude; Accuracy = accuracy; AllowLocation = true; DialogResult = true;
    }

    private void Deny_Click(object sender, RoutedEventArgs e) { AllowLocation = false; DialogResult = false; }
    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
}
