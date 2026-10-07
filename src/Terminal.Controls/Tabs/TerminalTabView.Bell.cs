using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Terminal.Tabs;

/// <summary>How the terminal answers BEL (0x07).</summary>
public enum TerminalBellStyle
{
    /// <summary>Play the system beep (default).</summary>
    Audible,

    /// <summary>Flash the terminal area briefly instead of beeping.</summary>
    Visual,

    /// <summary>Beep and flash.</summary>
    Both,

    /// <summary>Neither; <see cref="TerminalTabView.BellRang"/> still fires.</summary>
    None
}

public partial class TerminalTabView
{
    private static readonly Duration BellFlashDuration = new(TimeSpan.FromMilliseconds(180));
    private const double BellFlashOpacity = 0.22;

    /// <summary>How BEL is signalled. <see cref="BellRang"/> and <see cref="HasPendingBell"/> are unaffected.</summary>
    public TerminalBellStyle BellStyle { get; set; } = TerminalBellStyle.Audible;

    /// <summary>Settings value ("audible", "visual", "both", "none") to <see cref="TerminalBellStyle"/>.</summary>
    public static TerminalBellStyle ParseBellStyle(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "visual" => TerminalBellStyle.Visual,
            "both" => TerminalBellStyle.Both,
            "none" or "off" => TerminalBellStyle.None,
            _ => TerminalBellStyle.Audible
        };

    private void SignalBell()
    {
        if (BellStyle is TerminalBellStyle.Audible or TerminalBellStyle.Both)
        {
            PlayBell();
        }

        if (BellStyle is TerminalBellStyle.Visual or TerminalBellStyle.Both)
        {
            FlashBell();
        }
    }

    private void FlashBell()
    {
        BellFlashOverlay.Background = new SolidColorBrush(_colorTheme.Foreground);
        var fade = new DoubleAnimation(BellFlashOpacity, 0, BellFlashDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        // Restarting mid-flash is fine: a burst of BELs reads as one flash, not a strobe.
        BellFlashOverlay.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }
}
