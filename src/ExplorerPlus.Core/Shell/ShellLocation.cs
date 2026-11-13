using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;

namespace ExplorerPlus.Core.Shell;

public sealed class ShellLocation
{
    public required Pidl Pidl { get; init; }
    public required string DisplayName { get; init; }
    public required string ParsingName { get; init; }
    public string? FileSystemPath { get; init; }
    public SFGAO Attributes { get; init; }

    public bool IsFolder => Attributes.HasFlag(SFGAO.FOLDER);

    /// <summary>True for a real directory we can list with the fast enumerator (zip files have a path but are streams).</summary>
    public bool IsFileSystemFolder => FileSystemPath is not null && IsFolder && !Attributes.HasFlag(SFGAO.STREAM);

    /// <summary>Text shown when the address bar is in edit mode.</summary>
    public string EditText => FileSystemPath ?? ParsingName;

    /// <summary>Search results only: the folder that was searched.</summary>
    public ShellLocation? SearchScope { get; init; }

    /// <summary>Search results only: what was searched for (Windows Search / AQS syntax).</summary>
    public string? SearchQuery { get; init; }

    public bool IsSearch => SearchQuery is not null;

    /// <summary>Instant-search results only (from the index service rather than Windows Search): what the last load found.</summary>
    public IndexSearchInfo? IndexResults { get; init; }

    public bool IsIndexSearch => IndexResults is not null;
    public bool IsHome => Is(Known.Home);
    public bool IsRecycleBin => Is(Known.RecycleBin);
    public bool IsNetwork => Is(Known.Network);
    public bool IsThisPC => Is(Known.ThisPC);

    private bool Is(string parsingName) => string.Equals(ParsingName, parsingName, StringComparison.OrdinalIgnoreCase);

    public static class Known
    {
        public const string ThisPC = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
        public const string Network = "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";
        public const string Home = "::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}";
        public const string QuickAccess = "::{679F85CB-0220-4080-B29B-5540CC05AAB6}";
        public const string ControlPanel = "::{26EE0668-A00A-44D7-9371-BEB064C98683}";
        public const string Linux = "::{B2B4A4D1-2754-4140-A2EB-9A76D9D7CDC6}";
        public const string OneDriveClsid = "{018D5C66-4533-4307-9B53-224DE2ED1FE6}";
        public const string GalleryClsid = "{E88865EA-0E1C-4E20-9AA6-EDCD0212C87C}";
        public const string LibrariesClsid = "{031E4825-7B94-4DC3-B131-E946B44C8DD5}";
    }

    /// <summary>Must run on an STA thread.</summary>
    public static ShellLocation FromPidl(Pidl pidl)
    {
        var item = ShellNative.CreateItem(pidl);
        try
        {
            item.GetAttributes(SFGAO.FOLDER | SFGAO.FILESYSTEM | SFGAO.STREAM | SFGAO.BROWSABLE | SFGAO.HASSUBFOLDER, out var attrs);
            var fsPath = attrs.HasFlag(SFGAO.FILESYSTEM) ? ShellNative.GetName(item, SIGDN.FILESYSPATH) : null;
            return new ShellLocation
            {
                Pidl = pidl,
                DisplayName = ShellNative.GetName(item, SIGDN.NORMALDISPLAY) ?? fsPath ?? "?",
                ParsingName = ShellNative.GetName(item, SIGDN.DESKTOPABSOLUTEPARSING) ?? fsPath ?? "",
                FileSystemPath = fsPath,
                Attributes = attrs,
            };
        }
        finally { ShellNative.Release(item); }
    }

    public static Task<ShellLocation> FromPidlAsync(Pidl pidl) => StaWorkerPool.Enumeration.Run(() => FromPidl(pidl));

    /// <summary>Resolves a parsing name (path, shell:xxx, ::{CLSID}, \\server\share). Returns null if it doesn't exist.</summary>
    public static Task<ShellLocation?> FromParsingNameAsync(string parsingName) =>
        StaWorkerPool.Enumeration.Run(() =>
        {
            var pidl = ShellNative.TryParse(parsingName);
            return pidl is null ? null : FromPidl(pidl);
        });

