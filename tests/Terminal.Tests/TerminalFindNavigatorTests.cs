using Terminal.Tabs;

namespace Terminal.Tests;

public sealed class TerminalFindNavigatorTests
{
    private static IReadOnlyList<TerminalMatch> Matches(params (int line, int column)[] positions)
    {
        var list = new List<TerminalMatch>();
        foreach ((int line, int column) in positions)
        {
            list.Add(new TerminalMatch(line, column, 3, string.Empty));
        }

        return list;
    }

    [Theory]
    [InlineData(0, 3, true, 1)]
    [InlineData(1, 3, true, 2)]
    [InlineData(2, 3, true, 0)] // 末尾から先頭へラップ
    [InlineData(2, 3, false, 1)]
    [InlineData(0, 3, false, 2)] // 先頭から末尾へラップ
    public void AdvanceCyclesWithWrap(int currentIndex, int count, bool forward, int expected)
    {
        Assert.Equal(expected, TerminalFindNavigator.Advance(currentIndex, count, forward));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(3, false)]
    public void AdvanceReturnsMinusOneWhenEmpty(int currentIndex, bool forward)
    {
        Assert.Equal(-1, TerminalFindNavigator.Advance(currentIndex, 0, forward));
    }

    [Theory]
    [InlineData(-1, true, 0)]   // 未確定→順方向は先頭
    [InlineData(-1, false, 2)]  // 未確定→逆方向は末尾
    public void AdvanceFromUnsetPicksEnd(int currentIndex, bool forward, int expected)
    {
        Assert.Equal(expected, TerminalFindNavigator.Advance(currentIndex, 3, forward));
    }

    [Fact]
    public void SeedIndexForwardPicksFirstMatchAtOrAfterPosition()
    {
        IReadOnlyList<TerminalMatch> matches = Matches((2, 4), (5, 0), (5, 10), (9, 2));

        // (5,0) 以降で最初は index 1。
        Assert.Equal(1, TerminalFindNavigator.SeedIndex(matches, 5, 0, forward: true));
        // 同一行・列の途中（5,3）以降で最初は (5,10) の index 2。
        Assert.Equal(2, TerminalFindNavigator.SeedIndex(matches, 5, 3, forward: true));
        // 先頭より前（0,0）なら index 0。
        Assert.Equal(0, TerminalFindNavigator.SeedIndex(matches, 0, 0, forward: true));
    }

    [Fact]
    public void SeedIndexForwardWrapsWhenPositionPastLastMatch()
    {
        IReadOnlyList<TerminalMatch> matches = Matches((2, 4), (5, 0));

        Assert.Equal(0, TerminalFindNavigator.SeedIndex(matches, 99, 0, forward: true));
    }

    [Fact]
    public void SeedIndexBackwardPicksLastMatchAtOrBeforePosition()
    {
        IReadOnlyList<TerminalMatch> matches = Matches((2, 4), (5, 0), (5, 10), (9, 2));

        Assert.Equal(2, TerminalFindNavigator.SeedIndex(matches, 5, 10, forward: false));
        Assert.Equal(1, TerminalFindNavigator.SeedIndex(matches, 5, 5, forward: false));
        // 先頭より前（0,0）なら末尾へラップ。
        Assert.Equal(3, TerminalFindNavigator.SeedIndex(matches, 0, 0, forward: false));
    }

    [Fact]
    public void SeedIndexReturnsMinusOneWhenEmpty()
    {
        Assert.Equal(-1, TerminalFindNavigator.SeedIndex(Matches(), 0, 0, forward: true));
    }

    [Theory]
    [InlineData(0, 17, "1/17")]
    [InlineData(2, 17, "3/17")]
    [InlineData(16, 17, "17/17")]
    [InlineData(0, 0, "0/0")]
    [InlineData(-1, 5, "1/5")]  // 範囲外はクランプ
    [InlineData(9, 5, "5/5")]   // 範囲外はクランプ
    public void FormatPositionRendersOneBasedCountOrZero(int currentIndex, int count, string expected)
    {
        Assert.Equal(expected, TerminalFindNavigator.FormatPosition(currentIndex, count));
    }
}
