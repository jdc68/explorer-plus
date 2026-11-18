using System.Diagnostics;
using System.Runtime.InteropServices;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Tests;

public class FileLockTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("epp-locks-").FullName;
    private readonly List<Process> _holders = [];

    public void Dispose()
    {
        foreach (var p in _holders)
        {
            try { if (!p.HasExited) p.Kill(); p.WaitForExit(5000); } catch { }
            p.Dispose();
        }
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string NewFile(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        return path;
    }

    // a PowerShell that keeps the file open with no sharing, optionally behind a window, until it is closed or killed
    private Process Hold(string file, bool window = false, string? workingDirectory = null, bool refuse = false)
    {
        var open = file.Length == 0 ? "" : $"$f=[IO.File]::Open('{file}','Open','ReadWrite','None'); ";
        var cancel = refuse ? "$form.Add_FormClosing({ $_.Cancel = $true }); " : "";
        var script = window
            ? $"Add-Type -AssemblyName System.Windows.Forms; {open}$form=New-Object Windows.Forms.Form; {cancel}[Windows.Forms.Application]::Run($form)"
            : $"{open}Start-Sleep 120";
        var p = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-Command", script])
        {
            UseShellExecute = false, CreateNoWindow = !window, WorkingDirectory = workingDirectory ?? _root,
        })!;
        _holders.Add(p);
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 20000)
        {
            if (file.Length > 0 && FileLocks.IsBlocked(file)) break;
            if (file.Length == 0 && workingDirectory is not null && FileLocks.IsBlocked(workingDirectory)) break;
            Thread.Sleep(100);
        }
        if (window) Thread.Sleep(1500);
        return p;
    }

    [Fact]
    public void Restart_Manager_structs_match_the_native_layout()
    {
        Assert.Equal(12, Marshal.SizeOf<RM_UNIQUE_PROCESS>());
        Assert.Equal(12 + 256 * 2 + 64 * 2 + 16, Marshal.SizeOf<RM_PROCESS_INFO>());
    }

    [Fact]
    public void Names_the_app_holding_a_file_and_one_inside_a_folder()
    {
        var file = NewFile(@"folder\inner\locked.txt");
        var holder = Hold(file);

        var direct = Assert.Single(FileLocks.FindHolders([file]));
        Assert.Equal(holder.Id, direct.ProcessId);
        Assert.True(direct.CanClose);
        Assert.Contains("PowerShell", direct.Name);

        var viaFolder = FileLocks.FindHolders([Path.Combine(_root, "folder")]);
        Assert.Contains(viaFolder, h => h.ProcessId == holder.Id);
    }

    [Fact]
    public void Finds_a_process_whose_current_directory_is_the_folder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "cwd")).FullName;
        var holder = Hold("", workingDirectory: folder);

        Assert.True(FileLocks.IsBlocked(folder));
        Assert.Contains(FileLocks.FindHolders([folder]), h => h.ProcessId == holder.Id);
    }

    [Fact]
    public void An_app_that_lets_others_delete_isnt_blamed()
    {
        var shared = NewFile(@"mixed\shared.txt");
        var locked = NewFile(@"mixed\locked.txt");
        using var open = File.Open(shared, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var holder = Hold(locked);

        Assert.False(FileLocks.IsBlocked(shared));
        var holders = FileLocks.FindHolders([Path.Combine(_root, "mixed")]);
        Assert.Contains(holders, h => h.ProcessId == holder.Id);
        Assert.DoesNotContain(holders, h => h.Kind == HolderKind.Self);
    }

    [Fact]
    public void This_app_is_never_offered_for_closing()
    {
        var file = NewFile("own.txt");
        using var open = File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var self = Assert.Single(FileLocks.FindHolders([file]));
        Assert.Equal(HolderKind.Self, self.Kind);
        Assert.False(self.CanClose);
        Assert.False(FileLocks.End(self));
    }

    [Fact]
    public void Nothing_is_found_for_a_free_file_or_a_missing_one()
    {
        Assert.Empty(FileLocks.FindHolders([NewFile("free.txt")]));
        Assert.Empty(FileLocks.FindHolders([Path.Combine(_root, "missing.txt")]));
    }

    [Fact]
    public async Task An_app_with_a_window_closes_when_asked_and_the_file_can_go()
    {
        var file = NewFile("window.txt");
        var holder = Hold(file, window: true);
        var found = Assert.Single(FileLocks.FindHolders([file]), h => h.ProcessId == holder.Id);

        Assert.True(await FileLocks.AskToCloseAsync(found, TimeSpan.FromSeconds(10)));
        Assert.False(FileLocks.IsRunning(found));
        File.Delete(file);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task An_app_that_doesnt_close_in_time_can_be_ended()
    {
        var file = NewFile("refuses.txt");
        var holder = Hold(file, window: true, refuse: true);
        var found = Assert.Single(FileLocks.FindHolders([file]), h => h.ProcessId == holder.Id);

        // like an app whose "Save changes?" was cancelled
        Assert.False(await FileLocks.AskToCloseAsync(found, TimeSpan.FromSeconds(3)));
        Assert.True(FileLocks.IsRunning(found));
        Assert.True(FileLocks.End(found));
        Assert.False(FileLocks.IsRunning(found));
        Assert.False(FileLocks.IsBlocked(file));
    }

    [Fact]
    public void A_holder_whose_process_id_was_reused_is_left_alone()
    {
        var file = NewFile("reuse.txt");
        var holder = Hold(file);
        var found = Assert.Single(FileLocks.FindHolders([file]), h => h.ProcessId == holder.Id);
        var stale = found with { StartTime = found.StartTime - 1 };

        Assert.False(FileLocks.IsRunning(stale));
        Assert.True(FileLocks.End(stale));
        Assert.True(FileLocks.IsRunning(found));
    }

    [Fact]
    public void Log_keeps_failed_and_skipped_items_with_where_they_were_going()
    {
        var file = NewFile("item.txt");
        var dest = Directory.CreateDirectory(Path.Combine(_root, "dest")).FullName;
        var item = ShellNative.CreateItem(ShellNative.Parse(file));
        var folder = ShellNative.CreateItem(ShellNative.Parse(dest));
        var log = new FileOpLog();

        log.PostDeleteItem(0, item, 0x0027000B, null); // pending while the shell's dialog is up
        log.PostDeleteItem(0, item, unchecked((int)0x80270000), null); // cancelled there
        log.PostCopyItem(0, item, folder, null, unchecked((int)0x80270028), null);
        log.PostMoveItem(0, item, folder, "other.txt", 0x00270005, null); // skipped
        log.PostRenameItem(0, item, "new.txt", unchecked((int)0x80070020), null);
        log.PostCopyItem(0, item, folder, null, 0, null);

        Assert.Collection(log.Failures,
            f => Assert.Equal((FileOpKind.Delete, file, (string?)null), (f.Kind, f.Source, f.Destination)),
            f => Assert.Equal((FileOpKind.Copy, file, Path.Combine(dest, "item.txt")), (f.Kind, f.Source, f.Destination)),
            f => Assert.Equal((FileOpKind.Move, file, Path.Combine(dest, "other.txt")), (f.Kind, f.Source, f.Destination)),
            f => Assert.Equal((FileOpKind.Rename, file, Path.Combine(_root, "new.txt")), (f.Kind, f.Source, f.Destination)));
        Assert.Equal([file, Path.Combine(dest, "item.txt"), Path.Combine(dest, "other.txt"), Path.Combine(_root, "new.txt")],
            FileLocks.PathsToCheck(log.Failures));
    }

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\a\", true)]
    [InlineData(@"C:\A\b.txt", @"c:\a", true)]
    [InlineData(@"C:\ab", @"C:\a", false)]
    [InlineData(@"C:\a", @"C:\a\b", false)]
    public void Same_or_under(string path, string root, bool expected) => Assert.Equal(expected, FileLocks.IsSameOrUnder(path, root));
}
