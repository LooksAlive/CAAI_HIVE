using System.Collections.ObjectModel;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Microsoft.CodeAnalysis.Scripting;

namespace CAAI;

public class ObjectNode
{
    private const int PageSize = 100;
    private const int MaxDepth = 12;
    private readonly object[] _ancestors = [];
    private readonly int _depth;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ObjectNode>>>? _loader;
    private Func<object?>? _getter;
    private Type? _declaredType;

    public string Name { get; set; }
    public string Type { get; set; }
    public string Value { get; set; }
    public object? RawValue { get; set; }
    public List<ObjectNode>? Children { get; private set; }
    public bool ChildrenLoaded { get; private set; }
    public bool IsDeferred => _getter != null;
    public bool IsError { get; private set; }
    public bool IsGroup { get; private set; }
    public string Kind { get; private set; } = "Variable";
    public bool CanExpand => Children != null;
    public string FullValue => RawValue is string text ? text : Value;

    public ObjectNode(string name, object? value) : this(name, value, value?.GetType(), [], 0)
    {
    }

    public ObjectNode(string name, object? value, Type? declaredType) : this(name, value, declaredType, [], 0)
    {
    }

    public ObjectNode(string name, string type, string displayValue,
        Func<CancellationToken, Task<IReadOnlyList<ObjectNode>>>? loadChildrenAsync = null)
    {
        Name = name;
        Type = type;
        Value = displayValue;
        _loader = loadChildrenAsync;
        if (_loader != null) Children = [];
    }

    private ObjectNode(string name, object? value, Type? declaredType, object[] ancestors, int depth)
    {
        Name = name;
        RawValue = value;
        _declaredType = declaredType;
        _ancestors = ancestors;
        _depth = depth;
        var type = value?.GetType() ?? declaredType;
        Type = FormatType(type);
        Value = FormatValue(value);
        if (value == null || IsScalar(type!)) return;
        if (!type!.IsValueType && ancestors.Any(ancestor => ReferenceEquals(ancestor, value)))
            Value += " (circular reference)";
        else if (depth >= MaxDepth)
            Value += " (depth limit)";
        else
            Children = [];
    }

