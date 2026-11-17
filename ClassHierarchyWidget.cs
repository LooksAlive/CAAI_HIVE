using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Media;
using Microsoft.CodeAnalysis;

namespace CAAI;

public sealed class HierarchyNode
{
    public INamedTypeSymbol Symbol { get; }
    public string Name { get; }
    public string BaseTypeName { get; }
    public string Accessibility { get; }
    public bool IsSelected { get; }
    public List<HierarchyNode> DerivedTypes { get; } = new();

    public HierarchyNode(INamedTypeSymbol symbol, INamedTypeSymbol? selectedType)
    {
        Symbol = symbol;
        Name = symbol.Name;
        BaseTypeName = symbol.BaseType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat) ?? "No base class";
        Accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant();
        IsSelected = selectedType != null && SymbolEqualityComparer.Default.Equals(symbol, selectedType);
    }
}

public sealed class ClassHierarchyWidget : UserControl
{
    private readonly ComboBox _typePicker;
    private readonly TextBox _typeSearch;
    private readonly ToggleButton _followCaretButton = null!;
    private readonly TreeView _tree;
    private readonly TextBlock _status;
    private List<INamedTypeSymbol> _allTypes = new();
    private bool _updating;

    public bool IsFollowingCaret => _followCaretButton.IsChecked == true;
    public event Action? FollowCaretRequested;
    public event Action<INamedTypeSymbol>? TypePicked;

