using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CxShell.Services;

namespace CxShell.Views;

/// <summary>
/// Asks for the master password that protects an mRemoteNG configuration.
/// mRemoteNG encrypts every stored password with either a user chosen master
/// password or its built-in default, so a protected file cannot be read until
/// this value is supplied.
/// </summary>
public partial class ImportPasswordWindow : AtomUI.Desktop.Controls.Window
{
    private static string T(string key) => LocalizationService.Shared.Text(key);
    private static string Tf(string key, params object[] args) => string.Format(T(key), args);

    protected override Type StyleKeyOverride { get; } = typeof(AtomUI.Desktop.Controls.Window);

    public ImportPasswordWindow()
    {
        InitializeComponent();
        Opened += (_, _) => PasswordBox.Focus();
    }

    public static async Task<string?> PromptAsync(Window owner, string fileName, bool isRetry)
    {
        var window = new ImportPasswordWindow
        {
            Title = T("SessionManager.ImportPasswordTitle")
        };
        window.HeadingText.Text = T("SessionManager.ImportPasswordTitle");
        window.MessageText.Text = Tf("SessionManager.ImportPasswordMessage", fileName);
        window.HintText.Text = T("SessionManager.ImportPasswordHint");
        window.CancelButton.Content = T("Common.Cancel");
        window.ConfirmButton.Content = T("Common.Ok");
        window.ErrorText.Text = T("SessionManager.ImportPasswordInvalid");
        window.ErrorText.IsVisible = isRetry;
        return await window.ShowDialog<string?>(owner);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Confirm();

    private void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Confirm();
                break;
            case Key.Escape:
                e.Handled = true;
                Close(null);
                break;
        }
    }

    private void Confirm()
    {
        var password = PasswordBox.Text ?? string.Empty;
        if (password.Length == 0)
        {
            ErrorText.Text = T("SessionManager.ImportPasswordEmpty");
            ErrorText.IsVisible = true;
            PasswordBox.Focus();
            return;
        }

        Close(password);
    }
}
