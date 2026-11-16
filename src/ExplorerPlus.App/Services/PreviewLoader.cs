using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.App.Services;

public enum MediaKind { None, Image, Video, Audio, Text }

public sealed record DecodedImage(BitmapSource Image, int SourceWidth, int SourceHeight, bool HasAlpha);

public sealed record AnimatedImage(IReadOnlyList<BitmapSource> Frames, IReadOnlyList<int> Delays, int SourceWidth, int SourceHeight);

public sealed record TextContent(string Text, string Encoding, int Lines, bool Truncated);

public static class PreviewLoader
{
    private static readonly HashSet<string> ImageExtensions = Set(
        "jpg", "jpeg", "jpe", "jfif", "png", "gif", "bmp", "dib", "tif", "tiff", "ico", "webp", "heic", "heif", "avif", "jxr", "wdp", "dds", "jxl",
        "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2", "raf", "srw", "pef");

    private static readonly HashSet<string> VideoExtensions = Set(
        "mp4", "m4v", "mov", "wmv", "avi", "mkv", "webm", "3gp", "3g2", "mpg", "mpeg", "ts", "m2ts", "mts", "asf");

    private static readonly HashSet<string> AudioExtensions = Set(
        "mp3", "wav", "wma", "m4a", "aac", "flac", "ogg", "oga", "opus", "aif", "aiff", "amr", "ac3", "mka");

    private static readonly HashSet<string> TextExtensions = Set(
        "txt", "text", "log", "md", "markdown", "rst", "csv", "tsv", "json", "jsonc", "xml", "xaml", "yaml", "yml", "toml", "ini", "cfg",
        "conf", "config", "properties", "env", "reg", "inf", "nfo", "srt", "vtt", "sub", "tex", "html", "htm", "css", "scss", "less", "svg",
        "c", "h", "cpp", "cc", "cxx", "hpp", "hxx", "cs", "java", "kt", "kts", "scala", "go", "rs", "swift", "m", "mm", "js", "mjs", "cjs",
        "jsx", "tsx", "vue", "svelte", "py", "pyw", "rb", "php", "pl", "lua", "r", "dart", "sql", "sh", "bash", "zsh", "bat", "cmd", "ps1",
        "psm1", "psd1", "vbs", "vb", "fs", "gradle", "cmake", "mk", "rc", "manifest", "csproj", "vbproj", "fsproj", "vcxproj", "sln", "props",
        "targets", "resx", "gitignore", "gitattributes", "editorconfig", "diff", "patch", "gql", "graphql", "proto", "dockerfile");

    private const long MaxGifCanvas = 40_000_000;
    private const long MaxGifBytes = 48L << 20;

    static PreviewLoader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static HashSet<string> Set(params string[] extensions) =>
        new(extensions.Select(e => "." + e), StringComparer.OrdinalIgnoreCase);

