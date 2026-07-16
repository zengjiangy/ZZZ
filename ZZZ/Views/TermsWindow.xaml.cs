using System.Windows;

namespace ZZZ.Views;

public partial class TermsWindow : Window
{
    // Increment only when the substance of the terms changes, not for ordinary
    // application releases, so users are not prompted unnecessarily.
    public const string CurrentTermsVersion = "1";

    public TermsWindow() => InitializeComponent();

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
