using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.Core.Tests;

public class ThumbnailExtractorTests
{
    [Fact]
    public void Straight_alpha_with_white_background_is_cleared_and_premultiplied()
    {
        // BGRA: transparent white, half-transparent white, opaque red
        byte[] p = [255, 255, 255, 0, 255, 255, 255, 128, 0, 0, 255, 255];
        ThumbnailExtractor.Premultiply(p);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 128, 128, 128, 128, 0, 0, 255, 255 }, p);
    }

    [Fact]
    public void Premultiplied_pixels_are_left_alone()
    {
        byte[] p = [0, 0, 0, 0, 64, 64, 64, 128, 10, 20, 30, 255];
        var copy = p.ToArray();
        ThumbnailExtractor.Premultiply(p);
        Assert.Equal(copy, p);
    }

    [Fact]
    public void Folder_images_must_have_a_transparent_outline_and_fill_the_canvas()
    {
        Assert.True(ThumbnailExtractor.IsSaneFolderImage(Image(48, 3, 44)));
        Assert.False(ThumbnailExtractor.IsSaneFolderImage(Image(48, 0, 47)));  // opaque square
        Assert.False(ThumbnailExtractor.IsSaneFolderImage(Image(48, 19, 28))); // tiny folder in the middle
    }

    private static RawImage Image(int size, int from, int to)
    {
        var p = new byte[size * size * 4];
        for (var y = from; y <= to; y++)
            for (var x = from; x <= to; x++)
                p[(y * size + x) * 4 + 3] = 255;
        return new RawImage(size, size, p, true);
    }
}
