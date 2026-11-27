using System;
using Avalonia;
using AtomUI;
using CxShell.Services;
using ReactiveUI.Avalonia;
using Velopack;

namespace CxShell;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetArgs(args)
            .Run();

        if (!CommandLineHandoffService.TryAcquireSingleInstanceOrForward(args, out var singleInstance))
            return;

        using (singleInstance)
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UseReactiveUI(builder => { })
            .UseAtomUIPlatformDetect()
            .With(new MacOSPlatformOptions
            {
                DisableSetProcessName = false
            })
            .WithAtomUIDefaultOptions()
            .UseAtomUI(App.ConfigureAtomUI)
            .LogToTrace();
    }
}
