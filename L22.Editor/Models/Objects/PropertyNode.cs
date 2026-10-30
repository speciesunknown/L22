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
}
