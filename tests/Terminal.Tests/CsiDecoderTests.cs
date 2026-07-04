using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class CsiDecoderTests
{
    [Fact]
    public void DecodeSeparatesPrivatePrefixAndParameters()
    {
        CsiCommand command = CsiDecoder.Decode('h', "?25;1049");

        Assert.Equal('h', command.Final);
        Assert.Equal('?', command.Prefix);
        Assert.True(command.IsPrivate);
        Assert.False(command.IsSecondary);
        Assert.Equal([25, 1049], command.Parameters);
        Assert.Equal("25;1049", command.ParameterText);
    }

    [Fact]
    public void DecodeSeparatesIntermediateSuffixFromParameters()
    {
        CsiCommand command = CsiDecoder.Decode('p', "?6$");

        Assert.Equal('?', command.Prefix);
        Assert.Equal("$", command.Intermediate);
        Assert.Equal("6", command.ParameterText);
        Assert.Equal([6], command.Parameters);
    }

    [Fact]
    public void DecodePreservesEmptySemicolonParameter()
    {
        CsiCommand command = CsiDecoder.Decode('m', "1;;31");

        Assert.Equal(3, command.Parameters.Length);
        Assert.Equal(1, command.Parameters[0]);
        Assert.Null(command.Parameters[1]);
        Assert.Equal(31, command.Parameters[2]);
    }

    [Fact]
    public void DecodeRecognizesSecondaryPrefix()
    {
        CsiCommand command = CsiDecoder.Decode('u', ">3;2");

        Assert.Equal('>', command.Prefix);
        Assert.True(command.IsSecondary);
        Assert.Equal([3, 2], command.Parameters);
        Assert.Equal(">3;2", command.RawParameters);
    }

    [Theory]
    [InlineData('A', "<5")]
    [InlineData('m', "=5")]
    public void LessThanAndEqualsRemainInLegacyParameterText(char final, string rawParameters)
    {
        CsiCommand command = CsiDecoder.Decode(final, rawParameters);

        Assert.Equal(rawParameters[0], command.Prefix);
        Assert.Equal(rawParameters, command.ParameterText);
        Assert.Single(command.Parameters);
        Assert.Null(command.Parameters[0]);
    }
}