    public ClassHierarchyWidget()
    {
        _typeSearch = new TextBox
        {
            Watermark = "Filter classes by name, namespace, or base type...",
            Margin = new Thickness(0, 0, 0, 5),
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
            Foreground = Brushes.White
        };
        _typeSearch.TextChanged += (_, _) => ApplyTypeFilter();

        _typePicker = new ComboBox
        {
            MinWidth = 120,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<INamedTypeSymbol>((symbol, _) => new TextBlock
            {
                Text = symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? string.Empty,
                TextTrimming = TextTrimming.CharacterEllipsis
            })
        };

        _followCaretButton = new ToggleButton
        {
            Content = "Follow caret",
            IsChecked = true,
            Margin = new Thickness(5, 0, 0, 0)
        };

        _typePicker.SelectionChanged += (_, _) =>
        {
            if (_updating || _typePicker.SelectedItem is not INamedTypeSymbol symbol) return;
            _followCaretButton.IsChecked = false;
            TypePicked?.Invoke(symbol);
        };
        
        _followCaretButton.IsCheckedChanged += (_, _) =>
        {
            if (!_updating && IsFollowingCaret) FollowCaretRequested?.Invoke();
        };

        _tree = new TreeView
        {
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            ItemTemplate = new FuncTreeDataTemplate<HierarchyNode>(
                _ => true,
                (node, _) =>
                {
                    var name = new TextBlock
                    {
                        Text = node.Name,
                        Foreground = node.IsSelected ? Brushes.White : Brushes.Gainsboro,
                        FontWeight = node.IsSelected ? FontWeight.Bold : FontWeight.SemiBold,
                        FontSize = 13
                    };
                    var details = new TextBlock
                    {
                        Text = $"{node.Accessibility}  |  base: {node.BaseTypeName}",
                        Foreground = Brushes.Gray,
                        FontSize = 10
                    };
                    var content = new StackPanel { Spacing = 2 };
                    content.Children.Add(name);
                    content.Children.Add(details);

                    var row = new Border
                    {
                        Padding = new Thickness(7, 4),
                        Margin = new Thickness(1, 1, 3, 1),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(70, 190, 165)),
                        BorderThickness = node.IsSelected ? new Thickness(3, 0, 0, 0) : new Thickness(0),
                        Background = node.IsSelected
                            ? new SolidColorBrush(Color.FromRgb(43, 61, 59))
                            : Brushes.Transparent,
                        Child = content
                    };
                    ToolTip.SetTip(row, node.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
                    return row;
                },
                node => node.DerivedTypes)
        };
        _tree.SelectionChanged += (_, _) =>
        {
            if (_tree.SelectedItem is HierarchyNode node)
            {
                _followCaretButton.IsChecked = false;
                TypePicked?.Invoke(node.Symbol);
            }
        };

        _status = new TextBlock
        {
            Text = "Loading class hierarchy...",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(8, 4)
        };

        var pickerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(6)
        };
        pickerRow.Children.Add(_typePicker);
        pickerRow.Children.Add(_followCaretButton);
        Grid.SetColumn(_followCaretButton, 1);

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(_typeSearch);
        header.Children.Add(pickerRow);

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
        };
        layout.Children.Add(header);
        layout.Children.Add(_tree);
        Grid.SetRow(_tree, 1);
        layout.Children.Add(_status);
        Grid.SetRow(_status, 2);
        Content = layout;
    }

    public void SetTypes(IEnumerable<INamedTypeSymbol> types, INamedTypeSymbol? selectedType)
    {
        _allTypes = types
            .OrderBy(type => type.ToDisplayString(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        _updating = true;
        ApplyTypeFilter();
        _typePicker.SelectedItem = selectedType == null || !MatchesTypeFilter(selectedType)
            ? null
            : _allTypes.FirstOrDefault(type => GetTypeKey(type) == GetTypeKey(selectedType));
        _updating = false;
    }

    public void SetFollowCaret(bool follow)
    {
        _updating = true;
        _followCaretButton.IsChecked = follow;
        _updating = false;
    }

    public void ShowHierarchy(IEnumerable<INamedTypeSymbol> sourceTypes, INamedTypeSymbol? selectedType)
    {
        var types = sourceTypes.ToList();
        if (selectedType == null)
        {
            SetPickerSelection(null);
            _tree.ItemsSource = Array.Empty<HierarchyNode>();
            _status.Text = types.Count == 0
                ? "No classes found in the current project"
                : "Place the caret inside a class or choose one above";
            return;
        }

        var selected = types.FirstOrDefault(type => GetTypeKey(type) == GetTypeKey(selectedType));
        if (selected == null)
        {
            SetPickerSelection(null);
            _tree.ItemsSource = Array.Empty<HierarchyNode>();
            _status.Text = $"{selectedType.ToDisplayString()} is not in the current project";
            return;
        }

        SetPickerSelection(selected);
        var typeSet = new HashSet<INamedTypeSymbol>(types, SymbolEqualityComparer.Default);
        var root = selected;
        while (root.BaseType != null && typeSet.Contains(root.BaseType.OriginalDefinition))
            root = root.BaseType.OriginalDefinition;

        var node = BuildNode(root, selected, types, new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default));
        _tree.ItemsSource = new[] { node };
        var derivedCount = types.Count(type => IsDerivedFrom(type, selected));
        _status.Text = $"{selected.ToDisplayString()}  |  {types.Count} project classes  |  {derivedCount} derived class{(derivedCount == 1 ? "" : "es")}";
    }

    private static HierarchyNode BuildNode(
        INamedTypeSymbol symbol,
        INamedTypeSymbol selectedType,
        IReadOnlyCollection<INamedTypeSymbol> types,
        HashSet<INamedTypeSymbol> path)
    {
        var node = new HierarchyNode(symbol, selectedType);
        if (!path.Add(symbol)) return node;

        foreach (var derived in types
                    .Where(type => type.BaseType != null &&
                                   SymbolEqualityComparer.Default.Equals(type.BaseType.OriginalDefinition, symbol.OriginalDefinition))
                     .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase))
        {
            node.DerivedTypes.Add(BuildNode(derived, selectedType, types, new HashSet<INamedTypeSymbol>(path, SymbolEqualityComparer.Default)));
        }

        return node;
    }

    private static bool IsDerivedFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType.OriginalDefinition)) return true;
        }
        return false;
    }

    private void SetPickerSelection(INamedTypeSymbol? selectedType)
    {
        _updating = true;
        _typePicker.SelectedItem = selectedType == null || !MatchesTypeFilter(selectedType)
            ? null
            : _allTypes.FirstOrDefault(type => GetTypeKey(type) == GetTypeKey(selectedType));
        _updating = false;
    }

    private void ApplyTypeFilter()
    {
        var selectedType = _typePicker.SelectedItem as INamedTypeSymbol;
        var filteredTypes = _allTypes.Where(MatchesTypeFilter).ToList();
        var wasUpdating = _updating;

        _updating = true;
        _typePicker.ItemsSource = filteredTypes;
        _typePicker.SelectedItem = selectedType != null && MatchesTypeFilter(selectedType)
            ? filteredTypes.FirstOrDefault(type => GetTypeKey(type) == GetTypeKey(selectedType))
            : null;
        _updating = wasUpdating;
    }

    private bool MatchesTypeFilter(INamedTypeSymbol type)
    {
        var query = _typeSearch.Text?.Trim();
        if (string.IsNullOrEmpty(query)) return true;

        return type.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
                   .Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (type.BaseType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
                   .Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string GetTypeKey(INamedTypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}