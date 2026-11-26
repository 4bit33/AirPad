using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using AirPad.ViewModels;

namespace AirPad;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _fullscreen;
    private bool _chromeHidden;
    private WindowState _prevState;
    private WindowStyle _prevStyle;

    public MainWindow()
    {
        InitializeComponent();

        var app = (App)Application.Current;
        _vm = new MainViewModel(app.Logger, app.Manager);
        DataContext = _vm;

        _vm.FullscreenToggleRequested += (_, _) => ToggleFullscreen();
        _vm.ChromeToggleRequested += (_, _) => ToggleChrome();
        _vm.SettingsRequested += (_, _) => OpenSettings();
        _vm.FullscreenRequested += (_, _) => Dispatcher.BeginInvoke(EnterFullscreen);

        Closed += (_, _) => _vm.Dispose();
        PreviewKeyDown += OnPreviewKeyDown;

        // Launched by "Start with Windows": the point is that the iPad can see the PC without anyone clicking Start.
        if (Environment.GetCommandLineArgs().Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            WindowState = WindowState.Minimized;
            Loaded += (_, _) => _vm.StartCommand.Execute(null);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && (_fullscreen || _chromeHidden))
        {
            ExitFullscreen();
            ShowChrome();
            e.Handled = true;
        }
        else if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleChrome();
            e.Handled = true;
        }
    }

    private void ToggleFullscreen()
    {
        if (_fullscreen)
            ExitFullscreen();
        else
            EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_fullscreen)
            return;
        _fullscreen = true;
        _prevState = WindowState;
        _prevStyle = WindowStyle;
        WindowStyle = WindowStyle.None;
        WindowState = WindowState.Maximized;
        Topmost = false;
    }

    private void ExitFullscreen()
    {
        if (!_fullscreen)
            return;
        _fullscreen = false;
        WindowStyle = _prevStyle;
        WindowState = _prevState == WindowState.Maximized ? WindowState.Normal : _prevState;
    }

    private void ToggleChrome()
    {
        if (_chromeHidden)
            ShowChrome();
        else
            HideChrome();
    }

    private void HideChrome()
    {
        _chromeHidden = true;
        TopBar.Visibility = Visibility.Collapsed;
        BottomBar.Visibility = Visibility.Collapsed;
        LogPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowChrome()
    {
        _chromeHidden = false;
        TopBar.Visibility = Visibility.Visible;
        BottomBar.Visibility = Visibility.Visible;
        LogPanel.Visibility = Visibility.Visible;
    }

    private void OpenSettings()
    {
        var app = (App)Application.Current;
        var dlg = new SettingsWindow(app.Manager)
        {
            Owner = this,
        };
        if (dlg.ShowDialog() == true)
            _vm.OnSettingsSaved();
    }
}

public sealed class BoolToVisibilityInverseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
