using Terminal.Buffer;

namespace Terminal.Tests;

public sealed class SixelDecoderTests
{
    [Fact]
    public void DecodesRgbColorRegisters()
    {
        // #Pc;2;Px;Py;Pz is the RGB form; its three components follow the mode directly.
        Assert.True(SixelDecoder.TryDecode("#0;2;100;0;0~", out byte[] pixels, out int width, out int height));

        Assert.Equal(1, width);
        Assert.Equal(6, height);
        Assert.Equal([0, 0, 255, 255], pixels[..4]);
    }

    [Fact]
    public void DecodesPayloadsWithoutRasterAttributes()
    {
        // Raster attributes are optional, so the canvas grows one pixel column at a time. Growing
        // by an exact fit each time would make this quadratic in both allocations and copying.
        string payload = "#0;2;100;100;100" + new string('~', 300);

        Assert.True(SixelDecoder.TryDecode(payload, out byte[] pixels, out int width, out int height));

        Assert.Equal(300, width);
        Assert.Equal(6, height);
        Assert.Equal([255, 255, 255, 255], pixels[..4]);
        Assert.Equal([255, 255, 255, 255], pixels[((5 * 300 + 299) * 4)..]);
    }

    [Fact]
    public void LateRasterAttributesDoNotTruncateDecodedContent()
    {
        // A raster header is allowed to appear after pixel data; shrinking the canvas to match it
        // would discard everything already decoded to the right of the new width.
        string payload = "#0;2;100;100;100" + new string('~', 100) + '"' + "1;1;10;10";

        Assert.True(SixelDecoder.TryDecode(payload, out _, out int width, out int height));

        Assert.Equal(100, width);
        Assert.Equal(6, height);
    }

    [Fact]
    public void CarriageReturnAndLineFeedPositionSubsequentBands()
    {
        // '$' returns to column 0, '-' also advances one six-pixel band.
        string payload = "#0;2;100;100;100~~$~-~";

        Assert.True(SixelDecoder.TryDecode(payload, out _, out int width, out int height));

        Assert.Equal(2, width);
        Assert.Equal(12, height);
    }

    [Fact]
    public void RepeatIntroducerExpandsASingleSixel()
    {
        Assert.True(SixelDecoder.TryDecode("#0;2;100;100;100!5~", out _, out int width, out int height));

        Assert.Equal(5, width);
        Assert.Equal(6, height);
    }
}
