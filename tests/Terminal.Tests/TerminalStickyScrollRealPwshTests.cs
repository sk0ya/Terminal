using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Terminal.Sessions;
using Terminal.Tabs;

namespace Terminal.Tests;

/// <summary>
/// Sticky scroll after Ctrl+L, against a real pwsh behind ConPTY. Ctrl+L arrives as ESC[2J, then
/// the new prompt's OSC 133 marks, and only then ESC[H — so a synthetic stream that puts the marks
/// after the cursor move cannot show the mark landing mid-screen and the previous command being
/// pinned over the fresh prompt. Skipped when pwsh is not installed.
/// </summary>
public sealed class TerminalStickyScrollRealPwshTests
{
    [Fact]
    public void CtrlLPutsTheNewPromptAtTheTopWithNothingPinnedOverIt()
    {
        if (!IsOnPath("pwsh.exe"))
        {
            return;
        }

        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            var chunks = new ConcurrentQueue<string>();
            using var session = new ConPtySession(120, 30, ShellIntegration.PrepareLaunch("pwsh.exe -NoLogo -NoProfile"), Path.GetTempPath());
            session.OutputReceived += (_, s) =>
            {
                chunks.Enqueue(s);
                if (s.Contains("\u001b[6n", StringComparison.Ordinal))
                {
                    session.Write("\u001b[1;1R");
                }
            };
            session.Start();
            var navigation = view.CommandNavigationForTests;

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
                    else if (quiet.ElapsedMilliseconds >= 300 && done())
                    {
                        return true;
                    }

                    Thread.Sleep(20);
                }

                return false;
            }

            Assert.True(PumpUntil(() => view.IsShellIntegrationActive, 15000), "pwsh did not start with shell integration");
            foreach (var command in new[] { "echo hi", "1..40 | % { \"row $_\" }" })
            {
                int before = navigation.Commands.Count;
                session.Write(command + "\r");
                Assert.True(PumpUntil(() => navigation.Commands.Count > before, 10000), $"no prompt after {command}");

                before = navigation.Commands.Count;
                session.Write("\u000c");
                Assert.True(PumpUntil(() => navigation.Commands.Count > before, 10000), $"no prompt after Ctrl+L following {command}");

                int top = view.ScreenTopLineForTests;
                Assert.Equal(top, navigation.Commands[^1].PromptLine);
                Assert.Null(navigation.FindStickyCommandLine(top));
            }
        });
    }

    private static bool IsOnPath(string executable) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir.Trim(), executable)));
}
