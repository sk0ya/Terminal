namespace Terminal.Tabs;

internal sealed record TerminalAgentCommandHost(
    Func<bool> HasSession,
    Func<string> ActiveCommandLine,
    Func<int> CurrentAbsoluteLine,
    Func<string, bool> SendInput,
    Action Interrupt,
    Func<int, string> CaptureFrom,
    Action<Action> Dispatch);

internal interface ITerminalAgentTimeoutScheduler
{
    IDisposable Schedule(TimeSpan delay, Action callback);
}

internal sealed class TerminalAgentTimeoutScheduler : ITerminalAgentTimeoutScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var source = new CancellationTokenSource(delay);
        CancellationTokenRegistration registration = source.Token.Register(callback);
        return new TimeoutRegistration(source, registration);
    }

    private sealed class TimeoutRegistration(
        CancellationTokenSource source,
        CancellationTokenRegistration registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Dispose();
            source.Dispose();
        }
    }
}

internal sealed class TerminalAgentCommandOrchestrator(
    TerminalAgentCommandCoordinator coordinator,
    TerminalAgentCommandHost host,
    ITerminalAgentTimeoutScheduler timeoutScheduler,
    TimeSpan timeout)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<TerminalCommandResult> RunAsync(
        string command,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!host.HasSession() || cancellationToken.IsCancellationRequested)
            {
                return Incomplete(command);
            }

            bool useShellIntegration = coordinator.ShellIntegrationObserved;
            string lineToSend;
            string markerId = string.Empty;
            int outputStartLine = -1;
            if (useShellIntegration)
            {
                lineToSend = command;
            }
            else
            {
                AgentShellKind shell = AgentCommandProtocol.DetectShellKind(host.ActiveCommandLine());
                if (shell == AgentShellKind.Unknown)
                {
                    return Incomplete(command);
                }

                markerId = AgentCommandProtocol.NewMarkerId();
                lineToSend = AgentCommandProtocol.BuildSentinelCommand(shell, command, markerId);
                outputStartLine = host.CurrentAbsoluteLine();
            }

            TerminalAgentCommandExecution execution =
                coordinator.Begin(command, markerId, outputStartLine);
            using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(() =>
                host.Dispatch(() => Cancel(execution)));
            using IDisposable timeoutRegistration = timeoutScheduler.Schedule(
                timeout,
                () => host.Dispatch(() => Timeout(execution)));

            if (!host.SendInput(lineToSend + "\r"))
            {
                coordinator.Abandon(execution);
                return Incomplete(command);
            }

            return await execution.Completion.Task.ConfigureAwait(true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Cancel(TerminalAgentCommandExecution execution)
    {
        coordinator.Cancel(execution, host.Interrupt, host.CaptureFrom);
    }

    public void Timeout(TerminalAgentCommandExecution execution)
    {
        coordinator.Timeout(execution, host.CaptureFrom);
    }

    private static TerminalCommandResult Incomplete(string command) =>
        new(command, string.Empty, -1, false);
}
