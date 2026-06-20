using Terminal.Rendering;

namespace Terminal.Tests;

public sealed class SixelDecoderTests
{
    [Fact]
    public void DecodeEmptyBodyReturnsNull()
    {
        Assert.Null(SixelDecoder.Decode(string.Empty));
        Assert.Null(SixelDecoder.Decode("#1;2;100;0;0"));
    }

    [Fact]
    public void DecodeSingleColumnFillsSixVerticalPixels()
    {
        // Define colour register 1 as pure red (RGB percentages), select it, then plot one sixel
        // byte with all six bits set ('~').
        SixelImageData? image = SixelDecoder.Decode("#1;2;100;0;0#1~");

        Assert.NotNull(image);
        Assert.Equal(1, image!.Value.Width);
        Assert.Equal(6, image.Value.Height);

        for (int y = 0; y < 6; y++)
        {
            AssertPixel(image.Value, x: 0, y, r: 255, g: 0, b: 0, a: 255);
        }
    }

    [Fact]
    public void DecodeRunLengthRepeatWidensImage()
    {
        SixelImageData? image = SixelDecoder.Decode("#1;2;0;100;0#1!4~");

        Assert.NotNull(image);
        Assert.Equal(4, image!.Value.Width);
        Assert.Equal(6, image.Value.Height);

        for (int x = 0; x < 4; x++)
        {
            AssertPixel(image.Value, x, y: 2, r: 0, g: 255, b: 0, a: 255);
        }
    }

    [Fact]
    public void DecodeGraphicsLineFeedStartsNewBand()
    {
        // First band: '@' = 0x40-0x3F = bit 0 only (top pixel of band 0 -> y=0).
        // Graphics line feed '-' then '@' again -> top pixel of band 1 -> y=6.
        SixelImageData? image = SixelDecoder.Decode("#1;2;100;100;100#1@-@");

        Assert.NotNull(image);
        Assert.Equal(1, image!.Value.Width);
        Assert.Equal(7, image.Value.Height);

        AssertPixel(image.Value, x: 0, y: 0, r: 255, g: 255, b: 255, a: 255);
        AssertPixel(image.Value, x: 0, y: 6, r: 255, g: 255, b: 255, a: 255);
        // Pixels between the two bands stay transparent.
        AssertPixel(image.Value, x: 0, y: 3, r: 0, g: 0, b: 0, a: 0);
    }

    [Fact]
    public void DecodeHonoursRasterCanvasWidth()
    {
        // Raster attributes declare a 10x6 canvas though only one column is plotted.
        SixelImageData? image = SixelDecoder.Decode("\"1;1;10;6#1;2;100;0;0#1~");

        Assert.NotNull(image);
        Assert.Equal(10, image!.Value.Width);
        Assert.Equal(6, image.Value.Height);
    }

    [Fact]
    public void DecodeUnsetPixelsAreTransparent()
    {
        // '?' (value 0) advances the cursor without plotting, then '~' fills the next column.
        SixelImageData? image = SixelDecoder.Decode("#1;2;100;0;0#1?~");

        Assert.NotNull(image);
        Assert.Equal(2, image!.Value.Width);
        AssertPixel(image.Value, x: 0, y: 0, r: 0, g: 0, b: 0, a: 0);
        AssertPixel(image.Value, x: 1, y: 0, r: 255, g: 0, b: 0, a: 255);
    }

    private static void AssertPixel(SixelImageData image, int x, int y, int r, int g, int b, int a)
    {
        int offset = ((y * image.Width) + x) * 4;
        Assert.Equal(b, image.Bgra[offset]);
        Assert.Equal(g, image.Bgra[offset + 1]);
        Assert.Equal(r, image.Bgra[offset + 2]);
        Assert.Equal(a, image.Bgra[offset + 3]);
    }
}
