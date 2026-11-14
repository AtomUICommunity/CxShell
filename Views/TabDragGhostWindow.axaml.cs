using System;
using Avalonia;
using Avalonia.Controls;

namespace CxShell.Views;

/// <summary>
/// A tiny click-through top-level window used as the drag preview while a tab
/// is being pulled out of its tab strip. It has to be a real window instead of
/// an in-window overlay so it stays visible above the title bar and outside the
/// main window bounds.
/// </summary>
public partial class TabDragGhostWindow : Window
{
    private const double GhostWidth = 180;
    private const double GhostHeight = 34;

    public TabDragGhostWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
    }

    public void ShowAt(string? title, Window owner, Point anchorInOwner)
    {
        if (TabDragGhostTitle != null &&
            !string.Equals(TabDragGhostTitle.Text, title, StringComparison.Ordinal))
        {
            TabDragGhostTitle.Text = title;
        }

        var ownerScaling = owner.RenderScaling <= 0 ? 1 : owner.RenderScaling;
        var screenPoint = new PixelPoint(
            owner.Position.X + (int)Math.Round(anchorInOwner.X * ownerScaling),
            owner.Position.Y + (int)Math.Round(anchorInOwner.Y * ownerScaling));
        var scaling = owner.Screens.ScreenFromPoint(screenPoint)?.Scaling ?? ownerScaling;

        Position = new PixelPoint(
            screenPoint.X - (int)Math.Round(GhostWidth * scaling / 2),
            screenPoint.Y - (int)Math.Round(GhostHeight * scaling / 2));

        if (!IsVisible)
            Show();
    }
}
