using Terminal.Buffer;

namespace Terminal.Tests;

/// <summary>デスクトップ通知（OSC 9 の本文・OSC 777;notify;題;本文）。</summary>
public sealed class TerminalDesktopNotificationTests
{
    [Fact]
    public void Osc777Notify_RaisesTitleAndBody()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        TerminalDesktopNotification? received = null;
        string? legacy = null;
        buffer.DesktopNotificationRequested += (_, n) => received = n;
        buffer.NotificationRequested += (_, m) => legacy = m;

        buffer.Process("\u001b]777;notify;Build;done; 3 warnings\u0007");

        Assert.Equal(new TerminalDesktopNotification("Build", "done; 3 warnings"), received);
        Assert.Equal("Build: done; 3 warnings", legacy);
    }

    [Fact]
    public void Osc777OtherSubcommands_AreIgnored()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        bool raised = false;
        buffer.DesktopNotificationRequested += (_, _) => raised = true;

        buffer.Process("\u001b]777;preexec;ls\u0007");

        Assert.False(raised);
    }

    [Fact]
    public void Osc9_RaisesBodyWithoutTitle()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        TerminalDesktopNotification? received = null;
        buffer.DesktopNotificationRequested += (_, n) => received = n;

        buffer.Process("\u001b]9;Tests passed\u0007");

        Assert.Equal(new TerminalDesktopNotification(null, "Tests passed"), received);
    }

    [Fact]
    public void ControlCharactersAndLongText_AreCleaned()
    {
        var notification = TerminalDesktopNotification.Create(" a\tb ", new string('x', 1000));

        Assert.NotNull(notification);
        Assert.Equal("a b", notification!.Title);
        Assert.Equal(TerminalDesktopNotification.MaxLength, notification.Body.Length);
        Assert.Null(TerminalDesktopNotification.Create("  ", null));
    }

    [Fact]
    public void ReplayedNotifications_AreNotRaised()
    {
        var buffer = new AnsiTerminalBuffer(80, 10);
        bool raised = false;
        buffer.DesktopNotificationRequested += (_, _) => raised = true;

        buffer.Process("\u001b]7770;begin\u0007\u001b]9;old\u0007\u001b]7770;end\u0007");

        Assert.False(raised);
    }
}
