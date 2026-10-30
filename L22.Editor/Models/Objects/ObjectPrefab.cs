using System.Collections.Generic;

namespace L22.Editor.Models.Objects;

// A reusable object template. Lives in the project's sibling Prefabs/
// library as a <id>.prefab.json file. Instances in a scene reference
// prefabs by Id and may override individual fields.
public sealed class ObjectPrefab
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ForeignIdentifier { get; set; } = "";
    public IconRef? Icon { get; set; }
    public List<PropertyNode> Properties { get; set; } = new();
}
