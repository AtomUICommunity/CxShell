using System.Diagnostics;
using AtomUI.Desktop.Controls;
using AtomUI.Theme.Resources;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CxShell.Services;
using CxShell.ViewModels;

namespace CxShell.Views;

internal readonly record struct ExternalLaunchConfirmation(bool Confirmed, bool TrustTarget);

internal static class AtomUiDialogService
{
    public static async Task ShowMessageAsync(
        TopLevel owner,
        string title,
        string message,
        MessageBoxStyle style = MessageBoxStyle.Information)
    {
        await MessageBox.ShowMessageBoxModalAsync(
            new Avalonia.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            },
            options: WithLocalizedButtonText(new MessageBoxOptions
            {
                Title = title,
                Style = style,
                Width = 420,
                MinHeight = 150,
                PlacementTarget = owner as Control
            }),
            topLevel: owner);
    }

    public static async Task<bool> ShowConfirmAsync(
        TopLevel owner,
        string title,
        string message,
        string? okText = null,
        string? cancelText = null)
    {
        var result = await MessageBox.ShowMessageBoxModalAsync(
            new Avalonia.Controls.TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            },
            options: WithLocalizedButtonText(new MessageBoxOptions
            {
                Title = title,
                Style = MessageBoxStyle.Confirm,
                Width = 380,
                MinHeight = 150,
                PlacementTarget = owner as Control,
                OkButtonText = okText,
                CancelButtonText = cancelText
            }),
            topLevel: owner);

        return result is DialogCode.Accepted;
    }

    public static async Task<ExternalLaunchConfirmation> ShowExternalLaunchConfirmAsync(
        TopLevel owner,
        string title,
        string sourceLabel,
        string origin,
        string protocolLabel,
        string protocol,
        string targetLabel,
        string target,
        string credentialLabel,
        bool credentialSupplied,
        string credentialSuppliedText,
        string credentialNoneText,
        string connectText,
        string cancelText,
        string trustText)
    {
        var trustCheckBox = new Avalonia.Controls.CheckBox
        {
            Content = trustText,
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel
        {
            Spacing = 10,
            Width = 470,
            Children =
            {
                CreateSummaryRow(sourceLabel, origin),
                CreateSummaryRow(protocolLabel, protocol),
                CreateSummaryRow(targetLabel, target),
                CreateSummaryRow(credentialLabel, credentialSupplied ? credentialSuppliedText : credentialNoneText),
                trustCheckBox
            }
        };

        var result = await MessageBox.ShowMessageBoxModalAsync(
            content,
            options: WithLocalizedButtonText(new MessageBoxOptions
            {
                Title = title,
                Style = MessageBoxStyle.Confirm,
                Width = 560,
                MinHeight = 250,
                PlacementTarget = owner as Control,
                OkButtonText = connectText,
                CancelButtonText = cancelText
            }),
            topLevel: owner);

        return new ExternalLaunchConfirmation(result is DialogCode.Accepted, trustCheckBox.IsChecked == true);
    }

    private static StackPanel CreateSummaryRow(string label, string value)
    {
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                new Avalonia.Controls.TextBlock
                {
                    Text = label,
                    Width = 90,
                    Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorTextSecondary, Color.Parse("#8C8C8C")))
                },
                new Avalonia.Controls.TextBlock
                {
                    Text = value,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360
                }
            }
        };
    }

    public static async Task ShowAboutAsync(
        TopLevel owner,
        string title,
        string appName,
        string versionText,
        string description,
        string builtWith,
        string githubLabel,
        string githubUrl)
    {
        var link = new Avalonia.Controls.TextBlock
        {
            Text = githubUrl,
            TextWrapping = TextWrapping.Wrap,
            Cursor = new Cursor(StandardCursorType.Hand),
            Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorPrimary, Color.Parse("#1677FF")))
        };
        link.PointerPressed += (_, _) => OpenUrl(githubUrl);

        var content = new StackPanel
        {
            Spacing = 14,
            Width = 430,
            Children =
            {
                new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new Avalonia.Controls.TextBlock
                        {
                            Text = appName,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")))
                        },
                        new Avalonia.Controls.TextBlock
                        {
                            Text = versionText,
                            Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")))
                        }
                    }
                },
                new Avalonia.Controls.TextBlock
                {
                    Text = description,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")))
                },
                new Avalonia.Controls.TextBlock
                {
                    Text = builtWith,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")))
                },
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new Avalonia.Controls.TextBlock
                        {
                            Text = githubLabel,
                            Foreground = new SolidColorBrush(ThemeTokenColorHelper.GetColor(SharedTokenKind.ColorText, Color.Parse("#262626")))
                        },
                        link
                    }
                }
            }
        };

        await MessageBox.ShowMessageBoxModalAsync(
            content,
            options: WithLocalizedButtonText(new MessageBoxOptions
            {
                Title = title,
                Style = MessageBoxStyle.Information,
                Width = 560,
                MinHeight = 260,
                PlacementTarget = owner as Control
            }),
            topLevel: owner);
    }

    internal static MessageBoxOptions WithLocalizedButtonText(MessageBoxOptions options)
    {
        return options with
        {
            OkButtonText = options.OkButtonText ?? LocalizationService.Shared.Text("Common.Ok"),
            CancelButtonText = options.CancelButtonText ?? LocalizationService.Shared.Text("Common.Cancel")
        };
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore shell integration failures; the visible URL can still be copied.
        }
    }
}
