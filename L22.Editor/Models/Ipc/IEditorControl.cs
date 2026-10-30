using System.Collections.Generic;
using System.Threading.Tasks;

namespace L22.Editor.Models.Ipc;

// Admin-level operations an agent can trigger over the socket. The IPC
// server is given one of these at startup; it owns the MainWindow /
// MainViewModel references so the server itself doesn't need to.
public interface IEditorControl
{
    // Creates a fresh scene or prefab and makes it active. Returns the new
    // document's fileId.
    Task<string> NewScene();
    Task<string> NewPrefab();

    // Captures the given viewport ("edit" or "free") to the specified path
    // on the local filesystem. Returns the captured pixel dimensions.
    Task<(int w, int h)> Screenshot(string viewportName, string path);

    // List of currently open documents.
    IEnumerable<DocumentInfo> ListDocuments();

    // The currently active document's context, if any — used when an agent
    // opens an admin session (fileId = "*") and then issues doc-level ops.
    EditorContext? GetActiveContext();
}

public sealed record DocumentInfo(string FileId, string Name, string Kind);
