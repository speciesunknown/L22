using System.Collections.Generic;

namespace L22.Editor.Models.Objects;

public enum PropertyKind
{
    Text,
    Number,
    Array,       // positional children — children's Name is ignored on display
    Properties,  // named children — a nested dictionary
}

// Recursive tree node. Text/Number are leaves; Array/Properties carry
// child nodes. Only one of TextValue/NumberValue/Children is meaningful
// for a given Kind — the others round-trip as default values but shouldn't
// be read by consumers.
public sealed class PropertyNode
{
    public string Name { get; set; } = "";
    public PropertyKind Kind { get; set; } = PropertyKind.Text;

    // Kind == Text
    public string? TextValue { get; set; }

    // Kind == Number
    public double? NumberValue { get; set; }

    // Kind == Array | Properties
    public List<PropertyNode>? Children { get; set; }

    // Used by the UI tree view's data template. Not persisted.
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayLine => Kind switch
    {
        PropertyKind.Text       => $"{Name}: {TextValue}",
        PropertyKind.Number     => $"{Name}: {NumberValue}",
        PropertyKind.Array      => $"{Name}  [{Children?.Count ?? 0}]",
        PropertyKind.Properties => $"{Name}  {{{Children?.Count ?? 0}}}",
        _ => Name,
    };
}
