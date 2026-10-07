using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class XtwinopsPixelReportTests
{
    private static readonly TerminalPixelMetrics Metrics = new(
        CellWidth: 9.6, CellHeight: 20, TextAreaLeft: 110, TextAreaTop: 140,
        WindowLeft: 100, WindowTop: 100, WindowWidth: 1000, WindowHeight: 720,
        ScreenWidth: 2560, ScreenHeight: 1440);

    private static List<string> Run(string input, TerminalPixelMetrics? metrics)
    {
        var buffer = new AnsiTerminalBuffer(80, 24) { PixelMetricsProvider = () => metrics };
        var emitted = new List<string>();
        buffer.InputSequenceGenerated += (_, text) => emitted.Add(text);
        buffer.Process(input);
        return emitted;
    }

    [Theory]
    [InlineData("\u001b[14t", "\u001b[4;480;768t")]
    [InlineData("\u001b[14;2t", "\u001b[4;720;1000t")]
    [InlineData("\u001b[16t", "\u001b[6;20;10t")]
    [InlineData("\u001b[13t", "\u001b[3;100;100t")]
    [InlineData("\u001b[13;2t", "\u001b[3;110;140t")]
    [InlineData("\u001b[15t", "\u001b[5;1440;2560t")]
    public void ReportsPixelGeometry(string query, string expected)
    {
        Assert.Equal([expected], Run(query, Metrics));
    }

    [Fact]
    public void StaysSilentWithoutMetrics()
    {
        Assert.Empty(Run("\u001b[14t\u001b[16t", null));
    }
}
