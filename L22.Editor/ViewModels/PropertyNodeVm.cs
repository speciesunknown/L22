using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using L22.Editor.Models.Objects;

namespace L22.Editor.ViewModels;

// Mutable observable mirror of a PropertyNode. The property editor binds
// to a tree of these so Avalonia's binding system picks up name / value /
// child-add / child-remove changes without the editor having to reset
// ItemsSource (which collapses the whole tree).
//
// PropertyNode itself stays as pure data — this VM is the layer the UI
// edits. OnOk the editor walks the VM tree back into a fresh PropertyNode
// list and hands it to EditInstanceCommand.
public sealed class PropertyNodeVm : INotifyPropertyChanged
{
    private string _name = "";
    private PropertyKind _kind = PropertyKind.Text;
    private string _textValue = "";
    private double _numberValue;

    public string Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(DisplayLine)); } }
    public PropertyKind Kind { get => _kind; private set => Set(ref _kind, value); }
    public string TextValue { get => _textValue; set { if (Set(ref _textValue, value)) Raise(nameof(DisplayLine)); } }
    public double NumberValue { get => _numberValue; set { if (Set(ref _numberValue, value)) Raise(nameof(DisplayLine)); } }

    // Combined value text the editor's TextBox two-way binds against.
    // Kind-aware: writes to TextValue for Text nodes, parses to double for
    // Number nodes, no-op for groups.
    public string ValueDisplay
    {
        get => _kind switch
        {
            PropertyKind.Text => _textValue,
            PropertyKind.Number => _numberValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "",
        };
        set
        {
            if (_kind == PropertyKind.Text) TextValue = value;
            else if (_kind == PropertyKind.Number &&
                     double.TryParse(value, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var d))
                NumberValue = d;
        }
    }

    // ObservableCollection so adding/removing children notifies the
    // TreeView; nodes never get null children here, which also kills the
    // "null-vs-empty-vs-collapsed" ambiguity.
    public ObservableCollection<PropertyNodeVm> Children { get; } = new();

    public string DisplayLine => Kind switch
    {
        PropertyKind.Text => $"{Name}: {TextValue}",
        PropertyKind.Number => $"{Name}: {NumberValue}",
        PropertyKind.Array => $"{Name}  [{Children.Count}]",
        PropertyKind.Properties => $"{Name}  {{{Children.Count}}}",
        _ => Name,
    };

    public bool IsGroup => Kind is PropertyKind.Array or PropertyKind.Properties;

    public PropertyNodeVm(string name, PropertyKind kind)
    {
        _name = name;
        _kind = kind;
        Children.CollectionChanged += (_, _) => Raise(nameof(DisplayLine));
    }

    public static PropertyNodeVm From(PropertyNode node)
    {
        var vm = new PropertyNodeVm(node.Name, node.Kind)
        {
            _textValue = node.TextValue ?? "",
            _numberValue = node.NumberValue ?? 0,
        };
        if (node.Children is not null)
            foreach (var c in node.Children) vm.Children.Add(From(c));
        return vm;
    }

    public PropertyNode ToModel()
    {
        var model = new PropertyNode
        {
            Name = Name,
            Kind = Kind,
            TextValue = Kind == PropertyKind.Text ? TextValue : null,
            NumberValue = Kind == PropertyKind.Number ? NumberValue : null,
        };
        if (Kind is PropertyKind.Array or PropertyKind.Properties)
        {
            model.Children = new List<PropertyNode>(Children.Count);
            foreach (var c in Children) model.Children.Add(c.ToModel());
        }
        return model;
    }

    public static List<PropertyNode> ToModelList(IEnumerable<PropertyNodeVm> roots)
    {
        var list = new List<PropertyNode>();
        foreach (var r in roots) list.Add(r.ToModel());
        return list;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
