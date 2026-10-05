using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>
/// <see cref="TerminalTabView.CommandOutputCaptured"/> against a real pwsh behind ConPTY. Synthetic
/// marker streams put the OSC 133 markers where the shell wrote them; ConPTY does not — it forwards
/// them ahead of the text it paints later, and repaints echoed lines — so only a real session shows
/// whether the cut is right. It runs in a working folder deep enough that the prompt wraps onto a
/// second row, as it does in real use. Skipped when pwsh is not installed.
/// </summary>
public sealed class TerminalCommandOutputRealPwshTests
{
    [Fact]
    public void RealPwshOutputIsCutPerCommand()
    {
        if (!IsOnPath("pwsh.exe"))
        {
            return;
        }

        // Wider than the view's 120 columns so the prompt wraps.
        string deep = Path.Combine(Path.GetTempPath(), "terminal-output-" + Guid.NewGuid().ToString("N"), new string('d', 60), new string('e', 60));
        Directory.CreateDirectory(deep);
        try
        {
            RunInFolder(deep);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(deep))!, recursive: true);
        }
    }

    private static void RunInFolder(string workingDirectory)
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var captured = new List<ShellCommandOutputEventArgs>();
            view.CommandOutputCaptured += (_, e) => captured.Add(e);
            var chunks = new ConcurrentQueue<string>();
            using var session = new ConPtySession(120, 30, ShellIntegration.PrepareLaunch("pwsh.exe -NoLogo -NoProfile"), workingDirectory);
            session.OutputReceived += (_, s) =>
            {
                chunks.Enqueue(s);
                if (s.Contains("\u001b[6n", StringComparison.Ordinal))
                {
                    session.Write("\u001b[1;1R");
                }
            };
            session.Start();

            // Plays the view's settle timer: cut once output has been quiet for the settle delay.
            bool PumpUntil(Func<bool> done, int timeoutMs)
            {
                var total = Stopwatch.StartNew();
                var quiet = Stopwatch.StartNew();
                while (total.ElapsedMilliseconds < timeoutMs)
                {
                    bool any = false;
                    while (chunks.TryDequeue(out var chunk))
                    {
                        view.FeedOutputForTests(chunk);
                        any = true;
                    }

                    if (any)
                    {
                        quiet.Restart();
                    }
                    else if (quiet.Elapsed >= TerminalCommandOutputCoordinator.SettleDelay)
                    {
                        view.SettleCommandOutputForTests();
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
            string[] commands =
            [
                "Write-Output alpha; Write-Output beta",
                "1..60 | % { \"row $_\" }",
                "cmd /c exit 3",
                "Write-Output ('x' * 150)",
            ];
            foreach (var command in commands)
            {
                int before = captured.Count;
                session.Write(command + "\r");
                Assert.True(PumpUntil(() => captured.Count > before, 10000), $"no output captured for {command}");
            }

            string nl = Environment.NewLine;
            Assert.Equal(commands, captured.Select(e => e.CommandLine));
            Assert.Equal("alpha" + nl + "beta", captured[0].Output);
            Assert.Equal(string.Join(nl, Enumerable.Range(1, 60).Select(i => $"row {i}")), captured[1].Output);
            Assert.Equal(3, captured[2].ExitCode);
            Assert.Equal("", captured[2].Output);
            Assert.Equal(new string('x', 150), captured[3].Output.Replace(nl, ""));
        });
    }

    private static bool IsOnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir.Trim(), executable)));
}
