using AtomUI.Desktop.Controls;
using CxShell.Services;
using CxShell.Views;

namespace CxShell.Tests;

public sealed class AtomUiDialogServiceTests
{
    [Fact]
    public void MessageBoxButtonText_UsesCurrentLocalizedDefaults()
    {
        var options = AtomUiDialogService.WithLocalizedButtonText(new MessageBoxOptions());

        Assert.Equal(LocalizationService.Shared.Text("Common.Ok"), options.OkButtonText);
        Assert.Equal(LocalizationService.Shared.Text("Common.Cancel"), options.CancelButtonText);
    }

    [Fact]
    public void MessageBoxButtonText_PreservesExplicitLabels()
    {
        var options = AtomUiDialogService.WithLocalizedButtonText(new MessageBoxOptions
        {
            OkButtonText = "Connect",
            CancelButtonText = "Back"
        });

        Assert.Equal("Connect", options.OkButtonText);
        Assert.Equal("Back", options.CancelButtonText);
    }
}
