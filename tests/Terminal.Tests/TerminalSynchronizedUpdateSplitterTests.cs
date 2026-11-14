using Terminal.Buffer;
using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalSynchronizedUpdateSplitterTests
{
    private const string B = "\u001b[?2026h";
    private const string E = "\u001b[?2026l";

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData(B + "frame")]                    // a frame that has not ended: nothing finished to draw
    [InlineData(B + "frame" + E)]                // ends cleanly: processing it whole already draws it
    [InlineData(B + "frame" + E + "trailing")]   // no new frame after the end: still not synchronizing
    public void DoesNotSplitWhenProcessingWholeDrawsTheSame(string text)
    {
        Assert.Equal(-1, TerminalSynchronizedUpdateSplitter.FindSplitAfterCompletedFrame(text));
    }

    [Fact]
    public void SplitsAfterTheFinishedFrameWhenTheNextOneStarts()
    {
        string head = "end of first" + E;
        string text = head + B + "second, partial";

        Assert.Equal(head.Length, TerminalSynchronizedUpdateSplitter.FindSplitAfterCompletedFrame(text));
    }

    [Fact]
    public void SplitsAfterTheLastFinishedFrame()
    {
        string head = B + "one" + E + B + "two" + E;
        string text = head + B + "three, partial";

        Assert.Equal(head.Length, TerminalSynchronizedUpdateSplitter.FindSplitAfterCompletedFrame(text));
    }

    [Fact]
    public void HeadEndsTheUpdateAndTailStartsTheNext()
    {
        var buffer = new AnsiTerminalBuffer(32, 10);
        buffer.Process(B + "first ");
        string text = "done" + E + B + "second";

        int split = TerminalSynchronizedUpdateSplitter.FindSplitAfterCompletedFrame(text);
        bool headEnded = buffer.Process(text[..split]);

        Assert.True(headEnded);
        Assert.False(buffer.SynchronizedUpdateActive);

        buffer.Process(text[split..]);
        Assert.True(buffer.SynchronizedUpdateActive);
    }
}
