using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExplorerPlus.App.Services;
using ExplorerPlus.App.Views;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Tests;

public sealed class HoverPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ExplorerPlusHover-" + Guid.NewGuid().ToString("N")[..8]);

    public HoverPreviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Text_encodings_are_told_apart()
    {
        Assert.Equal("ASCII", PreviewLoader.ReadText(Write("a.txt", "hello\n"u8.ToArray()), 1024)!.Encoding);
        Assert.Equal("UTF-8", PreviewLoader.ReadText(Write("b.txt", "café"u8.ToArray()), 1024)!.Encoding);
        Assert.Equal("café", PreviewLoader.ReadText(Write("c.txt", [0xEF, 0xBB, 0xBF, .. "café"u8.ToArray()]), 1024)!.Text);

        var utf16 = PreviewLoader.ReadText(Write("d.txt", Encoding.Unicode.GetBytes("hi there")), 1024)!;
        Assert.Equal(("UTF-16", "hi there"), (utf16.Encoding, utf16.Text));
        Assert.Equal("hi", PreviewLoader.ReadText(Write("e.txt", [0xFE, 0xFF, 0, (byte)'h', 0, (byte)'i']), 1024)!.Text);

        // not valid UTF-8: read in the ANSI code page, as Notepad does
        var ansi = PreviewLoader.ReadText(Write("f.txt", [(byte)'c', (byte)'a', (byte)'f', 0xE9]), 1024)!;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal(("ANSI", Encoding.GetEncoding((int)Kernel32.GetACP()).GetString([(byte)'c', (byte)'a', (byte)'f', 0xE9])), (ansi.Encoding, ansi.Text));

        var empty = PreviewLoader.ReadText(Write("g.txt", []), 1024)!;
        Assert.Equal(("", "", 0), (empty.Text, empty.Encoding, empty.Lines));
    }

    [Fact]
    public void Binary_files_are_turned_down()
    {
        var bytes = new byte[4096];
        new Random(1).NextBytes(bytes);
        Assert.Null(PreviewLoader.ReadText(Write("blob", bytes), 4096));
        Assert.Null(PreviewLoader.ReadText(Write("ctrl.txt", Enumerable.Repeat((byte)1, 100).ToArray()), 4096));
    }

    [Fact]
    public void Long_text_is_cut_and_short_text_counts_its_lines()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"line {i}"));
        var cut = PreviewLoader.ReadText(Write("long.log", Encoding.UTF8.GetBytes(lines)), 64 * 1024, 8000, 200)!;
        Assert.True(cut.Truncated);
        Assert.Equal(0, cut.Lines);
        Assert.Equal(200, cut.Text.Split('\n').Length);

        var whole = PreviewLoader.ReadText(Write("short.txt", "a\nb\nc"u8.ToArray()), 1024, 8000, 200)!;
        Assert.Equal((3, false), (whole.Lines, whole.Truncated));
        Assert.Equal(2, PreviewLoader.ReadText(Write("nl.txt", "a\nb\n"u8.ToArray()), 1024)!.Lines);
    }

    [Fact]
    public void A_character_cut_by_the_read_limit_keeps_the_file_utf8()
    {
        var path = Write("cut.txt", Encoding.UTF8.GetBytes(new string('é', 100)));
        var text = PreviewLoader.ReadText(path, 51)!;
        Assert.Equal("UTF-8", text.Encoding);
        Assert.Equal(new string('é', 25), text.Text);
        Assert.True(text.Truncated);
    }

    [Fact]
    public void Kinds_come_from_the_name_and_ts_from_the_bytes()
    {
        Assert.Equal(MediaKind.Image, PreviewLoader.KindOf(@"C:\x\photo.JPG"));
        Assert.Equal(MediaKind.Audio, PreviewLoader.KindOf(@"C:\x\song.flac"));
        Assert.Equal(MediaKind.Text, PreviewLoader.KindOf(@"C:\x\README"));
        Assert.Equal(MediaKind.Text, PreviewLoader.KindOf(@"C:\x\.gitignore"));
        Assert.Equal(MediaKind.None, PreviewLoader.KindOf(@"C:\x\setup.exe"));

        Assert.Equal(MediaKind.Text, PreviewLoader.ResolveKind(Write("app.ts", "export const x = 1;\n"u8.ToArray())));
        var ts = new byte[400];
        ts[0] = ts[188] = 0x47;
        Assert.Equal(MediaKind.Video, PreviewLoader.ResolveKind(Write("clip.ts", ts)));
    }

    [Fact]
    public void Media_uris_keep_odd_characters_in_file_names()
    {
        var path = Write("a #1 100%.mp4", [0]);
        Assert.Equal(path, new Uri(path).LocalPath);
    }

    // a 40×20 photo with a red top-left quadrant, saved with an EXIF orientation
    private string Photo(string name, ushort orientation, int width = 40, int height = 20)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var red = x < width / 2 && y < height / 2;
            var i = (y * width + x) * 4;
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = red ? ((byte)0, (byte)0, (byte)255, (byte)255) : ((byte)255, (byte)0, (byte)0, (byte)255);
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var meta = new BitmapMetadata("jpg");
        meta.SetQuery("/app1/ifd/{ushort=274}", orientation);
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(source, null, meta, null));
        var path = Path.Combine(_dir, name);
        using (var stream = File.Create(path)) encoder.Save(stream);
        return path;
    }

    private static bool IsRed(BitmapSource image, double fx, double fy)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var px = new byte[4];
        converted.CopyPixels(new Int32Rect((int)(image.PixelWidth * fx), (int)(image.PixelHeight * fy), 1, 1), px, 4, 0);
        return px[2] > 180 && px[0] < 80;
    }

    [Theory]
    [InlineData(1, 0.25, 0.25)]
    [InlineData(2, 0.75, 0.25)]
    [InlineData(3, 0.75, 0.75)]
    [InlineData(4, 0.25, 0.75)]
    [InlineData(5, 0.25, 0.25)]
    [InlineData(6, 0.75, 0.25)]
    [InlineData(7, 0.75, 0.75)]
    [InlineData(8, 0.25, 0.75)]
    public void Photos_are_shown_upright_for_every_exif_orientation(ushort orientation, double redX, double redY)
    {
        var decoded = PreviewLoader.DecodeImage(Photo($"o{orientation}.jpg", orientation), 1000, 1000)!;
        var sideways = orientation >= 5;
        Assert.Equal(sideways ? (20, 40) : (40, 20), (decoded.Image.PixelWidth, decoded.Image.PixelHeight));
        Assert.Equal((decoded.Image.PixelWidth, decoded.Image.PixelHeight), (decoded.SourceWidth, decoded.SourceHeight));
        Assert.True(IsRed(decoded.Image, redX, redY), $"orientation {orientation}: red quadrant not at {redX},{redY}");
        Assert.False(IsRed(decoded.Image, 1 - redX, 1 - redY));
    }

    [Fact]
    public void Big_photos_are_decoded_at_the_size_they_are_shown()
    {
        var decoded = PreviewLoader.DecodeImage(Photo("big.jpg", 6, 800, 400), 100, 100)!;
        Assert.Equal((50, 100), (decoded.Image.PixelWidth, decoded.Image.PixelHeight));
        Assert.Equal((400, 800), (decoded.SourceWidth, decoded.SourceHeight));
        Assert.False(decoded.HasAlpha);
        Assert.Null(PreviewLoader.DecodeImage(Write("broken.jpg", [1, 2, 3]), 100, 100));
    }

    [Fact]
    public void Animated_gifs_give_every_frame()
    {
        BitmapSource Solid(byte b) => BitmapSource.Create(30, 10, 96, 96, PixelFormats.Bgr32, null, Enumerable.Repeat(new[] { b, b, b, (byte)255 }, 300).SelectMany(p => p).ToArray(), 120);

        var encoder = new GifBitmapEncoder();
        foreach (var shade in new byte[] { 0, 128, 255 }) encoder.Frames.Add(BitmapFrame.Create(Solid(shade)));
        var path = Path.Combine(_dir, "anim.gif");
        using (var stream = File.Create(path)) encoder.Save(stream);

        var gif = PreviewLoader.DecodeGif(path, 15, 15, CancellationToken.None)!;
        Assert.Equal(3, gif.Frames.Count);
        Assert.Equal((30, 10), (gif.SourceWidth, gif.SourceHeight));
        Assert.All(gif.Frames, f => Assert.Equal((15, 5), (f.PixelWidth, f.PixelHeight)));
        Assert.All(gif.Delays, d => Assert.True(d >= 20));

        var still = new GifBitmapEncoder();
        still.Frames.Add(BitmapFrame.Create(Solid(9)));
        var stillPath = Path.Combine(_dir, "still.gif");
        using (var stream = File.Create(stillPath)) still.Save(stream);
        Assert.Null(PreviewLoader.DecodeGif(stillPath, 15, 15, CancellationToken.None));
    }

    private static HoverPlace Place(int x, int y) => new(x, y, new RECT(0, 0, 1920, 1040), 1, 600, 600);

    [Fact]
    public void The_popup_goes_beside_the_cursor_and_stays_on_screen()
    {
        Assert.Equal((120, 300), Place(100, 500).Position(400, 400));
        // no room on the right: left of the cursor
        Assert.Equal((1880 - 400, 300), Place(1900, 500).Position(400, 400));
        // near the bottom: pushed up to the work area
        Assert.Equal((120, 1040 - 8 - 400), Place(100, 1000).Position(400, 400));
        // too wide for either side: below the cursor
        Assert.Equal((8, 120), Place(100, 100).Position(1900, 300));
    }

    [Fact]
    public void The_popup_measures_to_its_content_without_showing() => RunSta(() =>
    {
        var owner = new Window { Left = -10000, Top = -10000, Width = 200, Height = 200, ShowActivated = false };
        owner.Show();
        var popup = new HoverPopup(owner);
        var place = new HoverPlace(100, 100, new RECT(0, 0, 1920, 1040), 1, 600, 600);
        var text = PreviewLoader.ReadText(Write("m.txt", "x"u8.ToArray()), 1024)!;
        popup.ShowText(place, "m.txt", 1, text);
        Assert.True(popup.IsVisible);
        Assert.True(popup.ActualWidth >= 280);
        popup.Clear();
        Assert.False(popup.IsVisible);
        popup.Close();
        owner.Close();
    });

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }
}
