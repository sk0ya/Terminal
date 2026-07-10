using System.IO;
using System.Text;
using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class ConPtyIoPumpTests
{
    [Fact]
    public async Task PumpsUtf8OutputAndWritesTextAndBytes()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream(Encoding.UTF8.GetBytes("hello 日本語"));
        var received = new List<string>();
        var pump = new ConPtyIoPump(input, output);
        await pump.Start(received.Add);
        pump.Write("abc");
        pump.Write([0x1b, 0x5b, 0x41]);
        Assert.Equal("hello 日本語", string.Concat(received));
        Assert.Equal("abc\u001b[A", Encoding.UTF8.GetString(input.ToArray()));
        await pump.DisposeAsync();
        await pump.DisposeAsync();
    }
}
