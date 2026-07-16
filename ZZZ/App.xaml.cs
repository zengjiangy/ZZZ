using System.Windows;
using ZZZ.Services;
using ZZZ.ViewModels;
using ZZZ.Views;

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
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(Window_Loaded));
        Services = new AppServices();
        await Services.InitializeAsync();
        LocalizationService.Apply(Services.Settings.Current.Ui.Language);
        ThemeService.Apply(Services.Settings.Current.Appearance, Services.Settings.Current.StartPage, Services.Settings.Current.Ui.GrayscaleMode);
        if (!string.Equals(Services.Settings.Current.Legal.AcceptedTermsVersion, TermsWindow.CurrentTermsVersion, StringComparison.Ordinal))
        {
            var terms = new TermsWindow();
            if (terms.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            Services.Settings.Current.Legal.AcceptedTermsVersion = TermsWindow.CurrentTermsVersion;
            await Services.Settings.SaveAsync();
        }
        var launchUrls = e.Args.Select(SingleInstanceService.NormalizeTarget).Where(x => x is not null).Cast<string>().ToArray();
        var viewModel = new MainViewModel(Services, launchUrls);
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
        _ = Dispatcher.BeginInvoke(new Action(() => _ = Services.EnsureBackgroundInitializedAsync()), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is not null)
            Services.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
