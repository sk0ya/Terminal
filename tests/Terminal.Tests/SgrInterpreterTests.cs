using System.Windows.Media;

using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class SgrInterpreterTests
{
    [Fact]
    public void ParsePreservesSemicolonTokensAndColonSubParameters()
    {
        SgrParam[] tokens = SgrInterpreter.Parse("1;38:2::12:34:56;4:3");

        Assert.Equal([1, 38, 4], tokens.Select(token => token.Code));
        Assert.Null(tokens[0].Sub);
        Assert.Equal([2, 12, 34, 56], tokens[1].Sub!);
        Assert.Equal([3], tokens[2].Sub!);
    }

    [Fact]
    public void LegacyRgbColorConsumesParametersAndClampsComponents()
    {
        SgrParam[] tokens = SgrInterpreter.Parse("38;2;-1;128;999;1");
        int index = 0;

        bool parsed = SgrInterpreter.TryReadExtendedColor(
            tokens, ref index, Palette(), Colors.Magenta, out Color color);

        Assert.True(parsed);
        Assert.Equal(Color.FromRgb(0, 128, 255), color);
        Assert.Equal(4, index);
        Assert.Equal(1, tokens[index + 1].Code);
    }

    [Fact]
    public void ColonPaletteColorDoesNotConsumeFollowingToken()
    {
        SgrParam[] tokens = SgrInterpreter.Parse("38:5:196;1");
        int index = 0;

        bool parsed = SgrInterpreter.TryReadExtendedColor(
            tokens, ref index, Palette(), Colors.Magenta, out Color color);

        Assert.True(parsed);
        Assert.Equal(Color.FromRgb(255, 0, 0), color);
        Assert.Equal(0, index);
    }

    [Theory]
    [InlineData(16, 0, 0, 0)]
    [InlineData(21, 0, 0, 255)]
    [InlineData(231, 255, 255, 255)]
    [InlineData(232, 8, 8, 8)]
    [InlineData(255, 238, 238, 238)]
    public void ResolveXtermColorMapsCubeAndGrayscale(int index, byte red, byte green, byte blue)
    {
        Color color = SgrInterpreter.ResolveXtermColor(index, Palette(), Colors.Magenta);

        Assert.Equal(Color.FromRgb(red, green, blue), color);
    }

    private static Color[] Palette() =>
    [
        Colors.Black, Colors.Red, Colors.Green, Colors.Yellow,
        Colors.Blue, Colors.Magenta, Colors.Cyan, Colors.White,
        Colors.Gray, Colors.OrangeRed, Colors.LimeGreen, Colors.LightYellow,
        Colors.LightBlue, Colors.Violet, Colors.LightCyan, Colors.Snow
    ];
}
