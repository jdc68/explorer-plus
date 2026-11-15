using System.Collections;

namespace ExplorerPlus.Core.Integration;

public static class InheritedEnvironment
{
    public static bool IsClaudeVariable(string name) =>
        name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLAUDE_PID", StringComparison.OrdinalIgnoreCase)
        || name.Equals("CLAUDE_EFFORT", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase);

    // Explorer+ started from Claude Code would hand its session to every terminal it opens (#26)
    public static List<string> RemoveClaudeVariables()
    {
        var removed = new List<string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && IsClaudeVariable(name))
            {
                Environment.SetEnvironmentVariable(name, null);
                removed.Add(name);
            }
        }
        return removed;
    }
}
