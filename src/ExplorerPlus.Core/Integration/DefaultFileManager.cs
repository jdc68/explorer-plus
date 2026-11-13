using ExplorerPlus.Interop;
using Microsoft.Win32;

namespace ExplorerPlus.Core.Integration;

public enum DefaultFileManagerState { Off, On, OnForOtherCopy }

public sealed record DefaultFileManagerStatus(DefaultFileManagerState State, string? RegisteredExe);

/// <summary>
/// The opt-in "default file manager" switch. Everything lives under HKCU\Software\Classes, so it needs no elevation
/// and only affects the current user:
/// <list type="bullet">
/// <item>Directory and Drive get a default verb that runs Explorer+ (folder links, "Open folder" in other apps).</item>
/// <item>The Home folder's "opennewwindow" verb runs Explorer+ (Win+E).</item>
/// </list>
/// Values that were already in HKCU are saved before they are replaced and put back by <see cref="Unregister"/>,
/// and keys created here are removed again, so turning it off leaves the registry as it was.
/// </summary>
public sealed class DefaultFileManager
{
    public const string Verb = "ExplorerPlus.open";
    public const string WinEFolder = @"CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}"; // Home, opened by Win+E

    private const string CreatedKeysValue = "CreatedKeys";
    private static readonly string[] VerbParents = [@"Directory\shell", @"Drive\shell"];
    private static readonly string WinECommand = WinEFolder + @"\shell\opennewwindow\command";

    private readonly RegistryKey _classes;
    private readonly RegistryKey _backup;

    /// <param name="classes">Where the classes live (HKCU\Software\Classes; a scratch key in tests).</param>
    /// <param name="backup">Where replaced values and created keys are remembered.</param>
    public DefaultFileManager(RegistryKey classes, RegistryKey backup)
    {
        _classes = classes;
        _backup = backup;
    }

    public static DefaultFileManager ForCurrentUser() =>
        new(Registry.CurrentUser.CreateSubKey(@"Software\Classes"), Registry.CurrentUser.CreateSubKey(@"Software\ExplorerPlus\DefaultFileManager"));

    public static string CommandFor(string exe) => $"\"{exe}\" \"%1\"";

    public DefaultFileManagerStatus GetStatus(string currentExe)
    {
        using var shell = _classes.OpenSubKey(VerbParents[0]);
        if (shell?.GetValue(null) as string != Verb) return new(DefaultFileManagerState.Off, null);
        using var command = _classes.OpenSubKey($@"{VerbParents[0]}\{Verb}\command");
        var exe = ExeFromCommand(command?.GetValue(null) as string);
        if (exe is null) return new(DefaultFileManagerState.Off, null);
        return new(string.Equals(Path.GetFullPath(exe), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase)
            ? DefaultFileManagerState.On
            : DefaultFileManagerState.OnForOtherCopy, exe);
    }

    /// <summary>Makes <paramref name="exe"/> the file manager for folders, drives and Win+E. Calling it again re-points it.</summary>
    public void Register(string exe)
    {
        foreach (var parent in VerbParents)
        {
            SetValue(parent, null, Verb);
            SetValue($@"{parent}\{Verb}", null, "Open in Explorer+");
            SetValue($@"{parent}\{Verb}", "Icon", $"\"{exe}\",0");
            SetValue($@"{parent}\{Verb}\command", null, CommandFor(exe));
        }
        SetValue(WinECommand, null, $"\"{exe}\"");
        // An empty DelegateExecute hides the machine-wide one, which would otherwise win over the command.
        SetValue(WinECommand, "DelegateExecute", "");
        NotifyShell();
    }

    /// <summary>Puts back what was there before <see cref="Register"/>. Safe to call when not registered.</summary>
    public void Unregister()
    {
        foreach (var name in _backup.GetValueNames())
        {
            if (name == CreatedKeysValue || !TrySplit(name, out var keyPath, out var valueName)) continue;
            var saved = _backup.GetValue(name) as string;
            if (saved is not null && saved.StartsWith('='))
            {
                // The key may be gone: the MSI deletes the keys Explorer+ writes to before this runs on uninstall.
                using var restore = _classes.CreateSubKey(keyPath, writable: true);
                restore.SetValue(valueName, saved[1..]);
                continue;
            }
            using var key = _classes.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName ?? "", throwOnMissingValue: false);
        }
        foreach (var parent in VerbParents) _classes.DeleteSubKeyTree($@"{parent}\{Verb}", throwOnMissingSubKey: false);

        // Deepest first, and only while empty: something else may have put its own values there since.
        var created = (_backup.GetValue(CreatedKeysValue) as string[] ?? []).OrderByDescending(k => k.Length);
        foreach (var path in created) DeleteIfEmpty(path);

        foreach (var name in _backup.GetValueNames()) _backup.DeleteValue(name, throwOnMissingValue: false);
        NotifyShell();
    }

    private void SetValue(string keyPath, string? valueName, string value)
    {
        var backupName = $"{keyPath}|{valueName}";
        if (_backup.GetValue(backupName) is null)
        {
            // First change to this value: remember what was there ("=old") or that there was nothing ("-").
            using var existing = _classes.OpenSubKey(keyPath);
            var old = existing?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            _backup.SetValue(backupName, old is string s ? "=" + s : "-");
        }
        using var key = CreateKeyRemembering(keyPath);
        key.SetValue(valueName, value);
    }

    private RegistryKey CreateKeyRemembering(string keyPath)
    {
        var created = (_backup.GetValue(CreatedKeysValue) as string[] ?? []).ToList();
        var parts = keyPath.Split('\\');
        for (var i = 1; i <= parts.Length; i++)
        {
            var partial = string.Join('\\', parts.Take(i));
            using var probe = _classes.OpenSubKey(partial);
            if (probe is null && !created.Contains(partial, StringComparer.OrdinalIgnoreCase)) created.Add(partial);
        }
        _backup.SetValue(CreatedKeysValue, created.ToArray(), RegistryValueKind.MultiString);
        return _classes.CreateSubKey(keyPath, writable: true);
    }

    private void DeleteIfEmpty(string path)
    {
        using (var key = _classes.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0) return;
        }
        _classes.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    private static bool TrySplit(string name, out string keyPath, out string? valueName)
    {
        var bar = name.LastIndexOf('|');
        keyPath = bar < 0 ? "" : name[..bar];
        valueName = bar < 0 || bar == name.Length - 1 ? null : name[(bar + 1)..];
        return bar > 0;
    }

    internal static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static void NotifyShell()
    {
        try { Shell32.SHChangeNotify((int)SHCNE.ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero); }
        catch { /* only refreshes cached associations */ }
    }
}
