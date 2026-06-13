using System.Runtime.ExceptionServices;
using System.Threading;

namespace Terminal.Tests;

/// <summary>
/// Runs a test body on a dedicated STA thread, serialized across all test classes.
/// xUnit runs collections in parallel, and constructing TerminalTabView on two STA
/// threads at once races WPF's BAML loading (System.IO.Packaging is not thread-safe),
/// which made view-constructing tests flaky. The global gate removes that race.
/// </summary>
public static class StaTestRunner
{
    private static readonly object Gate = new();

    public static void Run(Action action)
    {
        lock (Gate)
        {
            ExceptionDispatchInfo? captured = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    captured = ExceptionDispatchInfo.Capture(ex);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            captured?.Throw();
        }
    }
}
