using Terminal.Buffer;

namespace Terminal.Tests;

/// <summary>
/// <see cref="AnsiTerminalBuffer.GetLogicalTextForAbsoluteLineRange"/>: the text a command's output is
/// read from. A line longer than the screen must come back whole, or a build diagnostic cut at the edge
/// turns into two lines and the second half is parsed as a diagnostic of its own.
/// </summary>
public sealed class AnsiTerminalBufferLogicalTextTests
{
    [Fact]
    public void SoftWrappedRowsAreJoinedIntoOneLine()
    {
        var buffer = new AnsiTerminalBuffer(20, 6, scrollbackLimit: 100);
        var longLine = @"C:\work\deep\src\Program.cs(10,32): error CS1061: 'Calculator' has no 'Mul'";
        buffer.Process(longLine + "\r\nnext\r\n");

        var text = buffer.GetLogicalTextForAbsoluteLineRange(0, int.MaxValue);

        Assert.Equal(longLine + Environment.NewLine + "next", text);
    }

    [Fact]
    public void HardLineBreaksStaySeparateAndTrailingBlankRowsAreDropped()
    {
        var buffer = new AnsiTerminalBuffer(40, 8, scrollbackLimit: 100);
        buffer.Process("one  \r\ntwo\r\n");

        var text = buffer.GetLogicalTextForAbsoluteLineRange(0, int.MaxValue);

        Assert.Equal("one" + Environment.NewLine + "two", text);
    }
}
