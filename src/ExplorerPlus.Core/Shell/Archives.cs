using System.Diagnostics;
using System.IO.Compression;

namespace ExplorerPlus.Core.Shell;

public enum ArchiveFormat { Zip, SevenZip, Tar, TarGz }

/// <summary>
/// Compress and extract without leaving the window. ZIP uses .NET's own implementation; every other format goes
/// through the libarchive-based tar.exe that ships with Windows (7z, RAR, tar.gz/xz/zst, cab…), the same engine
/// Explorer uses for them since Windows 11 23H2.
/// </summary>
public static class Archives
{
    private static readonly string[] ExtractableSuffixes =
    [
        ".zip", ".7z", ".rar", ".tar", ".tgz", ".tar.gz", ".tbz", ".tbz2", ".tar.bz2", ".txz", ".tar.xz",
        ".tzst", ".tar.zst", ".cab", ".iso", ".lzh", ".lha", ".cpio", ".xar",
    ];

    /// <summary>Windows' own tar.exe (bsdtar). Never the one on PATH: Git for Windows ships GNU tar, which can't read zip or 7z.</summary>
    public static string TarExe { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");

    public static bool IsExtractable(string? path) =>
        path is not null && ExtractableSuffixes.Any(s => path.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    public static string Extension(ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => ".7z",
        ArchiveFormat.Tar => ".tar",
        ArchiveFormat.TarGz => ".tar.gz",
        _ => ".zip",
    };

    public static string Label(ArchiveFormat format) => format switch
    {
        ArchiveFormat.SevenZip => "7z file",
        ArchiveFormat.Tar => "TAR file",
        ArchiveFormat.TarGz => "TAR.GZ file",
        _ => "ZIP file",
    };

    /// <summary>"photos.tar.gz" → "photos".</summary>
    public static string BaseName(string archivePath)
    {
        var name = Path.GetFileName(archivePath.TrimEnd('\\'));
        foreach (var s in ExtractableSuffixes.OrderByDescending(s => s.Length))
            if (name.Length > s.Length && name.EndsWith(s, StringComparison.OrdinalIgnoreCase)) return name[..^s.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Name for a new archive of these items: the item's own name for one item, else the folder's.</summary>
    public static string SuggestName(IReadOnlyList<string> sources, ArchiveFormat format)
    {
        var first = sources[0].TrimEnd('\\');
        var baseName = sources.Count == 1
            ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
            : Path.GetFileName(Path.GetDirectoryName(first) ?? "") is { Length: > 0 } parent ? parent : "Archive";
        if (baseName.Length == 0) baseName = "Archive";
        var dir = Path.GetDirectoryName(first)!;
        return FileOperations.UniqueName(dir, baseName, Extension(format));
    }

    // ---------- Compress ----------

    /// <summary>Packs files and folders into a new archive at <paramref name="archivePath"/>. Nothing is left behind on failure.</summary>
    public static async Task CompressAsync(IReadOnlyList<string> sources, string archivePath, ArchiveFormat format,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (sources.Count == 0) throw new ArgumentException("Nothing to compress.", nameof(sources));
        var partial = Path.Combine(Path.GetDirectoryName(archivePath)!, "." + Path.GetFileName(archivePath) + ".partial");
        try
        {
            if (format == ArchiveFormat.Zip) await Task.Run(() => WriteZip(sources, partial, progress, ct), ct);
            else await RunTarAsync(TarCreateArguments(sources, partial, format), Path.GetDirectoryName(sources[0].TrimEnd('\\'))!, ct);
            File.Move(partial, archivePath);
            File.SetAttributes(archivePath, File.GetAttributes(archivePath) & ~FileAttributes.Hidden);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    private static void WriteZip(IReadOnlyList<string> sources, string zipPath, IProgress<double>? progress, CancellationToken ct)
    {
        var files = new List<(string Full, string Entry, bool IsDir)>();
        foreach (var source in sources)
        {
            var path = source.TrimEnd('\\');
            var name = Path.GetFileName(path);
            if (Directory.Exists(path))
            {
                files.Add((path, name + "/", true));
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false };
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    // Don't follow junctions out of the folder being packed.
                    var relative = name + "/" + Path.GetRelativePath(path, entry.FullName).Replace('\\', '/');
                    files.Add(entry is DirectoryInfo ? (entry.FullName, relative + "/", true) : (entry.FullName, relative, false));
                }
            }
            else files.Add((path, name, false));
        }

        long total = Math.Max(1, files.Where(f => !f.IsDir).Sum(f => new FileInfo(f.Full).Length));
        long done = 0;
        using var stream = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        File.SetAttributes(zipPath, File.GetAttributes(zipPath) | FileAttributes.Hidden);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var buffer = new byte[1 << 16];
        foreach (var (full, entryName, isDir) in files)
        {
            ct.ThrowIfCancellationRequested();
            if (isDir)
            {
                zip.CreateEntry(entryName).LastWriteTime = SafeTime(Directory.GetLastWriteTime(full));
                continue;
            }
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = SafeTime(File.GetLastWriteTime(full));
            using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var output = entry.Open();
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                done += read;
                progress?.Report((double)done / total);
            }
        }
    }

    // Zip timestamps can't go before 1980.
    private static DateTimeOffset SafeTime(DateTime t) => t.Year < 1980 ? new DateTime(1980, 1, 1) : t;

    private static string TarCreateArguments(IReadOnlyList<string> sources, string archivePath, ArchiveFormat format)
    {
        var parent = Path.GetDirectoryName(sources[0].TrimEnd('\\'));
        if (sources.Any(s => !string.Equals(Path.GetDirectoryName(s.TrimEnd('\\')), parent, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Items from different folders can only be compressed to a ZIP file.");
        var formatArg = format switch
        {
            ArchiveFormat.SevenZip => "--format 7zip",
            ArchiveFormat.TarGz => "-z --format pax",
            _ => "--format pax",
        };
        var names = string.Join(" ", sources.Select(s => Quote(Path.GetFileName(s.TrimEnd('\\')))));
        return $"-c {formatArg} -f {Quote(archivePath)} -C {Quote(parent!)} -- {names}";
    }

    // ---------- Extract ----------

    /// <summary>
    /// Extracts an archive next to itself. When everything in it sits in one top-level folder or file, that item
    /// lands directly in the folder (no "photos\photos\" nesting); otherwise a new folder named after the archive
    /// holds the contents. Returns the created item. Paths that would escape the destination are refused.
    /// </summary>
    public static async Task<string> ExtractAsync(string archivePath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var parent = Path.GetDirectoryName(archivePath.TrimEnd('\\'))!;
        var baseName = BaseName(archivePath);
        var work = Path.Combine(parent, $".extracting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        File.SetAttributes(work, File.GetAttributes(work) | FileAttributes.Hidden);
        try
        {
            if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => ExtractZip(archivePath, work, progress, ct), ct);
            else
                await RunTarAsync($"-x -f {Quote(archivePath)} -C {Quote(work)}", parent, ct);

            var top = new DirectoryInfo(work).GetFileSystemInfos();
            if (top.Length == 1 && !Path.Exists(Path.Combine(parent, top[0].Name)))
            {
                var single = Path.Combine(parent, top[0].Name);
                if (top[0] is DirectoryInfo d) d.MoveTo(single); else ((FileInfo)top[0]).MoveTo(single);
                Directory.Delete(work, recursive: false);
                return single;
            }
            var final = Path.Combine(parent, FileOperations.UniqueName(parent, baseName));
            Directory.Move(work, final);
            File.SetAttributes(final, File.GetAttributes(final) & ~FileAttributes.Hidden);
            return final;
        }
        catch
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void ExtractZip(string zipPath, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The archive contains an unsafe path: \"{entry.FullName}\".");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            done += entry.Length;
            progress?.Report((double)done / total);
        }
    }

    // ---------- tar.exe ----------

    private static async Task RunTarAsync(string arguments, string workingDirectory, CancellationToken ct)
    {
        if (!File.Exists(TarExe)) throw new InvalidOperationException("This needs tar.exe, which comes with Windows 10 1803 and later.");
        var info = new ProcessStartInfo(TarExe, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Couldn't start tar.exe.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        _ = process.StandardOutput.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            var message = (await stderr).Trim().Replace("tar.exe: ", "");
            throw new InvalidDataException(message.Length > 0 ? message : $"tar.exe failed (exit code {process.ExitCode}).");
        }
    }

    // A trailing backslash would escape the closing quote, so "C:\" becomes "C:\.".
    private static string Quote(string s) => "\"" + (s.EndsWith('\\') ? s + "." : s) + "\"";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
