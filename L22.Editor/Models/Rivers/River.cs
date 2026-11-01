using System.Collections.Generic;

namespace L22.Editor.Models.Rivers;

// A single river. Vertices form a polyline (eventually bezier-sampled
// at render time); branches are child rivers that fork off from this
// one at the parent vertex matching ForkFromIndex. The editor treats
// branches as full rivers in their own right so Width edits and
// re-ordering work recursively.
public sealed class River
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<RiverVertex> Vertices { get; set; } = new();
    public List<River> Branches { get; set; } = new();
    // Index into Vertices this river forks off from, when it is itself
    // the branch of a parent. Ignored for top-level rivers (whose
    // ForkFromIndex stays at the default 0).
    public int ForkFromIndex { get; set; }
}
