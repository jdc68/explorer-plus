using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ExplorerPlus.Core.Shell;

public enum GitState { Clean, Ignored, Untracked, Added, Renamed, Staged, Modified, Deleted, Conflict }

public static class GitStatus
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RootLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static readonly ConcurrentDictionary<string, (string? Root, long At)> Roots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (Task<Snapshot?> Task, long At)> Snapshots = new(StringComparer.OrdinalIgnoreCase);
    private static volatile bool _noGit;

    internal sealed class Snapshot
    {
        // paths relative to the repository root, with forward slashes; folders end with a slash
        public Dictionary<string, GitState> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Prefix, GitState State)> Folders { get; } = [];
    }

    public static string Label(GitState state) => state switch
    {
        GitState.Clean => "",
        GitState.Staged => "Staged",
        _ => state.ToString(),
    };

    // null when the item isn't in a repository or git can't be run
    public static async Task<GitState?> GetAsync(string path, bool isFolder, CancellationToken ct = default)
    {
        if (_noGit) return null;
        var root = FindRoot(isFolder ? path : Path.GetDirectoryName(path) ?? path);
        if (root is null) return null;
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative == ".") return null;
        if (relative == ".git" || relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase)) return null;
        var snapshot = await GetSnapshotAsync(root).WaitAsync(ct).ConfigureAwait(false);
        return snapshot is null ? null : StateOf(snapshot, relative, isFolder);
    }

    public static void Invalidate() => Snapshots.Clear();

    internal static GitState StateOf(Snapshot snapshot, string relative, bool isFolder)
    {
        if (!isFolder && snapshot.Files.TryGetValue(relative, out var own)) return own;
        var key = isFolder ? relative.TrimEnd('/') + "/" : relative;
        foreach (var (prefix, state) in snapshot.Folders)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return state;
        if (!isFolder) return GitState.Clean;

        // a folder shows the most pressing change inside it; ignored files alone don't count
        var worst = GitState.Clean;
        foreach (var (file, state) in snapshot.Files)
            if (state > worst && state != GitState.Ignored && file.StartsWith(key, StringComparison.OrdinalIgnoreCase)) worst = state;
        foreach (var (prefix, state) in snapshot.Folders)
            if (state > worst && state != GitState.Ignored && prefix.StartsWith(key, StringComparison.OrdinalIgnoreCase)) worst = state;
        return worst is GitState.Untracked or GitState.Clean ? worst : worst == GitState.Conflict ? GitState.Conflict : GitState.Modified;
    }

    internal static string? FindRoot(string folder)
    {
        var now = Environment.TickCount64;
        if (Roots.TryGetValue(folder, out var known) && now - known.At < RootLifetime.TotalMilliseconds) return known.Root;
        string? root = null;
        for (var dir = folder.TrimEnd('\\'); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)?.TrimEnd('\\'))
        {
            var git = Path.Combine(dir.Length == 2 ? dir + "\\" : dir, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                root = dir.Length == 2 ? dir + "\\" : dir;
                break;
            }
            if (dir.Length <= 3) break;
        }
        if (Roots.Count > 10_000) Roots.Clear();
        Roots[folder] = (root, now);
        return root;
    }

    private static Task<Snapshot?> GetSnapshotAsync(string root)
    {
        var now = Environment.TickCount64;
        if (Snapshots.TryGetValue(root, out var known) && now - known.At < Lifetime.TotalMilliseconds) return known.Task;
        var task = Task.Run(() => Read(root));
        Snapshots[root] = (task, now);
        return task;
    }

    private static Snapshot? Read(string root)
    {
        var info = new ProcessStartInfo("git")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-C", root, "--no-optional-locks", "-c", "core.quotepath=off", "status", "--porcelain=v1", "-z", "--ignored=matching", "-unormal" })
            info.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            _ = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(); } catch { }
                return null;
            }
            return process.ExitCode == 0 ? Parse(output.GetAwaiter().GetResult()) : null;
        }
        catch (Win32Exception)
        {
            _noGit = true; // not installed
            return null;
        }
    }

    internal static Snapshot Parse(string output)
    {
        var snapshot = new Snapshot();
        var fields = output.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            if (f.Length < 4) continue;
            char x = f[0], y = f[1];
            var path = f[3..];
            if (x is 'R' or 'C') i++; // the original path follows
            var state = StateOf(x, y);
            if (path.EndsWith('/')) snapshot.Folders.Add((path, state));
            else snapshot.Files[path] = state;
        }
        return snapshot;
    }

    private static GitState StateOf(char x, char y) => (x, y) switch
    {
        ('?', '?') => GitState.Untracked,
        ('!', '!') => GitState.Ignored,
        ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'D') => GitState.Conflict,
        (_, 'D') or ('D', _) => GitState.Deleted,
        (_, 'M') or (_, 'T') => GitState.Modified,
        ('R', _) => GitState.Renamed,
        ('A', _) => GitState.Added,
        _ => GitState.Staged,
    };
}
