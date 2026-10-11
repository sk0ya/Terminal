using System.Windows.Threading;

namespace Terminal.Tabs;

public partial class TerminalTabView
{
    private readonly List<TerminalOutputLineTap> _outputTaps = [];
    private DispatcherTimer? _outputTapSettleTimer;

    /// <summary>
    /// Streams this terminal's output line by line from now on, without taking the shell the way
    /// <see cref="RunCommandAsync"/> does — so a long-running command (a watch build, a dev server) can be
    /// followed while the human keeps using the terminal and other <see cref="RunCommandAsync"/> callers are
    /// not queued behind it.
    /// <para><paramref name="lineReceived"/> gets each logical line once it is finished (the cursor has moved
    /// past it); rows soft-wrapped at the screen width are joined back into one line. The line the cursor is
    /// on is held back while it may still change. <paramref name="commandFinished"/> fires once, when the
    /// first command that starts after this call ends (OSC 133 D, after its trailing output has settled),
    /// with its exit code — or with null when the session goes away. No callbacks follow it.</para>
    /// <para>Call on the UI thread; callbacks run on it too. Dispose to stop early.</para>
    /// </summary>
    public IDisposable SubscribeOutputLines(Action<string> lineReceived, Action<int?>? commandFinished = null)
    {
        ArgumentNullException.ThrowIfNull(lineReceived);
        Dispatcher.VerifyAccess();

        var tap = new TerminalOutputLineTap(lineReceived, commandFinished);
        tap.Start(_terminalBuffer);
        _outputTaps.Add(tap);
        return new OutputTapSubscription(this, tap);
    }

    /// <summary>After each processed batch: hand out the lines that have finished.</summary>
    private void PumpOutputTaps()
    {
        if (_outputTaps.Count == 0)
        {
            return;
        }

        bool settling = false;
        foreach (TerminalOutputLineTap tap in _outputTaps.ToArray())
        {
            tap.Pump(_terminalBuffer);
            settling |= tap.IsSettling;
        }

        RemoveEndedOutputTaps();
        if (settling)
        {
            RestartOutputTapSettleTimer();
        }
    }

    private void OnOutputTapShellZone(Terminal.Buffer.ShellCommandZoneEventArgs e)
    {
        if (_outputTaps.Count == 0)
        {
            return;
        }

        bool settling = false;
        foreach (TerminalOutputLineTap tap in _outputTaps.ToArray())
        {
            tap.OnShellZone(_terminalBuffer, e);
            settling |= tap.IsSettling;
        }

        if (settling)
        {
            RestartOutputTapSettleTimer();
        }
    }

    /// <summary>Around a change that rebuilds the line numbering (reflow, scrollback clear): read what has
    /// finished under the old numbering first, then restart from the cursor under the new one.</summary>
    private void RenumberOutputTaps(Action change)
    {
        PumpOutputTaps();
        change();
        foreach (TerminalOutputLineTap tap in _outputTaps)
        {
            tap.Resync(_terminalBuffer);
        }
    }

    private void AbortOutputTaps()
    {
        _outputTapSettleTimer?.Stop();
        foreach (TerminalOutputLineTap tap in _outputTaps.ToArray())
        {
            tap.Abort();
        }

        _outputTaps.Clear();
    }

    private void RestartOutputTapSettleTimer()
    {
        if (_outputTapSettleTimer is null)
        {
            _outputTapSettleTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TerminalCommandOutputCoordinator.SettleDelay,
            };
            _outputTapSettleTimer.Tick += OutputTapSettleTimer_Tick;
        }

        _outputTapSettleTimer.Stop();
        _outputTapSettleTimer.Start();
    }

    private void OutputTapSettleTimer_Tick(object? sender, EventArgs e)
    {
        _outputTapSettleTimer?.Stop();
        foreach (TerminalOutputLineTap tap in _outputTaps.ToArray())
        {
            tap.Settle(_terminalBuffer);
        }

        RemoveEndedOutputTaps();
    }

    private void RemoveEndedOutputTaps() => _outputTaps.RemoveAll(tap => tap.IsEnded);

    private void Unsubscribe(TerminalOutputLineTap tap)
    {
        tap.End();
        if (Dispatcher.CheckAccess())
        {
            _outputTaps.Remove(tap);
        }
        else
        {
            _ = Dispatcher.BeginInvoke(() => _outputTaps.Remove(tap));
        }
    }

    /// <summary>Feeds raw PTY output and pumps the taps, as a flush does; test seam.</summary>
    internal void FeedOutputForTapTests(string data)
    {
        _terminalBuffer.Process(data);
        PumpOutputTaps();
    }

    /// <summary>Plays the taps' settle timer; test seam.</summary>
    internal void SettleOutputTapsForTests() => OutputTapSettleTimer_Tick(null, EventArgs.Empty);

    private sealed class OutputTapSubscription(TerminalTabView owner, TerminalOutputLineTap tap) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Unsubscribe(tap);
            }
        }
    }
}
