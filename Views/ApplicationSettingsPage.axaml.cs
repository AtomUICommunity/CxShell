using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CxShell.Services;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class ApplicationSettingsPage : UserControl
{
    public ApplicationSettingsPage()
    {
        InitializeComponent();
    }

    private async void OnBrowseBackgroundImageClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null || DataContext is not ApplicationSettingsViewModel vm)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Shared.Text("Dialog.FilePicker.BackgroundImage"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(LocalizationService.Shared.Text("Dialog.FilePicker.ImageFiles"))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"]
                },
                FilePickerFileTypes.All
            ]
        });

        var file = files.FirstOrDefault();
        if (file != null)
            vm.BackgroundImagePath = file.Path.LocalPath;
    }
}
