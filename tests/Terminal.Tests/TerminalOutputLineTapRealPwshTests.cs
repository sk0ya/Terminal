using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>
/// <see cref="TerminalTabView.SubscribeOutputLines"/> against a real pwsh behind ConPTY: ConPTY forwards the
/// OSC 133 markers ahead of the text it paints, so only a real session shows that the last lines still
/// arrive before the end is reported, and that a long line wrapped by the screen comes back whole.
/// Skipped when pwsh is not installed.
/// </summary>
public sealed class TerminalOutputLineTapRealPwshTests
{
    [Fact]
    public void RealPwshLinesStreamAndTheEndCarriesTheExitCode()
    {
        if (!IsOnPath("pwsh.exe"))
        {
            return;
        }

        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var chunks = new ConcurrentQueue<string>();
            using var session = new ConPtySession(80, 30, ShellIntegration.PrepareLaunch("pwsh.exe -NoLogo -NoProfile"),
                Path.GetTempPath());
            session.OutputReceived += (_, s) =>
            {
                chunks.Enqueue(s);
                if (s.Contains("\u001b[6n", StringComparison.Ordinal))
                {
                    session.Write("\u001b[1;1R");
                }
            };
            session.Start();

            bool PumpUntil(Func<bool> done, int timeoutMs)
            {
                var total = Stopwatch.StartNew();
                var quiet = Stopwatch.StartNew();
                while (total.ElapsedMilliseconds < timeoutMs)
                {
                    bool any = false;
                    while (chunks.TryDequeue(out var chunk))
                    {
                        view.FeedOutputForTapTests(chunk);
                        any = true;
                    }

                    if (any)
                    {
                        quiet.Restart();
                    }
                    else if (quiet.Elapsed >= TerminalCommandOutputCoordinator.SettleDelay)
                    {
                        view.SettleOutputTapsForTests();
                        if (done())
                        {
                            return true;
                        }
                    }

                    Thread.Sleep(20);
                }

                return false;
            }

            Assert.True(PumpUntil(() => view.IsShellIntegrationActive, 15000), "pwsh did not start with shell integration");

            var lines = new List<string>();
            var finished = new List<int?>();
            int linesWhenTick3Arrived = -1;
            using var subscription = view.SubscribeOutputLines(
                line =>
                {
                    lines.Add(line);
                    if (line == "tick 3") linesWhenTick3Arrived = lines.Count;
                },
                finished.Add);
            session.Write("1..3 | % { \"tick $_\"; Start-Sleep -Milliseconds 300 }; Write-Output ('y' * 150); cmd /c exit 4\r");
            Assert.True(PumpUntil(() => finished.Count > 0, 15000), "the end of the command was not reported");

            Assert.Equal([4], finished);
            foreach (var tick in new[] { "tick 1", "tick 2", "tick 3" })
            {
                Assert.Single(lines, tick);
            }

            Assert.Contains(new string('y', 150), lines);   // wrapped at 80 columns, handed out as one line
            Assert.True(linesWhenTick3Arrived > 0);
            Assert.DoesNotContain(lines, line => line.StartsWith("PS ", StringComparison.Ordinal) && !line.Contains("tick", StringComparison.Ordinal));
        });
    }

    private static bool IsOnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir.Trim(), executable)));
}
