using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class VtParserTests
{
    [Fact]
    public void OscEscapeFollowedByNonStIsReinterpretedAsEscapeCommand()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, "\u001b]2;discarded\u001b7");

        Assert.Equal(["escape:7"], events);
    }

    [Fact]
    public void DcsTransitionsThroughParamAndIntermediateToCompletedPayload()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, "\u001bP1;2$qpayload\u001b\\");

        Assert.Equal(["dcs:1;2$qpayload"], events);
    }

    [Fact]
    public void ResetDiscardsIncompleteSequence()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);
        Process(parser, "\u001b]2;incomplete");

        parser.Reset();
        parser.Process('\u0007');

        Assert.Equal(["control:7"], events);
        Assert.True(parser.IsNormal);
    }

    [Fact]
    public void C0AndC1SequencesDispatchCallbacksInInputOrder()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, "\u0005\u009b6n\u009d2;title\u009c\u0090$qm\u009c\u0007");

        Assert.Equal(
        [
            "control:5",
            "csi:6:n",
            "osc:2;title",
            "dcs:$qm",
            "control:7"
        ],
        events);
    }

    [Fact]
    public void EightBitDcsIntroducerDispatchesDcsAndStringTerminatorCompletesIt()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, "\u0090$qm\u009cX");

        Assert.Equal(["dcs:$qm", "control:88"], events);
    }

    [Theory]
    [InlineData("\u0098")]
    [InlineData("\u009e")]
    public void UnsupportedEightBitControlStringsAreConsumedUntilSt(string introducer)
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, $"{introducer}hidden\u001b[31mstill-hidden\u009cX");

        Assert.Equal(["control:88"], events);
    }

    [Fact]
    public void UnsupportedControlStringCanUseSevenBitStTerminator()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, "\u0098hidden\u001b\\X");

        Assert.Equal(["control:88"], events);
    }

    [Theory]
    [InlineData('^')]
    [InlineData('X')]
    public void SevenBitUnsupportedControlStringsAreConsumedUntilSt(char introducer)
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, $"\u001b{introducer}hidden\u001b[31mstill-hidden\u001b\\X");

        Assert.Equal(["control:88"], events);
    }

    [Theory]
    [InlineData('\u0018')]
    [InlineData('\u001a')]
    public void CsiCanBeCancelledByCanOrSub(char cancel)
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, $"\u001b[31{cancel}X");

        Assert.Equal(["control:88"], events);
    }

    [Fact]
    public void EscapeInsideCsiResynchronizesToANewEscapeSequence()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, "\u001b[31\u001b]2;title\u0007X");

        Assert.Equal(["osc:2;title", "control:88"], events);
    }

    [Theory]
    [InlineData("\u001b]2;partial", '\u0018')]
    [InlineData("\u001b]2;partial", '\u001a')]
    [InlineData("\u001bP$qpartial", '\u0018')]
    [InlineData("\u001bP$qpartial", '\u001a')]
    public void OscAndDcsCanBeCancelledByCanOrSub(string prefix, char cancel)
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, $"{prefix}{cancel}X");

        Assert.Equal(["control:88"], events);
    }

    [Fact]
    public void OversizedOscPayloadIsDiscardedAndParserReturnsToNormal()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, $"\u001b]0;{new string('a', VtParser.MaxControlStringLength - 2)}");
        Process(parser, "Y");
        parser.Process('X');

        Assert.Equal(["control:88"], events);
    }

    [Fact]
    public void OversizedNonImageDcsPayloadIsDiscardedAndParserReturnsToNormal()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, $"\u001bP${new string('a', VtParser.MaxControlStringLength - 1)}");
        Process(parser, "Y");
        parser.Process('X');

        Assert.Equal(["control:88"], events);
    }

    [Fact]
    public void SixelPayloadIsAllowedPastTheOrdinaryControlStringLimit()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);
        // Sixel rasters routinely run past the 64 KB an ordinary control string is capped at.
        string payload = new('?', VtParser.MaxControlStringLength + 1024);

        Process(parser, $"\u001bPq{payload}\u001b\\");

        Assert.Equal([$"dcs:q{payload}"], events);
    }

    [Theory]
    // Only APC (0x9f / ESC _) carries kitty graphics. SOS and PM stay opaque, and the C1 and
    // ESC forms of each must agree.
    [InlineData("\u0098")]
    [InlineData("\u009e")]
    [InlineData("\u001bX")]
    [InlineData("\u001b^")]
    public void SosAndPmContentIsNotDispatchedAsApc(string introducer)
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, $"{introducer}Ga=T,f=100;payload\u001b\\");
        parser.Process('X');

        Assert.Equal(["control:88"], events);
    }

    [Theory]
    [InlineData("\u009f")]
    [InlineData("\u001b_")]
    public void ApcContentIsDispatched(string introducer)
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);

        Process(parser, $"{introducer}Ga=T,f=100;payload\u001b\\");
        parser.Process('X');

        Assert.Equal(["apc:Ga=T,f=100;payload", "control:88"], events);
    }
    [Fact]
    public void ImageOscPayloadIsAllowedPastTheOrdinaryControlStringLimit()
    {
        var events = new List<string>();
        VtParser parser = CreateParser(events);
        // OSC 1337;File= carries base64 image data and outgrows the ordinary 64 KB budget.
        string payload = new('a', VtParser.MaxControlStringLength + 1024);

        Process(parser, $"\u001b]1337;File=inline=1:{payload}\u0007");

        Assert.Equal([$"osc:1337;File=inline=1:{payload}"], events);
    }
    private static VtParser CreateParser(List<string> events)
    {
        return new VtParser(
            control: ch => events.Add($"control:{(int)ch}"),
            escape: ch => events.Add($"escape:{ch}"),
            csi: (command, parameters) => events.Add($"csi:{parameters}:{command}"),
            osc: payload => events.Add($"osc:{payload}"),
            dcs: payload => events.Add($"dcs:{payload}"),
            charset: (target, designator) => events.Add($"charset:{target}:{designator}"),
            decLineSize: command => events.Add($"line-size:{command}"),
            apc: payload => events.Add($"apc:{payload}"));
    }

    private static void Process(VtParser parser, string input)
    {
        foreach (char ch in input)
        {
            parser.Process(ch);
        }
    }
}
