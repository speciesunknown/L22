using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using L22.Editor.Models.Objects;
using L22.Editor.ViewModels;

namespace L22.Editor.Views;

// Modal editor for a PropertyNode tree plus a display name + code name.
// The TreeView binds to PropertyNodeVm (observable); the raw PropertyNode
// model is only produced on OK via PropertyNodeVm.ToModelList and handed
// to the caller for a command push.
//
// The same dialog edits both ObjectInstance (NameOverride + CodeName +
// PropertyOverrides) and ObjectPrefab (DisplayName + ForeignIdentifier +
// Properties). The caller unpacks the payload after a true result.
public partial class ObjectEditorDialog : Window
{
    private readonly ObjectEditorPayload _payload;
    private readonly ObservableCollection<PropertyNodeVm> _roots;
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
        _roots = new ObservableCollection<PropertyNodeVm>();
        _roots.CollectionChanged += OnRootsChanged;
        foreach (var p in payload.Properties) _roots.Add(PropertyNodeVm.From(p));
        PropTree.ItemsSource = _roots;

        if (payload.ShowPrefabPicker)
        {
            GridPrefabPicker.IsVisible = true;
            var sorted = new List<ObjectPrefab>(payload.AvailablePrefabs);
            sorted.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName,
                System.StringComparison.OrdinalIgnoreCase));
            CbPrefabPicker.ItemsSource = sorted;
            CbPrefabPicker.ValueMemberBinding = new Avalonia.Data.Binding(nameof(ObjectPrefab.DisplayName));
            CbPrefabPicker.ItemFilter = (search, item) =>
            {
                if (item is not ObjectPrefab p) return false;
                if (string.IsNullOrEmpty(search)) return true;
                var s = search.ToLowerInvariant();
                return (p.DisplayName?.ToLowerInvariant().Contains(s) ?? false)
                    || (p.ForeignIdentifier?.ToLowerInvariant().Contains(s) ?? false)
                    || (p.Id?.ToLowerInvariant().Contains(s) ?? false);
            };
            if (!string.IsNullOrEmpty(payload.SelectedPrefabId))
            {
                foreach (var pf in sorted)
                {
                    if (pf.Id == payload.SelectedPrefabId)
                    {
                        CbPrefabPicker.SelectedItem = pf;
                        BtnEditPrefab.IsVisible = true;
                        break;
                    }
                }
            }
            CbPrefabPicker.SelectionChanged += (_, _) =>
                BtnEditPrefab.IsVisible = CbPrefabPicker.SelectedItem is ObjectPrefab;
        }
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
        _payload.Properties = PropertyNodeVm.ToModelList(_roots);
        if (_payload.ShowPrefabPicker)
            _payload.SelectedPrefabId = CbPrefabPicker.SelectedItem is ObjectPrefab pf ? pf.Id : null;
    }

    private void OnEditPrefabFromDialog(object? sender, RoutedEventArgs e)
    {
        if (CbPrefabPicker.SelectedItem is ObjectPrefab pf)
            _payload.RaiseEditPrefabRequested(pf.Id);
    }

    private void OnPrefabPickerFocus(object? sender, RoutedEventArgs _)
    {
        if (CbPrefabPicker.ItemsSource is not null)
            CbPrefabPicker.IsDropDownOpen = true;
    }

    private void OnClearPrefabPicker(object? sender, RoutedEventArgs e)
    {
        CbPrefabPicker.SelectedItem = null;
        CbPrefabPicker.Text = "";
        BtnEditPrefab.IsVisible = false;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    // Enter in the main form fields commits the current text (LostFocus
    // flushes the two-way binding) and tabs to the next logical field.
    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (ReferenceEquals(sender, TxtDisplayName))
        {
            TxtCodeName.Focus();
            e.Handled = true;
        }
        else if (ReferenceEquals(sender, TxtCodeName))
        {
            if (GridPrefabPicker.IsVisible) CbPrefabPicker.Focus();
            else PropTree.Focus();
            e.Handled = true;
        }
    }

    // Enter inside a tree row's Name TextBox jumps to that row's Value
    // TextBox. Enter inside the Value jumps to the Name field of the next
    // sibling in the parent list — Enter effectively walks down the tree
    // row by row, which matches the common Excel-style "finish this cell
    // and go to the one below" expectation.
    private void OnTreeRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb) return;

        var row = tb.FindAncestorOfType<StackPanel>();
        if (row is null) return;

        if (tb.Tag as string == "name")
        {
            // Name → Value in the same row.
            foreach (var child in row.GetVisualChildren())
                if (child is TextBox other && other.Tag as string == "value")
                {
                    other.Focus();
                    e.Handled = true;
                    return;
                }
        }
        else if (tb.Tag as string == "value")
        {
            // Value → next sibling TreeViewItem's Name box, if we can
            // find one. If not, swallow Enter so the dialog doesn't
            // commit (OK is not IsDefault, but a bubbling KeyDown could
            // still trigger focused-button activation).
            e.Handled = true;
            var treeItem = tb.FindAncestorOfType<TreeViewItem>();
            if (treeItem?.Parent is ItemsControl ic)
            {
                int idx = ic.IndexFromContainer(treeItem);
                if (idx >= 0 && idx + 1 < ic.ItemCount)
                {
                    if (ic.ContainerFromIndex(idx + 1) is TreeViewItem nextRow)
                    {
                        var nextName = nextRow.FindDescendantOfType<TextBox>();
                        nextName?.Focus();
                    }
                }
            }
        }
    }

    // Insert into the currently-selected group VM's Children. Falls through
    // to the root collection when nothing (or a leaf) is selected. The
    // ObservableCollection updates fire tree notifications — no manual
    // ItemsSource reset, so the TreeView keeps its expand state.
    private void InsertNode(PropertyNodeVm node)
    {
        if (PropTree.SelectedItem is PropertyNodeVm sel && sel.IsGroup)
            sel.Children.Add(node);
        else
            _roots.Add(node);
    }

    private void OnAddText(object? s, RoutedEventArgs e)
        => InsertNode(new PropertyNodeVm("new_text", PropertyKind.Text));

    private void OnAddNumber(object? s, RoutedEventArgs e)
        => InsertNode(new PropertyNodeVm("new_number", PropertyKind.Number));

    private void OnAddGroup(object? s, RoutedEventArgs e)
        => InsertNode(new PropertyNodeVm("new_group", PropertyKind.Properties));

    private void OnAddArray(object? s, RoutedEventArgs e)
        => InsertNode(new PropertyNodeVm("new_array", PropertyKind.Array));

    private void OnRemove(object? s, RoutedEventArgs e)
    {
        if (PropTree.SelectedItem is not PropertyNodeVm target) return;
        if (_roots.Remove(target)) return;
        foreach (var r in _roots)
            if (RemoveFromChildren(r, target)) return;
    }

    private static bool RemoveFromChildren(PropertyNodeVm node, PropertyNodeVm target)
    {
        if (node.Children.Remove(target)) return true;
        foreach (var c in node.Children)
            if (RemoveFromChildren(c, target)) return true;
        return false;
    }

    // _roots is the sibling pool for the top-level VMs. The parent-VM
    // Children collection self-manages via PropertyNodeVm.OnChildrenChanged;
    // the dialog owns _roots so it does the same bookkeeping manually.
    private void OnRootsChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (PropertyNodeVm c in e.NewItems) c.SiblingList = _roots;
        if (e.OldItems is not null)
            foreach (PropertyNodeVm c in e.OldItems)
                if (ReferenceEquals(c.SiblingList, _roots)) c.SiblingList = null;
        foreach (var r in _roots) r.RaiseSiblingMoveFlags();
    }

    // Journey 008 scenario 2: pressing anywhere on a group row (that isn't
    // an interactive child like a TextBox or Button) toggles the TreeViewItem.
    // Leaf rows are no-ops.
    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control src)
        {
            if (src is TextBox or Button) return;
            if (src.FindAncestorOfType<TextBox>() is not null) return;
            if (src.FindAncestorOfType<Button>() is not null) return;
        }
        if (sender is not StackPanel sp) return;
        if (sp.DataContext is not PropertyNodeVm vm) return;
        if (!vm.IsGroup || vm.Children.Count == 0) return;
        var tvi = sp.FindAncestorOfType<TreeViewItem>();
        if (tvi is not null) tvi.IsExpanded = !tvi.IsExpanded;
    }

    private void OnMoveNodeUp(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is PropertyNodeVm vm) vm.MoveUp();
    }

    private void OnMoveNodeDown(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.DataContext is PropertyNodeVm vm) vm.MoveDown();
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

    public bool ShowPrefabPicker { get; init; }
    public List<L22.Editor.Models.Objects.ObjectPrefab> AvailablePrefabs { get; init; } = new();
    public string? SelectedPrefabId { get; set; }
    public event System.Action<string>? EditPrefabRequested;
    internal void RaiseEditPrefabRequested(string id) => EditPrefabRequested?.Invoke(id);
}