    // by name only; .ts can be a video or TypeScript, which ResolveKind settles
    public static MediaKind KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return MediaKind.Text; // README, LICENSE; binary files are turned down when read
        if (ImageExtensions.Contains(ext)) return MediaKind.Image;
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        if (AudioExtensions.Contains(ext)) return MediaKind.Audio;
        return TextExtensions.Contains(ext) ? MediaKind.Text : MediaKind.None;
    }

    public static MediaKind ResolveKind(string path)
    {
        var kind = KindOf(path);
        if (kind == MediaKind.Video && Path.GetExtension(path).Equals(".ts", StringComparison.OrdinalIgnoreCase) && !LooksLikeMpegTs(path))
            return MediaKind.Text;
        return kind;
    }

    private static bool LooksLikeMpegTs(string path)
    {
        try
        {
            using var stream = OpenRead(path);
            Span<byte> b = stackalloc byte[189];
            return stream.ReadAtLeast(b, b.Length, throwOnEndOfStream: false) == b.Length && b[0] == 0x47 && b[188] == 0x47;
        }
        catch (Exception) { return true; }
    }

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);

    public static (int Width, int Height) Fit(double width, double height, double boxWidth, double boxHeight, double maxUpscale)
    {
        if (width <= 0 || height <= 0 || boxWidth <= 0 || boxHeight <= 0) return (0, 0);
        var s = Math.Min(maxUpscale, Math.Min(boxWidth / width, boxHeight / height));
        return (Math.Max(1, (int)Math.Round(width * s)), Math.Max(1, (int)Math.Round(height * s)));
    }

    // ---------- Images ----------

    // decoded straight at the size it's shown at (JPEG decodes at 1/2, 1/4, 1/8 natively), and upright
    public static DecodedImage? DecodeImage(string path, int maxWidth, int maxHeight)
    {
        try
        {
            using var stream = OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            var (width, height) = (frame.PixelWidth, frame.PixelHeight);
            if (width <= 0 || height <= 0) return null;
            var orientation = Orientation(frame);
            var alpha = HasAlpha(frame);

            var swap = orientation is >= 5 and <= 8;
            var (ow, oh) = swap ? (height, width) : (width, height);
            var (tw, th) = Fit(ow, oh, maxWidth, maxHeight, 1);

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (tw < ow)
            {
                image.DecodePixelWidth = swap ? th : tw;
                image.DecodePixelHeight = swap ? tw : th;
            }
            image.EndInit();

            BitmapSource result = image;
            if (OrientationTransform(orientation) is { } transform) result = new WriteableBitmap(new TransformedBitmap(image, transform));
            result.Freeze();
            return new DecodedImage(result, ow, oh, alpha);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ushort Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o && o is >= 1 and <= 8) return o;
        }
        catch (Exception) { }
        return 1;
    }

    // rotate first, then mirror: that's how EXIF 5 and 7 are defined
    private static Transform? OrientationTransform(ushort orientation) => orientation switch
    {
        2 => new ScaleTransform(-1, 1),
        3 => new RotateTransform(180),
        4 => new ScaleTransform(1, -1),
        5 => new TransformGroup { Children = { new RotateTransform(90), new ScaleTransform(-1, 1) } },
        6 => new RotateTransform(90),
        7 => new TransformGroup { Children = { new RotateTransform(270), new ScaleTransform(-1, 1) } },
        8 => new RotateTransform(270),
        _ => null,
    };

    private static bool HasAlpha(BitmapFrame frame)
    {
        var f = frame.Format;
        if (f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64 ||
            f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float) return true;
        try
        {
            return frame.Palette?.Colors.Any(c => c.A < 255) == true;
        }
        catch (Exception) { return false; }
    }

    // null when it isn't an animated GIF
    public static AnimatedImage? DecodeGif(string path, int maxWidth, int maxHeight, CancellationToken ct)
    {
        try
        {
            using var stream = OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder is not GifBitmapDecoder || decoder.Frames.Count < 2) return null;

            int cw = 0, ch = 0;
            try
            {
                cw = Query(decoder.Metadata, "/logscrw", 0);
                ch = Query(decoder.Metadata, "/logscrh", 0);
            }
            catch (Exception) { }
            if (cw <= 0 || ch <= 0) (cw, ch) = (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
            if (cw <= 0 || ch <= 0 || (long)cw * ch > MaxGifCanvas) return null;

            var (tw, th) = Fit(cw, ch, maxWidth, maxHeight, 1);
            var count = (int)Math.Min(decoder.Frames.Count, Math.Clamp(MaxGifBytes / ((long)tw * th * 4), 1, 300));
            var canvas = new int[cw * ch];
            int[]? saved = null;
            var frames = new List<BitmapSource>(count);
            var delays = new List<int>(count);

            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var frame = decoder.Frames[i];
                var meta = frame.Metadata as BitmapMetadata;
                var left = Query(meta, "/imgdesc/Left", 0);
                var top = Query(meta, "/imgdesc/Top", 0);
                var delay = Query(meta, "/grctlext/Delay", 10);
                var disposal = Query(meta, "/grctlext/Disposal", 0);
                var (fw, fh) = (frame.PixelWidth, frame.PixelHeight);
                var pixels = new int[fw * fh];
                new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0).CopyPixels(pixels, fw * 4, 0);

                if (disposal == 3) saved = (int[])canvas.Clone();
                for (var y = 0; y < fh && top + y < ch; y++)
                for (var x = 0; x < fw && left + x < cw; x++)
                {
                    var px = pixels[y * fw + x];
                    if ((px >>> 24) != 0) canvas[(top + y) * cw + left + x] = px;
                }

                BitmapSource composed = BitmapSource.Create(cw, ch, 96, 96, PixelFormats.Pbgra32, null, canvas, cw * 4);
                if (tw != cw || th != ch) composed = new TransformedBitmap(composed, new ScaleTransform((double)tw / cw, (double)th / ch));
                var output = new WriteableBitmap(composed);
                output.Freeze();
                frames.Add(output);
                delays.Add(delay < 2 ? 100 : delay * 10);

                if (disposal == 2 && left < cw)
                {
                    for (var y = 0; y < fh && top + y < ch; y++)
                        Array.Clear(canvas, (top + y) * cw + left, Math.Min(fw, cw - left));
                }
                else if (disposal == 3 && saved is not null) canvas = saved;
            }
            return new AnimatedImage(frames, delays, cw, ch);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return null;
        }
    }

    private static int Query(BitmapMetadata? meta, string query, int fallback)
    {
        try
        {
            return meta?.GetQuery(query) switch
            {
                byte b => b,
                ushort u => u,
                short s => s,
                uint ui => (int)ui,
                int i => i,
                _ => fallback,
            };
        }
        catch (Exception) { return fallback; }
    }

    // the shell's thumbnail, for formats WPF can't decode itself (no codec, RAW) and album art; STA thread
    public static BitmapSource? ShellThumbnail(string path, int size)
    {
        var raw = ThumbnailExtractor.Get(path, null, size, thumbnailOnly: true);
        if (raw is null) return null;
        var bmp = BitmapSource.Create(raw.Width, raw.Height, 96, 96, raw.HasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, raw.Pixels, raw.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---------- Text ----------

    // null for binary files; UTF-8, UTF-16 (with or without a BOM) and the ANSI code page are told apart
    public static TextContent? ReadText(string path, int maxBytes, int maxChars = int.MaxValue, int maxLines = int.MaxValue)
    {
        byte[] buffer;
        int n;
        bool partial;
        try
        {
            using var stream = OpenRead(path);
            buffer = new byte[(int)Math.Min(maxBytes, stream.Length)];
            n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            partial = stream.Length > n;
        }
        catch (Exception)
        {
            return null;
        }

        var b = buffer.AsSpan(0, n);
        string text;
        string encoding;
        if (b.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            text = DecodeUtf8(b[3..], partial, strict: false) ?? "";
            encoding = "UTF-8";
        }
        else if (b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            text = Utf16(b[2..], bigEndian: false);
            encoding = "UTF-16";
        }
        else if (b.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            text = Utf16(b[2..], bigEndian: true);
            encoding = "UTF-16 BE";
        }
        else
        {
            int zeros = 0, oddZeros = 0;
            for (var i = 0; i < n; i++)
                if (b[i] == 0) { zeros++; if ((i & 1) == 1) oddZeros++; }
            if (zeros > 0)
            {
                // NUL bytes that don't look like UTF-16 text: binary
                if (oddZeros * 10 < n * 4 || oddZeros < zeros * 9 / 10) return null;
                text = Utf16(b, bigEndian: false);
                encoding = "UTF-16";
            }
            else if (DecodeUtf8(b, partial, strict: true) is { } utf8)
            {
                text = utf8;
                encoding = b.IndexOfAnyInRange((byte)0x80, (byte)0xFF) < 0 ? "ASCII" : "UTF-8";
            }
            else
            {
                text = Encoding.GetEncoding((int)Kernel32.GetACP()).GetString(b);
                encoding = "ANSI";
            }
        }

        int controls = 0, probe = Math.Min(text.Length, 4000);
        for (var i = 0; i < probe; i++)
        {
            var c = text[i];
            if (c < 0x20 && c is not ('\t' or '\n' or '\r' or '\f')) controls++;
        }
        if (controls * 50 > probe + 1) return null;

        int lines = 0, end = text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            if (i >= maxChars) { end = i; break; }
            if (text[i] == '\n' && ++lines >= maxLines) { end = i; break; }
        }
        var cut = end < text.Length;
        if (cut) text = text[..end];
        var total = !cut && !partial ? lines + (text.Length == 0 || text[^1] == '\n' ? 0 : 1) : 0;
        return new TextContent(text, text.Length == 0 ? "" : encoding, total, cut || partial);
    }

    private static string Utf16(ReadOnlySpan<byte> bytes, bool bigEndian) =>
        (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(bytes[..(bytes.Length & ~1)]);

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    // the read may have cut a character in half: try a few bytes shorter
    private static string? DecodeUtf8(ReadOnlySpan<byte> bytes, bool partial, bool strict)
    {
        if (!strict) return Encoding.UTF8.GetString(bytes);
        for (var cut = 0; cut <= 3 && cut <= bytes.Length; cut++)
        {
            try
            {
                return StrictUtf8.GetString(bytes[..^cut]);
            }
            catch (DecoderFallbackException)
            {
                if (!partial) break;
            }
        }
        return null;
    }
}
