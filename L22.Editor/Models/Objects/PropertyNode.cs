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

    // Two-way bound in the object editor dialog: reads TextValue or
    // NumberValue depending on Kind; silently coerces strings to doubles
    // for number nodes (bad input leaves the previous number intact).
    [System.Text.Json.Serialization.JsonIgnore]
    public string ValueDisplay
    {
        get => Kind switch
        {
            PropertyKind.Text   => TextValue ?? "",
            PropertyKind.Number => NumberValue?.ToString() ?? "",
            _ => "",
        };
        set
        {
            if (Kind == PropertyKind.Text) TextValue = value;
            else if (Kind == PropertyKind.Number
                     && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var d))
                NumberValue = d;
        }
    }

    public PropertyNode DeepClone()
    {
        var copy = new PropertyNode
        {
            Name = Name,
            Kind = Kind,
            TextValue = TextValue,
            NumberValue = NumberValue,
        };
        if (Children is { Count: > 0 })
        {
            copy.Children = new List<PropertyNode>(Children.Count);
            foreach (var c in Children) copy.Children.Add(c.DeepClone());
        }
        return copy;
    }

    public static List<PropertyNode> DeepClone(List<PropertyNode> src)
    {
        var copy = new List<PropertyNode>(src.Count);
        foreach (var c in src) copy.Add(c.DeepClone());
        return copy;
    }
}
