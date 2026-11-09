using ExplorerPlus.Core.Navigation;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.Core.Tests;

public class PathParserTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("epp-parse-").FullName;

    public PathParserTests() => Directory.CreateDirectory(Path.Combine(_root, "src", "app"));

    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(@"C:\Windows", @"C:\Windows")]
    [InlineData(@"c:", @"c:\")]
    [InlineData(@"""C:\Program Files""", @"C:\Program Files")]
    [InlineData(@"C:\Windows\", @"C:\Windows")]
    public void Absolute_paths_are_locations(string input, string expected)
    {
        var r = PathParser.Parse(input, null);
        Assert.Equal(AddressKind.Location, r.Kind);
        Assert.Equal(expected, r.Text, ignoreCase: true);
    }

    [Theory]
    [InlineData("shell:Downloads")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    [InlineData(@"\\server\share")]
    public void Shell_and_unc_names_pass_through(string input)
    {
        var r = PathParser.Parse(input, null);
        Assert.Equal(AddressKind.Location, r.Kind);
        Assert.Equal(input, r.Text);
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var r = PathParser.Parse("%USERPROFILE%", null);
        Assert.Equal(AddressKind.Location, r.Kind);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), r.Text, ignoreCase: true);
    }

    [Fact]
    public void Relative_paths_resolve_against_current_folder()
    {
        var r = PathParser.Parse(@"src\app", _root);
        Assert.Equal(AddressKind.Location, r.Kind);
        Assert.Equal(Path.Combine(_root, "src", "app"), r.Text, ignoreCase: true);

        var up = PathParser.Parse("..", Path.Combine(_root, "src"));
        Assert.Equal(_root.TrimEnd('\\'), up.Text, ignoreCase: true);
    }

    [Theory]
    [InlineData("This PC", ShellLocation.Known.ThisPC)]
    [InlineData("recycle bin", ShellLocation.Known.RecycleBin)]
    [InlineData("Downloads", "shell:Downloads")]
    public void Friendly_names_map_to_shell_locations(string input, string expected)
    {
        var r = PathParser.Parse(input, _root);
        Assert.Equal(AddressKind.Location, r.Kind);
        Assert.Equal(expected, r.Text);
    }

    [Fact]
    public void Existing_subfolder_wins_over_alias()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Downloads"));
        var r = PathParser.Parse("Downloads", _root);
        Assert.Equal(Path.Combine(_root, "Downloads"), r.Text, ignoreCase: true);
    }

    [Theory]
    [InlineData("cmd", "cmd", null)]
    [InlineData("code .", "code", ".")]
    [InlineData(@"""C:\My Tools\x.exe"" -v", @"C:\My Tools\x.exe", "-v")]
    public void Unknown_text_is_a_command(string input, string exe, string? args)
    {
        var r = PathParser.Parse(input, _root);
        Assert.Equal(AddressKind.Command, r.Kind);
        Assert.Equal(exe, r.Text);
        Assert.Equal(args, r.Arguments);
    }

    [Fact]
    public void Urls_are_urls() => Assert.Equal(AddressKind.Url, PathParser.Parse("https://example.com", null).Kind);
}

public class ExplorerCommandLineTests
{
    [Fact]
    public void Select_with_comma_in_one_argument()
    {
        var c = ExplorerCommandLine.Parse([@"/select,C:\Windows\notepad.exe"]);
        Assert.Equal(@"C:\Windows\notepad.exe", c.SelectPath);
        Assert.Empty(c.Paths);
    }

    [Fact]
    public void Select_split_by_shell_and_quoted()
    {
        var c = ExplorerCommandLine.Parse(["/select,", @"C:\Program Files\a, b.txt"]);
        Assert.Equal(@"C:\Program Files\a, b.txt", c.SelectPath);
    }

    [Fact]
    public void New_window_explore_and_path()
    {
        var c = ExplorerCommandLine.Parse(["/n,/e,", @"C:\Users"]);
        Assert.True(c.NewWindow);
        Assert.Equal([@"C:\Users"], c.Paths);
    }

    [Fact]
    public void Shell_paths_are_kept()
    {
        var c = ExplorerCommandLine.Parse(["shell:Downloads"]);
        Assert.Equal(["shell:Downloads"], c.Paths);
    }

    [Fact]
    public void Root_switch_is_a_path()
    {
        var c = ExplorerCommandLine.Parse([@"/root,C:\Temp"]);
        Assert.Equal([@"C:\Temp"], c.Paths);
    }
}
