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

        Process(parser, "\u0005\u009b6n\u009d2;title\u009c\u009f$qm\u009c\u0007");

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

    private static VtParser CreateParser(List<string> events)
    {
        return new VtParser(
            control: ch => events.Add($"control:{(int)ch}"),
            escape: ch => events.Add($"escape:{ch}"),
            csi: (command, parameters) => events.Add($"csi:{parameters}:{command}"),
            osc: payload => events.Add($"osc:{payload}"),
            dcs: payload => events.Add($"dcs:{payload}"),
            charset: (target, designator) => events.Add($"charset:{target}:{designator}"),
            decLineSize: command => events.Add($"line-size:{command}"));
    }

    private static void Process(VtParser parser, string input)
    {
        foreach (char ch in input)
        {
            parser.Process(ch);
        }
    }
}
