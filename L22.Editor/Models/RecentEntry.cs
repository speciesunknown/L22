namespace L22.Editor.Models;

// One entry in the recent-files list. Persistence is deferred to plan 002;
// for now the list is session-scoped and starts empty.
public sealed class RecentEntry
{
    public string Name { get; }
    public string Path { get; }
    public DocumentKind Kind { get; }

    public RecentEntry(string name, string path, DocumentKind kind)
    {
        Name = name;
        Path = path;
        Kind = kind;
    }

    public string Display => $"{(Kind == DocumentKind.Prefab ? "◆ " : "")}{Name}";
    public string Subtitle => string.IsNullOrEmpty(Path) ? "(unsaved)" : Path;
}
