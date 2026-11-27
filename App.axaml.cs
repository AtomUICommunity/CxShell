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
using AtomUI.DesignLanguages.AntDesign;
using AtomUI.Desktop.Controls.Themes.AntDesign;
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
        var initialTheme = LoadInitialThemeSelection();
        builder.UseAlibabaSansFont();
        builder.UseDesktopControls();
        builder.UseDesktopColorPicker();
        builder.UseDesktopDataGrid();
        builder.UseAntDesign(language => language
            .AddDesktopControls()
            .AddDesktopColorPicker()
            .AddDesktopDataGrid());
        builder.WithInitialTheme(initialTheme);
    }

    private static ThemeSelection LoadInitialThemeSelection()
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

        var appearance = string.Equals(
            themeMode,
            ApplicationSettings.LightThemeMode,
            StringComparison.OrdinalIgnoreCase)
            ? ThemeAppearance.Light
            : ThemeAppearance.Dark;

        return ThemeSelection.Of<AntDesignLanguage>()
            .WithTheme(AntDesignThemes.DaybreakBlue)
            .WithAppearance(appearance);
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
        if (TryGetResource(resourceKey, ActualThemeVariant, out var resource) && resource is ControlTheme appTheme)
        {
            Resources[resourceKey] = appTheme;
            return;
        }

        foreach (var provider in Styles.OfType<IResourceProvider>())
        {
            if (provider.TryGetResource(resourceKey, ActualThemeVariant, out resource) && resource is ControlTheme theme)
            {
                Resources[resourceKey] = theme;
                return;
            }
        }

        System.Diagnostics.Debug.WriteLine(
            "AtomUI WindowDrawnDecorations theme was not exposed by the active theme providers; continuing with Avalonia's default window decorations.");
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
