using System.Windows;
using ZZZ.Configuration;

namespace ZZZ.Views;
public partial class ClearDataWindow : Window
{
    public ClearDataSelection Selection { get; }
    public ClearDataWindow(ClearDataSelection source)
    {
        InitializeComponent();
        Selection = new ClearDataSelection { History = source.History, Cache = source.Cache, Cookies = source.Cookies, Passwords = source.Passwords, FormData = source.FormData };
        DataContext = Selection;
    }
    private void Clear_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