    public static string FormatType(Type? type)
    {
        if (type == null) return "null";
        if (type.IsArray) return $"{FormatType(type.GetElementType())}[{new string(',', type.GetArrayRank() - 1)}]";
        if (!type.IsGenericType) return type.Name;
        var name = type.Name.Split('`')[0];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatType))}>";
    }

    private static bool IsScalar(Type type) => type.IsPrimitive || type.IsEnum ||
        type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri);

    private static string FormatValue(object? value)
    {
        try
        {
            return value switch
            {
                null => "null",
                string text => JsonSerializer.Serialize(text.Length > 200 ? text[..200] + "..." : text),
                char character => $"'{character}'",
                bool boolean => boolean ? "true" : "false",
                DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
                TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
                Guid identifier => identifier.ToString(),
                Uri uri => uri.OriginalString,
                Array array => $"Count = {array.Length}",
                ICollection collection => $"Count = {collection.Count}",
                _ when value.GetType().IsPrimitive || value is decimal || value.GetType().IsEnum =>
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null",
                _ => $"{{{FormatType(value.GetType())}}}"
            };
        }
        catch (Exception exception) { return $"<unavailable: {GetError(exception)}>"; }
    }

    public void LoadChildren()
    {
        if (ChildrenLoaded || RawValue == null || Children == null || _loader != null) return;
        var children = new List<ObjectNode>();
        try
        {
            if (RawValue is Array array)
                children.AddRange(ReadArray(array, 0));
            else if (RawValue is IDictionary dictionary)
                children.AddRange(ReadDictionary(dictionary, 0));
            else if (RawValue is IList list)
                children.AddRange(ReadList(list, 0));
            else
            {
                if (RawValue is IEnumerable enumerable)
                {
                    children.Add(new ObjectNode("Results", "IEnumerable", "Enumerates up to 100 items",
                        token => Task.Run<IReadOnlyList<ObjectNode>>(() => ReadEnumerable(enumerable, token), token))
                    { Kind = "Enumeration", IsGroup = true });
                }
                var type = RawValue.GetType();
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(field => field.Name).Take(200))
                {
                    try { children.Add(Child(field.Name, field.GetValue(RawValue), field.FieldType, "Field")); }
                    catch (Exception exception) { children.Add(Error(field.Name, exception)); }
                }
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.GetMethod?.IsPublic == true).OrderBy(property => property.Name).Take(200))
                {
                    if (property.GetIndexParameters().Length != 0) continue;
                    var node = Child(property.Name, null, property.PropertyType, "Property");
                    node.Value = "<not evaluated>";
                    node._getter = () => property.GetValue(RawValue);
                    children.Add(node);
                }
            }
        }
        catch (Exception exception) { children.Add(Error("Evaluation", exception)); }
        Children = children;
        ChildrenLoaded = true;
    }

    public async Task<IReadOnlyList<ObjectNode>> LoadChildrenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ChildrenLoaded || Children == null) return Children ?? [];
        if (_loader == null)
            await Task.Run(LoadChildren, cancellationToken).WaitAsync(cancellationToken);
        else
        {
            var children = await _loader(cancellationToken).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Children = children.ToList();
            ChildrenLoaded = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Children ?? [];
    }

    public async Task<ObjectNode> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        if (_getter == null) return this;
        try
        {
            var value = await Task.Run(_getter, cancellationToken).WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new ObjectNode(Name, value, _declaredType, _ancestors, _depth) { Kind = Kind };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { return Error(Name, exception); }
    }

    private ObjectNode Child(string name, object? value, Type? declaredType, string kind = "Item") =>
        new(name, value, declaredType, [.. _ancestors, RawValue!], _depth + 1) { Kind = kind };

    private static string GetError(Exception exception) =>
        (exception is TargetInvocationException { InnerException: { } inner } ? inner : exception).Message;

    private static ObjectNode Error(string name, Exception exception) =>
        new(name, "Error", GetError(exception)) { IsError = true, Kind = "Error" };

    private ObjectNode More(int offset, Func<IReadOnlyList<ObjectNode>> load) =>
        new($"Items [{offset}...]", "Page", "Next 100 items",
            token => Task.Run(load, token)) { IsGroup = true, Kind = "Page" };

    private IReadOnlyList<ObjectNode> ReadArray(Array array, int offset)
    {
        var result = new List<ObjectNode>();
        var end = (int)Math.Min((long)offset + PageSize, array.Length);
        for (var index = offset; index < end; index++)
        {
            var remaining = index;
            var indices = new int[array.Rank];
            for (var dimension = array.Rank - 1; dimension >= 0; dimension--)
            {
                indices[dimension] = remaining % array.GetLength(dimension) + array.GetLowerBound(dimension);
                remaining /= array.GetLength(dimension);
            }
            result.Add(Child($"[{string.Join(", ", indices)}]", array.GetValue(indices), array.GetType().GetElementType()));
        }
        if (end < array.Length) result.Add(More(end, () => ReadArray(array, end)));
        return result;
    }

    private IReadOnlyList<ObjectNode> ReadList(IList list, int offset)
    {
        var result = new List<ObjectNode>();
        var count = list.Count;
        var end = (int)Math.Min((long)offset + PageSize, count);
        for (var index = offset; index < end; index++)
        {
            try { result.Add(Child($"[{index}]", list[index], null)); }
            catch (Exception exception) { result.Add(Error($"[{index}]", exception)); }
        }
        if (end < count) result.Add(More(end, () => ReadList(list, end)));
        return result;
    }

    private IReadOnlyList<ObjectNode> ReadDictionary(IDictionary dictionary, int offset)
    {
        var result = new List<ObjectNode>();
        var enumerator = dictionary.GetEnumerator();
        try
        {
            var index = 0;
            while (index < offset && enumerator.MoveNext()) index++;
            while (index < (long)offset + PageSize && enumerator.MoveNext())
            {
                var entry = enumerator.Entry;
                var name = $"[{FormatValue(entry.Key)}]";
                var node = Child(name, entry.Value, null);
                result.Add(node);
                index++;
            }
            if (index < dictionary.Count) result.Add(More(index, () => ReadDictionary(dictionary, index)));
        }
        finally { (enumerator as IDisposable)?.Dispose(); }
        return result;
    }

    private IReadOnlyList<ObjectNode> ReadEnumerable(IEnumerable enumerable, CancellationToken token)
    {
        var result = new List<ObjectNode>();
        var enumerator = enumerable.GetEnumerator();
        try
        {
            while (result.Count < PageSize && enumerator.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                result.Add(Child($"[{result.Count}]", enumerator.Current, null));
            }
            if (result.Count == PageSize)
                result.Add(new ObjectNode("Limit", "", "First 100 results only"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { result.Add(Error("Enumeration", exception)); }
        finally { (enumerator as IDisposable)?.Dispose(); }
        return result;
    }
}

public class ObjectInspectorWidget : UserControl
{
    private sealed record NodeView(ObjectNode Node, string Path, string Expression, string RootName);

    private readonly TreeView _tree = new()
    {
        Background = Brushes.Transparent,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly ObservableCollection<ObjectNode> _rootNodes = [];
    private readonly ObservableCollection<TreeViewItem> _items = [];
    private readonly Dictionary<string, TreeViewItem> _visibleItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (object? Value, Type? Type)> _liveValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pinnedNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedPaths = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private Dictionary<string, string> _previousValues = new(StringComparer.Ordinal);
    private Dictionary<string, string> _currentValues = new(StringComparer.Ordinal);
    private readonly TextBox _search = new() { Watermark = "Filter variables...", Margin = new Thickness(8, 2, 8, 6), FontSize = 12 };
    private readonly CheckBox _pinnedOnly = new() { Content = "Pinned", FontSize = 11, MinHeight = 28 };
    private readonly TextBlock _context = new() { Text = "No session", FontSize = 10, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { FontSize = 10, Foreground = Brushes.Gray, Margin = new Thickness(8, 5), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _empty = new()
    {
        Text = "No variables",
        Foreground = Brushes.Gray,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _detailName = new() { Text = "No selection", FontWeight = FontWeight.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detailType = new() { FontSize = 10, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 5) };
    private readonly TextBox _detailValue = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Cascadia Code,Consolas,Monospace"),
        FontSize = 12,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0)
    };
    private readonly Button _evaluate = new() { Content = "Evaluate getter", FontSize = 11, Padding = new Thickness(8, 3), IsVisible = false };
    private readonly Button _copy;
    private readonly Button _pin;
    private CancellationTokenSource _renderCts = new();
    private Func<IEnumerable<ObjectNode>>? _snapshotFactory;
    private string? _selectedPath;
    private bool _rendering;

    public event Action? RefreshRequested;

    public ObjectInspectorWidget()
    {
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 35));
        Foreground = Brushes.White;
        _tree.ItemsSource = _items;
        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _tree.SizeChanged += (_, _) =>
        {
            foreach (var item in _visibleItems.Values)
                if (item.Header is Grid header && item.Tag is NodeView view) SetRowWidth(header, view);
        };
        _tree.SelectionChanged += (_, _) =>
        {
            if (_rendering) return;
            if (_tree.SelectedItem is TreeViewItem { Tag: NodeView view }) _selectedPath = view.Path;
            UpdateDetails();
        };

        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto"), Margin = new Thickness(10, 8, 10, 4) };
        heading.Children.Add(new TextBlock { Text = "Inspector", FontSize = 13, FontWeight = FontWeight.SemiBold });
        _context.MaxWidth = 140;
        _context.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_context, 1);
        heading.Children.Add(_context);

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, Auto, Auto, Auto, Auto, *"), Margin = new Thickness(6, 0, 8, 2) };
        _copy = ToolButton(Symbol.Copy, "Copy value", () => _ = CopyAsync(Selected?.Node.FullValue));
        _pin = ToolButton(Symbol.Pin, "Pin variable", TogglePin);
        var tools = new[]
        {
            ToolButton(Symbol.Refresh, "Refresh values", Refresh),
            ToolButton(Symbol.ChevronUp, "Collapse all", CollapseAll),
            _copy,
            _pin,
            ToolButton(Symbol.Delete, "Clear inspector", Clear)
        };
        for (var index = 0; index < tools.Length; index++)
        {
            Grid.SetColumn(tools[index], index);
            toolbar.Children.Add(tools[index]);
        }
        Grid.SetColumn(_pinnedOnly, 5);
        _pinnedOnly.HorizontalAlignment = HorizontalAlignment.Right;
        toolbar.Children.Add(_pinnedOnly);
        _pinnedOnly.IsCheckedChanged += (_, _) => RenderTree();
        _search.TextChanged += (_, _) => RenderTree();
        ToolTip.SetTip(_search, "Filter names, values, types, and loaded members");

        var treeArea = new Grid();
        treeArea.Children.Add(_tree);
        treeArea.Children.Add(_empty);

        var details = new Grid { RowDefinitions = new RowDefinitions("Auto, Auto, *, Auto"), Margin = new Thickness(10, 8, 10, 4) };
        details.Children.Add(_detailName);
        Grid.SetRow(_detailType, 1);
        details.Children.Add(_detailType);
        Grid.SetRow(_detailValue, 2);
        details.Children.Add(_detailValue);
        Grid.SetRow(_evaluate, 3);
        _evaluate.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(_evaluate, "Runs this property getter; it may execute user code");
        _evaluate.Click += async (_, _) =>
        {
            if (_tree.SelectedItem is TreeViewItem item) await EvaluateAsync(item);
        };
        details.Children.Add(_evaluate);

        var splitter = new GridSplitter
        {
            Height = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = new SolidColorBrush(Color.FromRgb(55, 55, 62))
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto, Auto, Auto, *, 4, 160, Auto") };
        var sections = new Control[] { heading, toolbar, _search, treeArea, splitter, details, _status };
        for (var index = 0; index < sections.Length; index++)
        {
            Grid.SetRow(sections[index], index);
            root.Children.Add(sections[index]);
        }
        var detailHeight = new GridLength(160);
        root.SizeChanged += (_, _) =>
        {
            var visible = root.Bounds.Height >= 350;
            if (details.IsVisible == visible) return;
            if (!visible) detailHeight = new GridLength(root.RowDefinitions[5].ActualHeight);
            details.IsVisible = splitter.IsVisible = visible;
            root.RowDefinitions[4].Height = new GridLength(visible ? 4 : 0);
            root.RowDefinitions[5].Height = visible ? detailHeight : new GridLength(0);
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        DetachedFromVisualTree += (_, _) => _renderCts.Cancel();
        AttachedToVisualTree += (_, _) => { if (_renderCts.IsCancellationRequested) RenderTree(); };
        Content = root;
        UpdateDetails();
        UpdateStatus();
    }

    private static Button ToolButton(Symbol symbol, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = new SymbolIcon { Symbol = symbol, FontSize = 14 },
            Width = 28,
            Height = 28,
            Padding = new Thickness(4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(button, tooltip);
        Avalonia.Automation.AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    private NodeView? Selected => (_tree.SelectedItem as TreeViewItem)?.Tag as NodeView;

    public void Inspect(string name, object? obj) => OnUI(() =>
    {
        _liveValues[name] = (obj, obj?.GetType());
        _snapshotFactory = () => _liveValues.Select(pair => new ObjectNode(pair.Key, pair.Value.Value, pair.Value.Type));
        ApplySnapshot(_snapshotFactory(), "Objects");
    });

    public void UpdateVariables(IEnumerable<ScriptVariable> variables)
    {
        var snapshot = variables.ToArray();
        OnUI(() =>
        {
            _liveValues.Clear();
            _snapshotFactory = () => snapshot.Select(variable => new ObjectNode(variable.Name, variable.Value, variable.Type));
            ApplySnapshot(_snapshotFactory(), "Notebook");
        });
    }

    public void SetNodes(IEnumerable<ObjectNode> nodes, string context = "Locals")
    {
        var snapshot = nodes.ToArray();
        OnUI(() =>
        {
            _liveValues.Clear();
            _snapshotFactory = null;
            ApplySnapshot(snapshot, context);
        });
    }

    private static void OnUI(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private void ApplySnapshot(IEnumerable<ObjectNode> nodes, string context)
    {
        var snapshot = nodes.ToArray();
        _previousValues = _currentValues;
        _currentValues = new Dictionary<string, string>(StringComparer.Ordinal);
        _rootNodes.Clear();
        foreach (var node in snapshot) _rootNodes.Add(node);
        _pinnedNames.IntersectWith(snapshot.Select(node => node.Name));
        _context.Text = context;
        RenderTree();
    }

    public void Refresh() => OnUI(() =>
    {
        try
        {
            if (_snapshotFactory != null) ApplySnapshot(_snapshotFactory(), _context.Text ?? "Objects");
            else RefreshRequested?.Invoke();
        }
        catch (Exception exception) { _status.Text = $"Refresh failed: {exception.Message}"; }
    });

    public void Clear() => OnUI(() =>
    {
        _snapshotFactory = null;
        _liveValues.Clear();
        _rootNodes.Clear();
        _previousValues.Clear();
        _currentValues.Clear();
        _pinnedNames.Clear();
        _expandedPaths.Clear();
        _selectedPath = null;
        _context.Text = "No session";
        _search.Text = "";
        _pinnedOnly.IsChecked = false;
        RenderTree();
    });

    private bool Matches(ObjectNode node)
    {
        var query = _search.Text?.Trim() ?? "";
        return query.Length == 0 || node.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            node.Type.Contains(query, StringComparison.OrdinalIgnoreCase) || node.Value.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            node.ChildrenLoaded && node.Children!.Any(Matches);
    }

    private void RenderTree()
    {
        _renderCts.Cancel();
        _renderCts.Dispose();
        _renderCts = new CancellationTokenSource();
        _loading.Clear();
        _rendering = true;
        try
        {
            _items.Clear();
            _visibleItems.Clear();
            foreach (var node in _rootNodes.OrderByDescending(node => _pinnedNames.Contains(node.Name)).ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase))
            {
                if ((_pinnedOnly.IsChecked != true || _pinnedNames.Contains(node.Name)) && Matches(node))
                    _items.Add(BuildItem(node, node.Name, node.Name, node.Name));
            }
            _tree.SelectedItem = _selectedPath != null && _visibleItems.TryGetValue(_selectedPath, out var selected) ? selected : null;
        }
        finally { _rendering = false; }
        _empty.Text = _rootNodes.Count == 0 ? "No variables" : "No matching variables";
        _empty.IsVisible = _items.Count == 0;
        UpdateDetails();
        UpdateStatus();
    }

    private TreeViewItem BuildItem(ObjectNode node, string path, string expression, string rootName)
    {
        var view = new NodeView(node, path, expression, rootName);
        var item = new TreeViewItem
        {
            Tag = view,
            Padding = new Thickness(2, 2),
            FontSize = 12
        };
        _visibleItems[path] = item;
        SetHeader(item, view);
        if (node.CanExpand)
        {
            if (node.ChildrenLoaded) FillChildren(item, view);
            else item.ItemsSource = new[] { new TextBlock { Text = "Loading...", Foreground = Brushes.Gray } };
        }
        item.PropertyChanged += (_, args) =>
        {
            if (args.Property != TreeViewItem.IsExpandedProperty) return;
            if (item.IsExpanded)
            {
                if (string.IsNullOrWhiteSpace(_search.Text)) _expandedPaths.Add(path);
                _ = ExpandAsync(item, view, _renderCts.Token);
            }
            else _expandedPaths.Remove(path);
        };
        item.ContextMenu = CreateContextMenu(item, view);
        if (_expandedPaths.Contains(path) || !string.IsNullOrWhiteSpace(_search.Text) && node.ChildrenLoaded && node.Children!.Any(Matches))
            item.IsExpanded = true;
        return item;
    }

    private void SetHeader(TreeViewItem item, NodeView view)
    {
        var node = view.Node;
        var fingerprint = node.Type + "\0" + node.Value;
        if (!node.IsDeferred) _currentValues[view.Path] = fingerprint;
        var changed = !node.IsDeferred && _previousValues.TryGetValue(view.Path, out var previous) && previous != fingerprint;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, *"), RowDefinitions = new RowDefinitions("Auto, Auto"), MinHeight = 32 };
        SetRowWidth(grid, view);
        var name = new TextBlock
        {
            Text = node.Name,
            Foreground = Brushes.LightSkyBlue,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = changed ? FontWeight.SemiBold : FontWeight.Normal,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var value = new TextBlock
        {
            Text = node.Value,
            Foreground = node.IsError ? Brushes.LightCoral : changed ? Brushes.Gold : node.IsDeferred ? Brushes.Gray : Brushes.LightGreen,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontFamily = new FontFamily("Cascadia Code,Consolas,Monospace"),
            FontSize = 11
        };
        ToolTip.SetTip(name, view.Expression);
        ToolTip.SetTip(value, node.Value);
        var nameArea = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto, *") };
        var pin = new SymbolIcon
        {
            Symbol = Symbol.Pin,
            FontSize = 10,
            Width = 14,
            Foreground = Brushes.Gold,
            IsVisible = _pinnedNames.Contains(node.Name) && view.Path == node.Name
        };
        nameArea.Children.Add(pin);
        Grid.SetColumn(name, 1);
        nameArea.Children.Add(name);
        Grid.SetColumn(value, 1);
        grid.Children.Add(nameArea);
        grid.Children.Add(value);
        var type = new TextBlock { Text = node.Type, FontSize = 10, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetRow(type, 1);
        Grid.SetColumnSpan(type, 2);
        grid.Children.Add(type);
        item.Header = grid;
    }

    private void SetRowWidth(Grid header, NodeView view) =>
        header.Width = Math.Max(0, _tree.Bounds.Width - 48 - view.Path.Count(character => character == '/') * 16);

    private void FillChildren(TreeViewItem item, NodeView view)
    {
        item.ItemsSource = view.Node.Children!.Where(Matches).Select(child =>
        {
            var expression = child.IsGroup ? view.Expression : child.Name.StartsWith('[') ? view.Expression + child.Name : view.Expression + "." + child.Name;
            return BuildItem(child, view.Path + "/" + child.Name, expression, view.RootName);
        }).ToArray();
    }

    private async Task ExpandAsync(TreeViewItem item, NodeView view, CancellationToken token)
    {
        if (view.Node.ChildrenLoaded || !_loading.Add(view.Path)) return;
        try
        {
            await view.Node.LoadChildrenAsync(token);
            token.ThrowIfCancellationRequested();
            if (!_visibleItems.TryGetValue(view.Path, out var current) || !ReferenceEquals(current, item)) return;
            FillChildren(item, view);
            if (_selectedPath != null && _visibleItems.TryGetValue(_selectedPath, out var selected)) _tree.SelectedItem = selected;
            UpdateStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested) return;
            item.ItemsSource = new[] { new TextBlock { Text = exception.Message, Foreground = Brushes.LightCoral, TextWrapping = TextWrapping.Wrap } };
            _status.Text = "Value unavailable";
        }
        finally { if (!token.IsCancellationRequested) _loading.Remove(view.Path); }
    }

    private async Task EvaluateAsync(TreeViewItem item)
    {
        if (item.Tag is not NodeView view || !view.Node.IsDeferred) return;
        var token = _renderCts.Token;
        _evaluate.IsEnabled = false;
        _status.Text = "Evaluating getter...";
        try
        {
            var evaluated = await view.Node.EvaluateAsync(token);
            token.ThrowIfCancellationRequested();
            foreach (var parent in _visibleItems.Values.Select(candidate => candidate.Tag).OfType<NodeView>())
            {
                if (parent.Node.Children?.IndexOf(view.Node) is >= 0 and var index) parent.Node.Children[index] = evaluated;
            }
            var replacement = BuildItem(evaluated, view.Path, view.Expression, view.RootName);
            var parentItem = item.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault();
            if (parentItem?.ItemsSource is TreeViewItem[] siblings)
                parentItem.ItemsSource = siblings.Select(sibling => ReferenceEquals(sibling, item) ? replacement : sibling).ToArray();
            _tree.SelectedItem = replacement;
            UpdateDetails();
            UpdateStatus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _evaluate.IsEnabled = true; }
    }

    private ContextMenu CreateContextMenu(TreeViewItem item, NodeView view)
    {
        var menu = new ContextMenu();
        MenuItem Command(string header, Action action, bool enabled = true)
        {
            var command = new MenuItem { Header = header, IsEnabled = enabled };
            command.Click += (_, _) => action();
            return command;
        }
        menu.Items.Add(Command("Copy value", () => _ = CopyAsync(view.Node.FullValue)));
        menu.Items.Add(Command("Copy expression", () => _ = CopyAsync(view.Expression)));
        menu.Items.Add(Command("Evaluate getter", () => _ = EvaluateAsync(item), view.Node.IsDeferred));
        menu.Items.Add(new Separator());
        menu.Items.Add(Command(_pinnedNames.Contains(view.RootName) ? "Unpin variable" : "Pin variable", () => TogglePin(view.RootName)));
        menu.Items.Add(Command("Expand", () => item.IsExpanded = true, view.Node.CanExpand));
        menu.Items.Add(Command("Collapse", () => item.IsExpanded = false, view.Node.CanExpand));
        return menu;
    }

    private void TogglePin()
    {
        if (Selected is { } view) TogglePin(view.RootName);
    }

    private void TogglePin(string name)
    {
        if (!_pinnedNames.Add(name)) _pinnedNames.Remove(name);
        RenderTree();
    }

    private void CollapseAll()
    {
        foreach (var item in _visibleItems.Values.ToArray()) item.IsExpanded = false;
        _expandedPaths.Clear();
    }

    private void UpdateDetails()
    {
        var view = Selected;
        _copy.IsEnabled = _pin.IsEnabled = view != null;
        _pin.Foreground = view != null && _pinnedNames.Contains(view.RootName) ? Brushes.Gold : Brushes.LightGray;
        _detailName.Text = view?.Expression ?? "No selection";
        ToolTip.SetTip(_detailName, view?.Expression);
        _detailType.Text = view == null ? "" : $"{view.Node.Kind} | {view.Node.Type}";
        var text = view?.Node.FullValue ?? "";
        _detailValue.Text = text.Length <= 32000 ? text : text[..32000] + "\n...";
        _detailValue.Foreground = view?.Node.IsError == true ? Brushes.LightCoral : Brushes.White;
        _evaluate.IsVisible = view?.Node.IsDeferred == true;
    }

    private void UpdateStatus()
    {
        var changed = _currentValues.Count(pair => _previousValues.TryGetValue(pair.Key, out var previous) && previous != pair.Value);
        _status.Text = $"{_rootNodes.Count} variables | {_pinnedNames.Count} pinned" + (changed > 0 ? $" | {changed} changed" : "");
    }

    private async Task CopyAsync(string? value)
    {
        if (value == null) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(value);
            _status.Text = "Copied";
        }
        catch (Exception exception) { _status.Text = $"Copy failed: {exception.Message}"; }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            _search.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control && _tree.IsKeyboardFocusWithin)
        {
            _ = CopyAsync(Selected?.Node.FullValue);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _search.IsKeyboardFocusWithin)
        {
            _search.Text = "";
            _tree.Focus();
            e.Handled = true;
        }
    }
}
