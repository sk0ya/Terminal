using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class DcsDecoderTests
{
    [Fact]
    public void DecodeClassifiesDecrqssAndReturnsRequestToken()
    {
        DcsCommand command = DcsDecoder.Decode("$qm");

        Assert.Equal(DcsCommandKind.Decrqss, command.Kind);
        Assert.Equal("m", command.RequestToken);
    }

    [Fact]
    public void DecodeDoesNotTreatArbitraryQAsSixelIntroducer()
    {
        DcsCommand command = DcsDecoder.Decode("pdataqmore");

        Assert.Equal(DcsCommandKind.Unknown, command.Kind);
    }

    [Fact]
    public void DecodeClassifiesNumericSixelIntroducer()
    {
        DcsCommand command = DcsDecoder.Decode("1;2;3q#0;2;0;0;0");

        Assert.Equal(DcsCommandKind.Sixel, command.Kind);
    }

    [Fact]
    public void DecodeClassifiesSixelWithNoParameters()
    {
        DcsCommand command = DcsDecoder.Decode("q#1;2;100;100;100");

        Assert.Equal(DcsCommandKind.Sixel, command.Kind);
    }

    [Fact]
    public void DecodeClassifiesUnrecognizedSequenceAsUnknown()
    {
        DcsCommand command = DcsDecoder.Decode("1;2!zdata");

        Assert.Equal(DcsCommandKind.Unknown, command.Kind);
        Assert.Null(command.RequestToken);
    }
}
