using System;
using AtomUI.Desktop.Controls;
using Avalonia.Interactivity;
using CxShell.ViewModels;

namespace CxShell.Views;

public partial class SessionManagerWindow : Window
{
    protected override Type StyleKeyOverride { get; } = typeof(Window);

    public SessionManagerWindow()
    {
        InitializeComponent();
    }

    public SessionManagerWindow(SessionTreeViewModel vm)
        : this()
    {
        vm.SelectedNode = null;
        // Bind before Show so the DataGrid source and first layout are ready
        // before macOS presents the native window. Setting DataContext from
        // Opened forces a second layout pass after the window is already visible,
        // which leaves only the native title bar on screen until that pass ends.
        DataContext = vm;
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SessionTreeViewModel vm || vm.SelectedSession == null)
            return;

        await vm.MainWindow.ConnectSession(vm.SelectedSession);
        Close();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

}
