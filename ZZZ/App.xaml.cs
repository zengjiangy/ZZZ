using System.Windows;
using ZZZ.Services;
using ZZZ.ViewModels;

namespace ZZZ;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    private SingleInstanceService? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.Initialize();
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary)
        {
            await _singleInstance.ForwardAsync(e.Args);
            Shutdown();
            return;
        }
        DevToolsPreferenceService.SuppressObsoleteWebHintBanner();
        NativeDependencyService.PrepareWebView2Loader();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Window_Loaded));
        Services = new AppServices();
        await Services.InitializeAsync();
        LocalizationService.Apply(Services.Settings.Current.Ui.Language);
        ThemeService.Apply(Services.Settings.Current.Appearance);
        var launchUrls = e.Args.Select(SingleInstanceService.NormalizeTarget).Where(x => x is not null).Cast<string>().ToArray();
        var viewModel = new MainViewModel(Services, launchUrls);
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
        _singleInstance.StartListening(url => Dispatcher.BeginInvoke(new Action(() =>
        {
            viewModel.CreateTab(url);
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        })));
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
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
