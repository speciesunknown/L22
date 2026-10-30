using System;

namespace L22.Editor.Models;

// One tab in the editor. Wraps a full EditorContext (world + history + brush +
// tools) with a user-visible name and the document kind.
public sealed class OpenDocument
{
    public EditorContext Context { get; }
    public DocumentKind Kind { get; }
    public string Name { get; set; }

    // File path on disk once saved. Null for new documents.
    public string? FilePath { get; set; }

    // Camera state is per-document so switching tabs restores the view the
    // user left behind. Nullable until the first time the tab becomes active.
    public CameraSnapshot? EditCamera;
    public CameraSnapshot? FreeCamera;

    public OpenDocument(EditorContext ctx, DocumentKind kind, string name)
    {
        Context = ctx;
        Kind = kind;
        Name = name;
    }

    public string TabLabel => Kind == DocumentKind.Prefab ? $"◆ {Name}" : Name;
}
