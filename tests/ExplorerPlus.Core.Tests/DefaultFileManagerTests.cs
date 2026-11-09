using ExplorerPlus.Core.Integration;
using Microsoft.Win32;

namespace ExplorerPlus.Core.Tests;

public class DefaultFileManagerTests : IDisposable
{
    private readonly string _root = $@"Software\ExplorerPlusTests\{Guid.NewGuid():N}";
    private readonly RegistryKey _classes;
    private readonly RegistryKey _backup;

    public DefaultFileManagerTests()
    {
        _classes = Registry.CurrentUser.CreateSubKey(_root + @"\Classes");
        _backup = Registry.CurrentUser.CreateSubKey(_root + @"\Backup");
    }

    public void Dispose()
    {
        _classes.Dispose();
        _backup.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\ExplorerPlusTests");
        if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\ExplorerPlusTests", throwOnMissingSubKey: false);
    }

    private DefaultFileManager Manager => new(_classes, _backup);

    private string? Read(string key, string? value = null)
    {
        using var k = _classes.OpenSubKey(key);
        return k?.GetValue(value) as string;
    }

    [Fact]
    public void Register_sets_folder_drive_and_win_e_verbs()
    {
        const string exe = @"C:\Apps\Explorer+\ExplorerPlus.exe";
        Manager.Register(exe);

        Assert.Equal(DefaultFileManager.Verb, Read(@"Directory\shell"));
        Assert.Equal(DefaultFileManager.Verb, Read(@"Drive\shell"));
        Assert.Equal($"\"{exe}\" \"%1\"", Read($@"Directory\shell\{DefaultFileManager.Verb}\command"));
        Assert.Equal($"\"{exe}\" \"%1\"", Read($@"Drive\shell\{DefaultFileManager.Verb}\command"));
        Assert.Equal($"\"{exe}\"", Read(DefaultFileManager.WinEFolder + @"\shell\opennewwindow\command"));
        Assert.Equal("", Read(DefaultFileManager.WinEFolder + @"\shell\opennewwindow\command", "DelegateExecute"));

        Assert.Equal(new DefaultFileManagerStatus(DefaultFileManagerState.On, exe), Manager.GetStatus(exe));
        Assert.Equal(DefaultFileManagerState.OnForOtherCopy, Manager.GetStatus(@"D:\Other\ExplorerPlus.exe").State);
    }

    [Fact]
    public void Unregister_leaves_no_keys_behind()
    {
        Manager.Register(@"C:\A\ExplorerPlus.exe");
        Manager.Unregister();

        Assert.Empty(_classes.GetSubKeyNames());
        Assert.Empty(_backup.GetValueNames());
        Assert.Equal(DefaultFileManagerState.Off, Manager.GetStatus(@"C:\A\ExplorerPlus.exe").State);
    }

    [Fact]
    public void Unregister_restores_what_another_file_manager_had_set()
    {
        using (var shell = _classes.CreateSubKey(@"Directory\shell"))
        {
            shell.SetValue(null, "openinfiles");
            using var cmd = shell.CreateSubKey(@"openinfiles\command");
            cmd.SetValue(null, "files.exe \"%1\"");
        }

        Manager.Register(@"C:\A\ExplorerPlus.exe");
        // Re-pointing to another copy must keep the original backup, not back up our own value.
        Manager.Register(@"C:\B\ExplorerPlus.exe");
        Assert.Equal(DefaultFileManagerState.On, Manager.GetStatus(@"C:\B\ExplorerPlus.exe").State);

        Manager.Unregister();
        Assert.Equal("openinfiles", Read(@"Directory\shell"));
        Assert.Equal("files.exe \"%1\"", Read(@"Directory\shell\openinfiles\command"));
        Assert.Null(_classes.OpenSubKey($@"Directory\shell\{DefaultFileManager.Verb}"));
        Assert.Null(_classes.OpenSubKey("Drive"));
        Assert.Null(_classes.OpenSubKey("CLSID"));
    }

    [Fact]
    public void Unregister_without_register_does_nothing()
    {
        Manager.Unregister();
        Assert.Empty(_classes.GetSubKeyNames());
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\X\\x.exe\" \"%1\"", "C:\\Program Files\\X\\x.exe")]
    [InlineData("C:\\X\\x.exe %1", "C:\\X\\x.exe")]
    [InlineData("", null)]
    public void Exe_is_read_back_from_the_command(string command, string? exe) =>
        Assert.Equal(exe, DefaultFileManager.ExeFromCommand(command));
}
