using System.IO.Compression;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.Core.Tests;

public class ArchiveTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("epp-zip-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private string MakeFolder(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllText(Path.Combine(dir, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(dir, "inner", "empty"));
        File.WriteAllText(Path.Combine(dir, "inner", "b.txt"), "beta");
        return dir;
    }

    [Theory]
    [InlineData(ArchiveFormat.Zip)]
    [InlineData(ArchiveFormat.SevenZip)]
    [InlineData(ArchiveFormat.TarGz)]
    public async Task Compress_then_extract_round_trips_a_folder(ArchiveFormat format)
    {
        var source = MakeFolder("Photos");
        var name = Archives.SuggestName([source], format);
        Assert.Equal("Photos" + Archives.Extension(format), name);
        var archive = Path.Combine(_root, name);

        await Archives.CompressAsync([source], archive, format);
        Assert.True(File.Exists(archive));
        Assert.False(File.GetAttributes(archive).HasFlag(FileAttributes.Hidden));
        Assert.Empty(Directory.GetFiles(_root, "*.partial", new EnumerationOptions { AttributesToSkip = 0 }));

        Directory.Delete(source, true);
        var extracted = await Archives.ExtractAsync(archive);

        // One top-level folder in the archive: it lands directly next to the archive, no extra nesting.
        Assert.Equal(source, extracted, ignoreCase: true);
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(extracted, "a.txt")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(extracted, "inner", "b.txt")));
        Assert.True(Directory.Exists(Path.Combine(extracted, "inner", "empty")));
        Assert.Empty(Directory.GetDirectories(_root, ".extracting-*", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Fact]
    public async Task Extracting_several_top_level_items_makes_a_folder_named_after_the_archive()
    {
        var a = Path.Combine(_root, "one.txt");
        var b = Path.Combine(_root, "two.txt");
        File.WriteAllText(a, "1");
        File.WriteAllText(b, "2");
        var archive = Path.Combine(_root, "notes.tar.gz");
        await Archives.CompressAsync([a, b], archive, ArchiveFormat.TarGz);

        var extracted = await Archives.ExtractAsync(archive);

        Assert.Equal(Path.Combine(_root, "notes"), extracted, ignoreCase: true);
        Assert.Equal("1", File.ReadAllText(Path.Combine(extracted, "one.txt")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(extracted, "two.txt")));
    }

    [Fact]
    public async Task Extract_never_overwrites_an_existing_item()
    {
        var file = Path.Combine(_root, "report.txt");
        File.WriteAllText(file, "new");
        var archive = Path.Combine(_root, "report.zip");
        await Archives.CompressAsync([file], archive, ArchiveFormat.Zip);
        File.WriteAllText(file, "keep me");

        var extracted = await Archives.ExtractAsync(archive);

        Assert.Equal("keep me", File.ReadAllText(file));
        Assert.Equal(Path.Combine(_root, "report"), extracted, ignoreCase: true);
        Assert.Equal("new", File.ReadAllText(Path.Combine(extracted, "report.txt")));
    }

    [Fact]
    public async Task Zip_with_a_path_escaping_the_destination_is_refused_and_leaves_nothing()
    {
        var archive = Path.Combine(_root, "evil.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry("../escaped.txt").Open());
            w.Write("x");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => Archives.ExtractAsync(archive));

        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")));
        Assert.Equal([archive], Directory.GetFileSystemEntries(_root, "*", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    [Theory]
    [InlineData(@"C:\x\photos.tar.gz", "photos")]
    [InlineData(@"C:\x\photos.zip", "photos")]
    [InlineData(@"C:\x\v1.2.7z", "v1.2")]
    public void Base_name_strips_double_extensions(string path, string expected) => Assert.Equal(expected, Archives.BaseName(path));

    [Theory]
    [InlineData("a.zip", true)]
    [InlineData("a.TAR.GZ", true)]
    [InlineData("a.rar", true)]
    [InlineData("a.txt", false)]
    public void Recognises_extractable_archives(string name, bool expected) => Assert.Equal(expected, Archives.IsExtractable(name));
}
