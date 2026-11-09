using System.Runtime.InteropServices;
using ExplorerPlus.Core.Navigation;
using ExplorerPlus.Core.Shell;
using ExplorerPlus.Core.Threading;
using ExplorerPlus.Interop;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace ExplorerPlus.Core.Tests;

public class LocationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("epp-loc-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    private static void CreateShortcut(string lnk, string target) =>
        StaWorkerPool.General.Run(() =>
        {
            var link = Activator.CreateInstance(Type.GetTypeFromCLSID(ShellLinkClsid.CLSID_ShellLink)!)!;
            try
            {
                ((IShellLinkW)link).SetPath(target);
                ((ComTypes.IPersistFile)link).Save(lnk, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }).GetAwaiter().GetResult();

    [Fact]
    public async Task Home_lists_sections_in_order_and_skips_missing_items()
    {
        var pinned = Directory.CreateDirectory(Path.Combine(_root, "Projects")).FullName;
        var starred = Path.Combine(_root, "plan.md");
        File.WriteAllText(starred, "x");
        var recentDir = Directory.CreateDirectory(Path.Combine(_root, "Recent")).FullName;
        var older = Path.Combine(_root, "older.txt");
        var newer = Path.Combine(_root, "newer.txt");
        File.WriteAllText(older, "o");
        File.WriteAllText(newer, "n");
        CreateShortcut(Path.Combine(recentDir, "older.lnk"), older);
        CreateShortcut(Path.Combine(recentDir, "folder.lnk"), pinned); // folders aren't recent *files*
        CreateShortcut(Path.Combine(recentDir, "newer.lnk"), newer);
        CreateShortcut(Path.Combine(recentDir, "dupe.lnk"), newer);
        File.SetLastWriteTime(Path.Combine(recentDir, "older.lnk"), DateTime.Now.AddHours(-2));
        File.SetLastWriteTime(Path.Combine(recentDir, "dupe.lnk"), DateTime.Now.AddHours(-3));

        var options = new HomeFeedOptions([pinned, Path.Combine(_root, "missing")], [starred], IncludeRecent: true, RecentFolder: recentDir);
        var entries = await StaWorkerPool.Enumeration.Run(() => HomeFeed.Enumerate(options).ToList());

        Assert.Equal(
            [(HomeFeed.QuickAccessSection, "Projects"), (HomeFeed.StarredSection, "plan.md"), (HomeFeed.RecentSection, "newer.txt"), (HomeFeed.RecentSection, "older.txt")],
            entries.Select(e => (e.Section!, e.Name)).ToList());
        Assert.Equal([0, 1, 2, 2], entries.Select(e => e.SectionRank));
        Assert.Equal([0, 1], entries.Where(e => e.Section == HomeFeed.RecentSection).Select(e => e.SectionOrder));
        Assert.EndsWith("newer.lnk", entries[2].RecentLink);
    }

    [Fact]
    public async Task Home_resolves_to_its_own_location()
    {
        var home = await ShellLocation.ResolveAsync(ShellLocation.Known.Home);
        Assert.NotNull(home);
        Assert.True(home.IsHome);
        Assert.Equal("Home", home.DisplayName);
    }

    [Fact]
    public void Search_url_round_trips_query_and_scopes()
    {
        var url = SearchLocation.BuildUrl("kind:document report & co", [@"C:\Users\me\Docs", @"D:\"], "Search results in Docs");
        var (query, scopes) = SearchLocation.ParseUrl(url);
        Assert.Equal("kind:document report & co", query);
        Assert.Equal([@"C:\Users\me\Docs", @"D:\"], scopes);
        Assert.Equal(AddressKind.Location, PathParser.Parse(url, null).Kind);
        Assert.Equal(url, PathParser.Parse(url, null).Text);
    }

    [Fact]
    public async Task Search_finds_files_in_a_folder_and_goes_up_to_it()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_root, "deep", "er")).FullName;
        File.WriteAllText(Path.Combine(sub, "needle-report.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "haystack.txt"), "x");
        var scope = (await ShellLocation.ResolveAsync(_root))!;

        var search = await StaWorkerPool.Enumeration.Run(() => SearchLocation.Create("needle", scope));
        Assert.NotNull(search);
        Assert.True(search.IsSearch);
        Assert.Same(scope, await search.GetParentAsync());

        var found = new List<FolderEntry>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await foreach (var batch in FolderLoader.LoadAsync(search, cts.Token)) found.AddRange(batch);
        var hit = Assert.Single(found, e => e.Name.Contains("needle", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.Combine(sub, "needle-report.txt"), hit.Path, ignoreCase: true);
        Assert.DoesNotContain(found, e => e.Name.Contains("haystack", StringComparison.OrdinalIgnoreCase));

        var reopened = await ShellLocation.ResolveAsync(search.ParsingName);
        Assert.Equal("needle", reopened?.SearchQuery);
    }

    [Fact]
    public async Task Native_menu_is_refused_for_items_from_different_folders()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
        File.WriteAllText(Path.Combine(a, "1.txt"), "1");
        File.WriteAllText(Path.Combine(a, "3.txt"), "3");
        File.WriteAllText(Path.Combine(b, "2.txt"), "2");
        var (mixed, same) = await StaWorkerPool.General.Run(() =>
        {
            var one = ShellNative.Parse(Path.Combine(a, "1.txt"));
            var two = ShellNative.Parse(Path.Combine(b, "2.txt"));
            var three = ShellNative.Parse(Path.Combine(a, "3.txt"));
            // (Building the menus themselves needs the UI thread and a window, so only the check is tested.)
            return (ShellContextMenu.ShareParent([one, two]), ShellContextMenu.ShareParent([one, three]));
        });
        Assert.False(mixed);
        Assert.True(same);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, [Out] char[]? lpszFile, uint cch);

    [Fact]
    public async Task Data_object_for_items_from_different_folders_carries_all_their_paths()
    {
        var a = Directory.CreateDirectory(Path.Combine(_root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(_root, "b")).FullName;
        var one = Path.Combine(a, "1.txt");
        var two = Path.Combine(b, "2.txt");
        File.WriteAllText(one, "1");
        File.WriteAllText(two, "2");

        var paths = await StaWorkerPool.General.Run(() =>
        {
            var ptr = ShellDragDrop.CreateDataObject([ShellNative.Parse(one), ShellNative.Parse(two)], IntPtr.Zero);
            Assert.NotEqual(IntPtr.Zero, ptr);
            var data = (ComTypes.IDataObject)Marshal.GetObjectForIUnknown(ptr);
            Marshal.Release(ptr);
            var format = new ComTypes.FORMATETC { cfFormat = 15 /* CF_HDROP */, dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = ComTypes.TYMED.TYMED_HGLOBAL };
            data.GetData(ref format, out var medium);
            var result = new List<string>();
            var count = DragQueryFileW(medium.unionmember, uint.MaxValue, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var buffer = new char[1024];
                var len = DragQueryFileW(medium.unionmember, i, buffer, (uint)buffer.Length);
                result.Add(new string(buffer, 0, (int)len));
            }
            Marshal.FinalReleaseComObject(data);
            return result;
        });

        Assert.Equal([one, two], paths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void New_templates_are_read_from_the_registry()
    {
        // Which templates exist depends on the machine (new Notepad even drops .txt's), so check their shape only.
        var templates = ShellNewTemplates.Load();
        Assert.NotEmpty(templates);
        Assert.All(templates, t =>
        {
            Assert.StartsWith(".", t.Extension);
            Assert.False(string.IsNullOrWhiteSpace(t.TypeName));
            Assert.False(string.IsNullOrWhiteSpace(t.BaseName));
        });
        Assert.DoesNotContain(templates, t => t.Extension.Equals(".library-ms", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(templates.Count, templates.Select(t => t.TypeName).Distinct(StringComparer.CurrentCultureIgnoreCase).Count());
    }

    [Fact]
    public async Task Creating_from_a_template_makes_the_file_with_its_content()
    {
        var folder = await StaWorkerPool.General.Run(() => ShellNative.Parse(_root));
        var template = new NewItemTemplate(".bin", "Test", "New Test", NewItemKind.Data, null, [1, 2, 3], -1);
        var log = new FileOpLog();

        var (result, path, kind) = await FileOperations.CreateFromTemplateAsync(template, folder, _root, "New Test.bin", IntPtr.Zero, log);

        Assert.Equal(FileOpResult.Succeeded, result);
        Assert.Equal(FileOpKind.New, kind);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(path!));
        Assert.Equal(path, Assert.Single(log.TopLevel(FileOpKind.New)).Result, ignoreCase: true);
    }
}
