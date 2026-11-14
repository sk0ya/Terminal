using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;

namespace Terminal.Tabs;

/// <summary>
/// Diagnostic log for input-to-screen latency. Only active when TERMINAL_LATENCY_TRACE=1; writes
/// "elapsed-ms tab event" lines to %TEMP%\terminal-latency-&lt;pid&gt;.log. The UI thread only enqueues;
/// a dedicated thread does the file I/O.
/// </summary>
internal static class TerminalLatencyTrace
{
    public static readonly bool Enabled = string.Equals(
        Environment.GetEnvironmentVariable("TERMINAL_LATENCY_TRACE"), "1", StringComparison.Ordinal);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static BlockingCollection<string>? _queue;

    public static void Write(object tab, string message)
    {
        if (!Enabled)
        {
            return;
        }

        string line = $"{Clock.Elapsed.TotalMilliseconds,10:F1} {RuntimeHelpers.GetHashCode(tab):X8} {message}";
        EnsureWriter().TryAdd(line);
    }

    private static BlockingCollection<string> EnsureWriter()
    {
        if (_queue is { } existing)
        {
            return existing;
        }

        lock (Gate)
        {
            if (_queue is not null)
            {
                return _queue;
            }

            var queue = new BlockingCollection<string>(boundedCapacity: 100_000);
            string path = Path.Combine(Path.GetTempPath(), $"terminal-latency-{Environment.ProcessId}.log");
            var thread = new Thread(() =>
            {
                try
                {
                    using var writer = new StreamWriter(path, append: true);
                    foreach (string line in queue.GetConsumingEnumerable())
                    {
                        writer.WriteLine(line);
                        if (queue.Count == 0)
                        {
                            writer.Flush();
                        }
                    }
                }
                catch (IOException)
                {
                }
            })
            {
                IsBackground = true,
                Name = "TerminalLatencyTrace"
            };
            thread.Start();
            _queue = queue;
            return queue;
        }
    }
}
