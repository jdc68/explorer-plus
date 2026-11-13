using ExplorerPlus.Core.Shell;
using Xunit;

namespace ExplorerPlus.Core.Tests;

public class GitStatusTests
{
    private static readonly string Output = string.Join('\0',
        " M src/App.cs",
        "M  src/Staged.cs",
        "A  src/New.cs",
        "R  src/Renamed.cs", "src/Old.cs",
        "?? notes.txt",
        "?? scratch/",
        "!! bin/",
        "!! src/obj/",
        "UU src/Conflict.cs",
        " D docs/Gone.md",
        "");

    [Fact]
    public void Files_get_their_own_state()
    {
        var s = GitStatus.Parse(Output);
        Assert.Equal(GitState.Modified, GitStatus.StateOf(s, "src/App.cs", false));
        Assert.Equal(GitState.Staged, GitStatus.StateOf(s, "src/Staged.cs", false));
        Assert.Equal(GitState.Added, GitStatus.StateOf(s, "src/New.cs", false));
        Assert.Equal(GitState.Renamed, GitStatus.StateOf(s, "src/Renamed.cs", false));
        Assert.Equal(GitState.Untracked, GitStatus.StateOf(s, "notes.txt", false));
        Assert.Equal(GitState.Conflict, GitStatus.StateOf(s, "src/Conflict.cs", false));
        Assert.Equal(GitState.Deleted, GitStatus.StateOf(s, "docs/Gone.md", false));
        Assert.Equal(GitState.Clean, GitStatus.StateOf(s, "src/Other.cs", false));
        Assert.False(s.Files.ContainsKey("src/Old.cs"));
    }

    [Fact]
    public void Untracked_and_ignored_folders_cover_their_contents()
    {
        var s = GitStatus.Parse(Output);
        Assert.Equal(GitState.Untracked, GitStatus.StateOf(s, "scratch", true));
        Assert.Equal(GitState.Untracked, GitStatus.StateOf(s, "scratch/a/b.txt", false));
        Assert.Equal(GitState.Ignored, GitStatus.StateOf(s, "bin", true));
        Assert.Equal(GitState.Ignored, GitStatus.StateOf(s, "src/obj/x.dll", false));
    }

    [Fact]
    public void Folders_show_the_most_pressing_change_inside()
    {
        var s = GitStatus.Parse(Output);
        Assert.Equal(GitState.Conflict, GitStatus.StateOf(s, "src", true));
        Assert.Equal(GitState.Modified, GitStatus.StateOf(s, "docs", true));
        Assert.Equal(GitState.Clean, GitStatus.StateOf(s, "tests", true));
        Assert.Equal(GitState.Clean, GitStatus.StateOf(s, "sr", true));
    }
}
