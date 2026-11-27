using CxShell.Models;

namespace CxShell.Services;

/// <summary>
/// Process wide mirror of the setting that controls the overlays painted inside
/// a session view. Those views sit several levels below the shell and have no
/// reference to the settings object, while the settings page is what owns the
/// value, so both sides meet here instead of threading a settings reference
/// through every tab, detached window and monitor window.
/// </summary>
public static class SessionOverlayPreferences
{
    private static bool _showQuickActions = true;
    private static double? _quickActionsX;
    private static double? _quickActionsY;

    /// <summary>Raised when a value changes so open session views can re-read it.</summary>
    public static event EventHandler? Changed;

    /// <summary>Whether the floating quick action bar is drawn inside a session.</summary>
    public static bool ShowQuickActions
    {
        get => _showQuickActions;
        set
        {
            if (_showQuickActions == value)
                return;

            _showQuickActions = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Horizontal spot of the floating bar, as a fraction of the free area.
    /// Null means "not moved yet", which keeps the default bottom right anchor.
    /// </summary>
    public static double? QuickActionsX
    {
        get => _quickActionsX;
        set
        {
            if (Same(_quickActionsX, value))
                return;

            _quickActionsX = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Vertical counterpart of <see cref="QuickActionsX"/>.</summary>
    public static double? QuickActionsY
    {
        get => _quickActionsY;
        set
        {
            if (Same(_quickActionsY, value))
                return;

            _quickActionsY = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Installed by the shell. Lets a session view persist a change it made
    /// (closing the floating bar) without knowing where settings are stored.
    /// </summary>
    public static Action<bool>? Persist { get; set; }

    /// <summary>
    /// Installed by the shell. Called when the bar is dragged, with the new
    /// fraction pair; both values are null when the bar goes back to its corner.
    /// </summary>
    public static Action<double?, double?>? PersistPosition { get; set; }

    /// <summary>Copies the loaded or edited settings into the mirror.</summary>
    public static void Configure(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ShowQuickActions = settings.ShowSessionQuickActions;
        QuickActionsX = settings.QuickActionsBarX;
        QuickActionsY = settings.QuickActionsBarY;
    }

    /// <summary>Hides the floating bar and remembers the choice.</summary>
    public static void Dismiss()
    {
        ShowQuickActions = false;
        Persist?.Invoke(false);
    }

    /// <summary>Stores where the bar was dropped and remembers the choice.</summary>
    public static void MoveQuickActions(double? x, double? y)
    {
        QuickActionsX = x;
        QuickActionsY = y;
        PersistPosition?.Invoke(x, y);
    }

    /// <summary>Puts the bar back in the corner it starts in and forgets the spot.</summary>
    public static void ResetQuickActionsPosition() => MoveQuickActions(null, null);

    private static bool Same(double? left, double? right) =>
        left is null ? right is null : right is not null && Math.Abs(left.Value - right.Value) < 0.0005;
}
