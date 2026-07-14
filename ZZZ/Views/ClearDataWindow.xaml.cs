using System.Windows;
using ZZZ.Configuration;

namespace ZZZ.Views;
public partial class ClearDataWindow : Window
{
    private bool _confirming;
    public ClearDataSelection Selection { get; }
    public ClearDataWindow(ClearDataSelection source, bool historyOnly = false)
    {
        InitializeComponent();
        Selection = new ClearDataSelection { History = source.History, Cache = source.Cache, Cookies = source.Cookies, Passwords = source.Passwords, FormData = source.FormData };
        DataContext = Selection;
        if (historyOnly)
        {
            CacheCheck.Visibility = CookiesCheck.Visibility = PasswordsCheck.Visibility = FormDataCheck.Visibility = Visibility.Collapsed;
        }
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_confirming) { DialogResult = true; return; }
        _confirming = true;
        SelectionPanel.Visibility = Visibility.Collapsed;
        ConfirmPanel.Visibility = Visibility.Visible;
        ClearButton.Content = Services.LocalizationService.Text("ClearNow");
        CancelButton.Content = Services.LocalizationService.Text("DialogBack");
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_confirming) { DialogResult = false; return; }
        _confirming = false;
        SelectionPanel.Visibility = Visibility.Visible;
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ClearButton.Content = Services.LocalizationService.Text("Continue");
        CancelButton.Content = Services.LocalizationService.Text("Cancel");
    }
}