    /// <summary>
    /// Resolves user input or a parsing name to a location. File-system folders reached through virtual
    /// names (shell:Downloads, known folders) are normalised to their real path, so the breadcrumb always
    /// shows the drive-based path and the fast enumerator is used.
    /// </summary>
    public static Task<ShellLocation?> ResolveAsync(string parsingName) =>
        StaWorkerPool.Enumeration.Run(() =>
        {
            if (string.Equals(parsingName, Known.Home, StringComparison.OrdinalIgnoreCase)) return CreateHome();
            if (SearchLocation.IsSearchUrl(parsingName)) return SearchLocation.FromUrl(parsingName);
            if (IndexSearch.IsUrl(parsingName)) return IndexSearch.FromUrl(parsingName);
            var pidl = ShellNative.TryParse(parsingName);
            if (pidl is null) return null;
            var loc = FromPidl(pidl);
            if (loc.IsFileSystemFolder && !string.Equals(loc.ParsingName, loc.FileSystemPath, StringComparison.OrdinalIgnoreCase))
            {
                var fsPidl = ShellNative.TryParse(loc.FileSystemPath!);
                if (fsPidl is not null) loc = FromPidl(fsPidl);
            }
            return loc;
        });

    /// <summary>
    /// Parent location, or null at the desktop root. Local file-system folders always go up along their real
    /// path (Downloads → your user folder → Users → C: → This PC), even when the shell models them as
    /// known folders hanging directly off the desktop.
    /// </summary>
    public Task<ShellLocation?> GetParentAsync() =>
        StaWorkerPool.Enumeration.Run<ShellLocation?>(() =>
        {
            if (SearchScope is not null) return SearchScope;
            if (IsLocalDrivePath(FileSystemPath))
            {
                var parentPath = Path.GetDirectoryName(FileSystemPath!.TrimEnd('\\') + (FileSystemPath.Length <= 3 ? "\\" : ""));
                var target = parentPath is null || FileSystemPath.TrimEnd('\\').Length <= 2 ? Known.ThisPC : parentPath;
                var pidl = ShellNative.TryParse(target);
                if (pidl is not null) return FromPidl(pidl);
            }
            var parent = Pidl.GetParent();
            return parent is null ? null : FromPidl(parent);
        });

    /// <summary>The chain of ancestors from just below the desktop down to this location (for the breadcrumb).</summary>
    public Task<IReadOnlyList<ShellLocation>> GetAncestryAsync() =>
        StaWorkerPool.Enumeration.Run<IReadOnlyList<ShellLocation>>(GetAncestry);

    private List<ShellLocation> GetAncestry()
    {
        if (SearchScope is not null) return [.. SearchScope.GetAncestry(), this];
        var chain = new List<ShellLocation>();
        if (IsLocalDrivePath(FileSystemPath))
        {
            // This PC › Local Disk (C:) › Users › denis › Downloads
            var thisPc = ShellNative.TryParse(Known.ThisPC);
            if (thisPc is not null) chain.Add(FromPidl(thisPc));
            var path = FileSystemPath!.TrimEnd('\\');
            var parts = path.Split('\\');
            var prefix = parts[0] + "\\";
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0) prefix = Path.Combine(prefix, parts[i]);
                if (i == parts.Length - 1) { chain.Add(this); break; }
                var pidl = ShellNative.TryParse(prefix);
                if (pidl is not null) chain.Add(FromPidl(pidl));
            }
            return chain;
        }

        var p = Pidl;
        while (!p.IsDesktop)
        {
            chain.Add(p.Equals(Pidl) ? this : FromPidl(p));
            p = p.GetParent()!;
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>
    /// Explorer+'s own Home page. It borrows the shell's Home (or, before Windows 11, Quick access) for its icon
    /// and identity, but its content is built by <see cref="HomeFeed"/>. Must run on an STA thread.
    /// </summary>
    public static ShellLocation? CreateHome()
    {
        var pidl = ShellNative.TryParse(Known.Home) ?? ShellNative.TryParse(Known.QuickAccess);
        if (pidl is null) return null;
        var shell = FromPidl(pidl);
        return new ShellLocation
        {
            Pidl = pidl,
            DisplayName = "Home",
            ParsingName = Known.Home,
            Attributes = shell.Attributes | SFGAO.FOLDER,
        };
    }

    private static bool IsLocalDrivePath(string? path) =>
        path is { Length: >= 2 } && char.IsLetter(path[0]) && path[1] == ':';

    public bool IsSameAs(ShellLocation? other) =>
        other is not null && string.Equals(ParsingName, other.ParsingName, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => DisplayName;
}
