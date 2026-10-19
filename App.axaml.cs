using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Styling;
using AtomUI;
using AtomUI.Controls;
using AtomUI.Desktop.Controls;
using AtomUI.Theme;
using AtomUI.Theme.Algorithms;
using AtomUI.Theme.Configuration;
using CxShell.Models;
using CxShell.Services;
using CxShell.ViewModels;
using CxShell.Views;

namespace CxShell;

public partial class App : Application
{
    private TrayIcon? _windowsTrayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        InstallMacOsApplicationMenu();
    }

    public static void ConfigureAtomUI(IAtomUIBuilder builder)
    {
        var initialTheme = LoadInitialTheme();
        builder.WithInitialTheme(IThemeManager.DEFAULT_THEME_ID, initialTheme);
        builder.UseAlibabaSansFont();
        builder.UseDesktopControls();
        builder.UseDesktopColorPicker();
        builder.UseDesktopDataGrid();
    }

    private static ThemeConfig LoadInitialTheme()
    {
        var themeMode = ApplicationSettings.DarkThemeMode;

        try
        {
            var legacySettings = new SessionStorageService().Load().Settings;
            themeMode = new ApplicationSettingsStore().Load(legacySettings).ThemeMode;
        }
        catch
        {
            // Keep startup on the established dark theme if the settings file is unavailable.
        }

        var algorithms = string.Equals(
            themeMode,
            ApplicationSettings.LightThemeMode,
            StringComparison.OrdinalIgnoreCase)
            ? new[] { ThemeAlgorithm.Default }
            : new[] { ThemeAlgorithm.Default, ThemeAlgorithm.Dark };

        return new ThemeConfigBuilder()
            .WithAlgorithms(algorithms)
            .Build();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            RegisterAtomUiWindowDecorationsTheme();
            desktop.MainWindow = new MainWindow(desktop.Args ?? Array.Empty<string>());
            InitializeWindowsTrayIcon(desktop);
            desktop.Exit += (_, _) => DisposeWindowsTrayIcon();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void RegisterAtomUiWindowDecorationsTheme()
    {
        var resourceKey = typeof(WindowDrawnDecorations);
        var themeProvider = Styles
            .OfType<IThemeManager>()
            .OfType<IResourceProvider>()
            .FirstOrDefault();

        if (themeProvider?.TryGetResource(resourceKey, ActualThemeVariant, out var resource) == true &&
            resource is ControlTheme controlTheme)
        {
            Resources[resourceKey] = controlTheme;
            return;
        }

        throw new InvalidOperationException("AtomUI's WindowDrawnDecorations theme could not be resolved.");
    }

    private void InitializeWindowsTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!OperatingSystem.IsWindows() || desktop.MainWindow is not { } mainWindow)
            return;

        var showWindowItem = new NativeMenuItem("显示 CxShell");
        showWindowItem.Click += (_, _) => ShowMainWindow(desktop);

        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Items.Add(showWindowItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        _windowsTrayIcon = new TrayIcon
        {
            Icon = mainWindow.Icon,
            ToolTipText = "CxShell",
            Menu = menu,
            IsVisible = true
        };
        _windowsTrayIcon.Clicked += (_, _) => ShowMainWindow(desktop);

        var trayIcons = new TrayIcons();
        trayIcons.Add(_windowsTrayIcon);
        TrayIcon.SetIcons(this, trayIcons);
    }

    private static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is not { } mainWindow)
            return;

        if (mainWindow.WindowState == WindowState.Minimized)
            mainWindow.WindowState = WindowState.Normal;

        mainWindow.Show();
        mainWindow.Activate();
    }

    private void DisposeWindowsTrayIcon()
    {
        _windowsTrayIcon?.Dispose();
        _windowsTrayIcon = null;
        TrayIcon.SetIcons(this, new TrayIcons());
    }

    private void InstallMacOsApplicationMenu()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        var aboutItem = new NativeMenuItem
        {
            Header = "About CxShell"
        };
        aboutItem.Click += (_, _) => ShowAboutFromApplicationMenu();

        var appMenu = new NativeMenu();
        appMenu.Items.Add(aboutItem);
        NativeMenu.SetMenu(this, appMenu);
    }

    private void ShowAboutFromApplicationMenu()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.DataContext: MainWindowViewModel vm } &&
            vm.ShowAboutCommand.CanExecute(null))
        {
            vm.ShowAboutCommand.Execute(null);
        }
    }
}
