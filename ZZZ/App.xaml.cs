using System.Windows;
using ZZZ.Services;
using ZZZ.ViewModels;

namespace ZZZ;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Window_Loaded));
        Services = new AppServices();
        await Services.InitializeAsync();
        LocalizationService.Apply(Services.Settings.Current.Ui.Language);
        ThemeService.Apply(Services.Settings.Current.Appearance);
        var window = new MainWindow { DataContext = new MainViewModel(Services) };
        MainWindow = window;
        window.Show();
    }

    private static void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) ThemeService.ApplyWindowChrome(window);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (Services is not null)
        {
            await Services.Session.SaveAsync(Services.Tabs.Items.Where(x => !x.IsPrivate).Select(x => x.Url));
            await Services.Privacy.ClearOnExitAsync();
            await Services.Settings.SaveAsync();
            Services.Dispose();
        }
        base.OnExit(e);
    }
}
