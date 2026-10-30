using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using L22.Editor.Models.Objects;

namespace L22.Editor.Views;

// Modal editor for a PropertyNode tree plus a display name + code name.
// The same dialog edits both ObjectInstance (NameOverride + CodeName +
// PropertyOverrides) and ObjectPrefab (DisplayName + ForeignIdentifier +
// Properties). The caller unpacks the payload after a true result.
public partial class ObjectEditorDialog : Window
{
    private readonly ObjectEditorPayload _payload;
    private readonly ObservableCollection<PropertyNode> _roots;
    private bool _accepted;

    public ObjectEditorDialog() : this(new ObjectEditorPayload()) { }

    public ObjectEditorDialog(ObjectEditorPayload payload)
    {
        InitializeComponent();
        _payload = payload;
        Title = payload.Title;
        LblSubject.Text = payload.SubjectLabel;
        LblCodeName.Text = payload.CodeNameLabel;
        TxtDisplayName.Text = payload.DisplayName;
        TxtCodeName.Text = payload.CodeName;
        _roots = new ObservableCollection<PropertyNode>(payload.Properties);
        PropTree.ItemsSource = _roots;
    }

    public static async Task<bool> ShowAsync(Window owner, ObjectEditorPayload payload)
    {
        var dlg = new ObjectEditorDialog(payload);
        return await dlg.ShowDialog<bool>(owner);
    }

    protected override void OnClosed(System.EventArgs e)
    {
        base.OnClosed(e);
        if (!_accepted) return;
        _payload.DisplayName = TxtDisplayName.Text ?? "";
        _payload.CodeName = TxtCodeName.Text ?? "";
        _payload.Properties = new List<PropertyNode>(_roots);
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    // Insert into the selected group's Children; otherwise append to roots.
    private void InsertNode(PropertyNode node)
    {
        var selected = PropTree.SelectedItem as PropertyNode;
        if (selected is { Kind: PropertyKind.Array or PropertyKind.Properties })
        {
            selected.Children ??= new List<PropertyNode>();
            selected.Children.Add(node);
            // Nudge the tree to redraw by re-assigning its items; the data
            // template reads Children directly.
            PropTree.ItemsSource = null;
            PropTree.ItemsSource = _roots;
        }
        else
        {
            _roots.Add(node);
        }
    }

    private void OnAddText(object? s, RoutedEventArgs e)
        => InsertNode(new PropertyNode { Name = "new_text", Kind = PropertyKind.Text, TextValue = "" });

    private void OnAddNumber(object? s, RoutedEventArgs e)
        => InsertNode(new PropertyNode { Name = "new_number", Kind = PropertyKind.Number, NumberValue = 0 });

    private void OnAddGroup(object? s, RoutedEventArgs e)
        => InsertNode(new PropertyNode { Name = "new_group", Kind = PropertyKind.Properties, Children = new List<PropertyNode>() });

    private void OnAddArray(object? s, RoutedEventArgs e)
        => InsertNode(new PropertyNode { Name = "new_array", Kind = PropertyKind.Array, Children = new List<PropertyNode>() });

    private void OnRemove(object? s, RoutedEventArgs e)
    {
        if (PropTree.SelectedItem is not PropertyNode target) return;
        if (_roots.Remove(target)) return;
        // Depth-first search for the containing parent and drop it.
        foreach (var root in _roots)
            if (RemoveFromChildren(root, target)) { PropTree.ItemsSource = null; PropTree.ItemsSource = _roots; return; }
    }

    private static bool RemoveFromChildren(PropertyNode node, PropertyNode target)
    {
        if (node.Children is null) return false;
        if (node.Children.Remove(target)) return true;
        foreach (var c in node.Children)
            if (RemoveFromChildren(c, target)) return true;
        return false;
    }
}

public sealed class ObjectEditorPayload
{
    public string Title { get; init; } = "Edit";
    public string SubjectLabel { get; init; } = "Object";
    public string CodeNameLabel { get; init; } = "Code name:";
    public string DisplayName { get; set; } = "";
    public string CodeName { get; set; } = "";
    public List<PropertyNode> Properties { get; set; } = new();
}
