using ExplorerPlus.Core.Integration;

namespace ExplorerPlus.Core.Tests;

public class InheritedEnvironmentTests
{
    [Theory]
    [InlineData("CLAUDECODE", true)]
    [InlineData("CLAUDE_PID", true)]
    [InlineData("CLAUDE_EFFORT", true)]
    [InlineData("CLAUDE_CODE_CHILD_SESSION", true)]
    [InlineData("claude_code_session_id", true)]
    [InlineData("CLAUDE_CONFIG_DIR", false)]
    [InlineData("PATH", false)]
    public void Recognises_claude_code_variables(string name, bool expected) =>
        Assert.Equal(expected, InheritedEnvironment.IsClaudeVariable(name));

    [Fact]
    public void Removes_claude_code_variables_and_keeps_the_rest()
    {
        var leaked = "CLAUDE_CODE_TEST_" + Guid.NewGuid().ToString("N");
        var kept = "EXPLORERPLUS_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(leaked, "1");
        Environment.SetEnvironmentVariable(kept, "1");
        try
        {
            var removed = InheritedEnvironment.RemoveClaudeVariables();

            Assert.Contains(leaked, removed);
            Assert.Null(Environment.GetEnvironmentVariable(leaked));
            Assert.Equal("1", Environment.GetEnvironmentVariable(kept));
        }
        finally
        {
            Environment.SetEnvironmentVariable(leaked, null);
            Environment.SetEnvironmentVariable(kept, null);
        }
    }
}
