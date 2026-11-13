namespace ExplorerPlus.App.Services;

public sealed class Workspace
{
    public string Name { get; set; } = "";
    public List<WorkspaceTab> Tabs { get; set; } = [];
    public int Active { get; set; }
}

public sealed class WorkspaceTab
{
    public string Left { get; set; } = "";
    public string? Right { get; set; }
    public string? LeftTitle { get; set; }
    public string? RightTitle { get; set; }
}

public static class Workspaces
{
    public static IReadOnlyList<Workspace> All =>
        AppSettings.Current.Workspaces.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static Workspace? Find(string name) =>
        AppSettings.Current.Workspaces.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.CurrentCultureIgnoreCase));

    // true when it replaced one with the same name
    public static bool Save(Workspace workspace)
    {
        var list = AppSettings.Current.Workspaces;
        var replaced = list.RemoveAll(w => string.Equals(w.Name, workspace.Name, StringComparison.CurrentCultureIgnoreCase)) > 0;
        list.Add(workspace);
        AppSettings.Current.Save();
        return replaced;
    }

    public static void Delete(string name)
    {
        if (AppSettings.Current.Workspaces.RemoveAll(w => string.Equals(w.Name, name, StringComparison.CurrentCultureIgnoreCase)) > 0)
            AppSettings.Current.Save();
    }

    public static string Describe(Workspace workspace) =>
        string.Join("  ·  ", workspace.Tabs.Select(t =>
            t.Right is null ? t.LeftTitle ?? t.Left : $"{t.LeftTitle ?? t.Left} | {t.RightTitle ?? t.Right}"));
}
