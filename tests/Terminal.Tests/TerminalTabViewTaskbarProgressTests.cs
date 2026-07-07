using System.Reflection;

using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalTabViewTaskbarProgressTests
{
    [Fact]
    public void ProgressEventPublishesUpdatedCurrentStateFromView()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            object? sender = null;
            TaskbarProgressChangedEventArgs? args = null;
            TaskbarProgressState observedCurrentState = TaskbarProgressState.None;
            int observedCurrentProgress = 0;
            view.TaskbarProgressChanged += (eventSender, eventArgs) =>
            {
                sender = eventSender;
                args = eventArgs;
                observedCurrentState = view.CurrentTaskbarProgressState;
                observedCurrentProgress = view.CurrentTaskbarProgress;
            };

            view.FeedOutputForTests("\u001b]9;4;2;73\u0007");

            Assert.Same(view, sender);
            Assert.NotNull(args);
            Assert.Equal(TaskbarProgressState.Error, args!.State);
            Assert.Equal(73, args.Progress);
            Assert.Equal(args.State, observedCurrentState);
            Assert.Equal(args.Progress, observedCurrentProgress);
        });
    }

    [Fact]
    public void ReplacingTerminalBufferClearsCurrentProgressAndNotifiesObserver()
    {
        StaTestRunner.Run(() =>
        {
            var view = new TerminalTabView("cmd.exe", Environment.CurrentDirectory);
            view.FeedOutputForTests("\u001b]9;4;4;42\u0007");
            TaskbarProgressChangedEventArgs? args = null;
            TaskbarProgressState observedCurrentState = TaskbarProgressState.Warning;
            int observedCurrentProgress = 42;
            view.TaskbarProgressChanged += (_, eventArgs) =>
            {
                args = eventArgs;
                observedCurrentState = view.CurrentTaskbarProgressState;
                observedCurrentProgress = view.CurrentTaskbarProgress;
            };

            MethodInfo replaceTerminalBuffer = typeof(TerminalTabView).GetMethod(
                "ReplaceTerminalBuffer",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            replaceTerminalBuffer.Invoke(view, [new AnsiTerminalBuffer(80, 24)]);

            Assert.NotNull(args);
            Assert.Equal(TaskbarProgressState.None, args!.State);
            Assert.Equal(0, args.Progress);
            Assert.Equal(TaskbarProgressState.None, observedCurrentState);
            Assert.Equal(0, observedCurrentProgress);
            Assert.Equal(TaskbarProgressState.None, view.CurrentTaskbarProgressState);
            Assert.Equal(0, view.CurrentTaskbarProgress);
        });
    }
}
