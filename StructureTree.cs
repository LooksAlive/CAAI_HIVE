using System;
using System.Collections;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using FluentAvalonia.UI.Controls;
using Avalonia.Styling;

namespace CAAI;

public enum StructureKind
{
    Namespace,
    Class,
    Struct,
    Record,
    Extension,
    Interface,
    Enum,
    EnumMember,
    Method,
    Property,
    Field,
    Constructor,
    Event,
    Delegate,
    Using
}

public class StructureNode : INotifyPropertyChanged
{
    private bool _isExpanded = true;
    private bool _isSelected;
    
    public string Name { get; }
    public StructureKind Kind { get; }
    public TextSpan Span { get; }
    public List<StructureNode> Children { get; init; } = new();
    
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }
    
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }
    
    public string? FilePath { get; set; }
    public int LineNumber { get; set; }
    public string? Modifiers { get; set; }
    public string? ReturnType { get; set; }
    public string? Parameters { get; set; }

    public StructureNode(string name, StructureKind kind, TextSpan span)
    {
        Name = name;
        Kind = kind;
        Span = span;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class StructureBuilder
{
    public static List<StructureNode> Build(SyntaxTree tree, string? filePath = null)
    {
        var root = tree.GetCompilationUnitRoot();
        var result = new List<StructureNode>();

        // Parse using directives
        foreach (var usingDirective in root.Usings)
        {
            result.Add(new StructureNode(
                usingDirective.Name?.ToString() ?? "using",
                StructureKind.Using,
                usingDirective.Span)
            {
                FilePath = filePath
            });
        }

        foreach (var member in root.Members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    result.Add(ParseNamespace(ns, filePath));
                    break;
                case TypeDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax:
                    result.Add(ParseType(member, filePath));
                    break;
            }
        }

        return result;
    }

    static StructureNode ParseNamespace(BaseNamespaceDeclarationSyntax ns, string? filePath = null)
    {
        var node = new StructureNode(
            ns.Name.ToString(),
            StructureKind.Namespace,
            ns.Span)
        {
            FilePath = filePath,
            LineNumber = ns.GetLocation().GetLineSpan().StartLinePosition.Line
        };

        foreach (var member in ns.Members)
        {
            if (member is TypeDeclarationSyntax or EnumDeclarationSyntax or DelegateDeclarationSyntax)
                node.Children.Add(ParseType(member, filePath));
        }

        return node;
    }

    static StructureNode ParseType(MemberDeclarationSyntax member, string? filePath = null)
    {
        return member switch
        {
            ClassDeclarationSyntax c => ParseTypeCore(c.Identifier.Text, StructureKind.Class, c.Span, c.Members, c.Modifiers.ToString(), filePath),
            StructDeclarationSyntax s => ParseTypeCore(s.Identifier.Text, StructureKind.Struct, s.Span, s.Members, s.Modifiers.ToString(), filePath),
            InterfaceDeclarationSyntax i => ParseTypeCore(i.Identifier.Text, StructureKind.Interface, i.Span, i.Members, i.Modifiers.ToString(), filePath),
            EnumDeclarationSyntax e => ParseEnum(e, filePath),
            RecordDeclarationSyntax r => ParseTypeCore(r.Identifier.Text, StructureKind.Record, r.Span, r.Members, r.Modifiers.ToString(), filePath),
            DelegateDeclarationSyntax d => ParseDelegate(d, filePath),
            _ => throw new NotSupportedException(member.Kind().ToString())
        };
    }

    static StructureNode ParseTypeCore(string name, StructureKind kind, TextSpan span, 
        SyntaxList<MemberDeclarationSyntax> members, string? modifiers = null, string? filePath = null)
    {
        var node = new StructureNode(name, kind, span)
        {
            Modifiers = modifiers,
            FilePath = filePath,
            LineNumber = span.Start
        };

        foreach (var m in members)
            ParseTypeMember(m, node);

        return node;
    }

    static StructureNode ParseEnum(EnumDeclarationSyntax e, string? filePath = null)
    {
        var node = new StructureNode(
            e.Identifier.Text,
            StructureKind.Enum,
            e.Span)
        {
            Modifiers = e.Modifiers.ToString(),
            FilePath = filePath
        };

        foreach (var member in e.Members)
        {
            node.Children.Add(new StructureNode(
                member.Identifier.Text,
                StructureKind.EnumMember,
                member.Span)
            {
                FilePath = filePath
            });
        }

        return node;
    }

    static StructureNode ParseDelegate(DelegateDeclarationSyntax d, string? filePath = null)
    {
        return new StructureNode(
            d.Identifier.Text,
            StructureKind.Delegate,
            d.Span)
        {
            Modifiers = d.Modifiers.ToString(),
            ReturnType = d.ReturnType.ToString(),
            FilePath = filePath
        };
    }

    static void ParseTypeMember(MemberDeclarationSyntax member, StructureNode parent)
    {
        switch (member)
        {
            case MethodDeclarationSyntax md:
                var methodNode = new StructureNode(
                    md.Identifier.Text,
                    StructureKind.Method,
                    md.Span)
                {
                    ReturnType = md.ReturnType.ToString(),
                    Parameters = string.Join(", ", md.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}")),
                    Modifiers = md.Modifiers.ToString()
                };
                parent.Children.Add(methodNode);
                break;

            case PropertyDeclarationSyntax pd:
                parent.Children.Add(new StructureNode(
                    pd.Identifier.Text,
                    StructureKind.Property,
                    pd.Span)
                {
                    ReturnType = pd.Type.ToString(),
                    Modifiers = pd.Modifiers.ToString()
                });
                break;

            case FieldDeclarationSyntax fd:
                foreach (var v in fd.Declaration.Variables)
                {
                    parent.Children.Add(new StructureNode(
                        v.Identifier.Text,
                        StructureKind.Field,
                        v.Span)
                    {
                        ReturnType = fd.Declaration.Type.ToString(),
                        Modifiers = fd.Modifiers.ToString()
                    });
                }
                break;

            case ConstructorDeclarationSyntax cd:
                parent.Children.Add(new StructureNode(
                    cd.Identifier.Text,
                    StructureKind.Constructor,
                    cd.Span)
                {
                    Parameters = string.Join(", ", cd.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}")),
                    Modifiers = cd.Modifiers.ToString()
                });
                break;

            case EventDeclarationSyntax ed:
                parent.Children.Add(new StructureNode(
                    ed.Identifier.Text,
                    StructureKind.Event,
                    ed.Span)
                {
                    ReturnType = ed.Type.ToString(),
                    Modifiers = ed.Modifiers.ToString()
                });
                break;

            case TypeDeclarationSyntax or EnumDeclarationSyntax:
                parent.Children.Add(ParseType(member));
                break;
        }
    }
}

public class CodeStructureWidget : UserControl
{
    public TreeView TreeView = null!;
    private TextBox _searchBox = null!;
    private ComboBox _filterCombo = null!;
    private TextBlock _statusText = null!;
    private StackPanel _toolbar = null!;
    private ObservableCollection<StructureNode> _allNodes = new();
    private List<StructureNode> _flatNodes = new();
    
    public event EventHandler<StructureNode>? NodeSelected;
    public event EventHandler<StructureNode>? NodeDoubleClicked;

    public CodeStructureWidget()
    {
        BuildUI();
    }

    private void BuildUI()
    {
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 35))
        };

        // Toolbar
        _toolbar = CreateToolbar();
        mainGrid.Children.Add(_toolbar);
        Grid.SetRow(_toolbar, 0);

        // Search bar
        var searchPanel = CreateSearchPanel();
        mainGrid.Children.Add(searchPanel);
        Grid.SetRow(searchPanel, 1);

        // Tree view
        TreeView = new TreeView
        {
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            ItemTemplate = CreateTreeTemplate()
        };
        
        TreeView.SelectionChanged += OnTreeSelectionChanged;
        TreeView.DoubleTapped += OnTreeDoubleTapped;

        var scrollViewer = new ScrollViewer
        {
            Content = TreeView,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        mainGrid.Children.Add(scrollViewer);
        Grid.SetRow(scrollViewer, 2);

        // Status bar
        var statusBar = CreateStatusBar();
        mainGrid.Children.Add(statusBar);
        Grid.SetRow(statusBar, 3);

        Content = mainGrid;
    }

    private StackPanel CreateToolbar()
    {
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Margin = new Thickness(5),
            Background = Brushes.Transparent
        };

        // Expand All Button
        var expandAllBtn = new Button
        {
            Content = "📂",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 6),
            FontSize = 14,
            Cursor = new Cursor(StandardCursorType.Hand),
            Width = 36,
            Height = 32
        };
        ToolTip.SetTip(expandAllBtn, "Expand All (Ctrl+Shift+E)");
        expandAllBtn.Click += (_, _) => ExpandAll();

        // Collapse All Button
        var collapseAllBtn = new Button
        {
            Content = "📁",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 6),
            FontSize = 14,
            Cursor = new Cursor(StandardCursorType.Hand),
            Width = 36,
            Height = 32
        };
        ToolTip.SetTip(collapseAllBtn, "Collapse All (Ctrl+Shift+C)");
        collapseAllBtn.Click += (_, _) => CollapseAll();

        // Refresh Button
        var refreshBtn = new Button
        {
            Content = "🔄",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 6),
            FontSize = 14,
            Cursor = new Cursor(StandardCursorType.Hand),
            Width = 36,
            Height = 32
        };
        ToolTip.SetTip(refreshBtn, "Refresh Structure (Ctrl+R)");
        refreshBtn.Click += (_, _) => RefreshStructure();

        // Separator
        var separator = new Border
        {
            Width = 1,
            Background = new SolidColorBrush(Color.FromRgb(80, 80, 85)),
            Margin = new Thickness(5, 5),
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Search Button
        var searchBtn = new Button
        {
            Content = "🔍",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 6),
            FontSize = 14,
            Cursor = new Cursor(StandardCursorType.Hand),
            Width = 36,
            Height = 32
        };
        ToolTip.SetTip(searchBtn, "Focus Search Box (Ctrl+F)");
        searchBtn.Click += (_, _) => _searchBox?.Focus();

        toolbar.Children.Add(expandAllBtn);
        toolbar.Children.Add(collapseAllBtn);
        toolbar.Children.Add(refreshBtn);
        toolbar.Children.Add(separator);
        toolbar.Children.Add(searchBtn);

        return toolbar;
    }

    private Border CreateSearchPanel()
    {
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
            Padding = new Thickness(5),
            Margin = new Thickness(5, 0, 5, 5),
            CornerRadius = new CornerRadius(3)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto")
        };

        _searchBox = new TextBox
        {
            Watermark = "Search members...",
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 5, 0)
        };
        _searchBox.TextChanged += OnSearchTextChanged;

        _filterCombo = new ComboBox
        {
            Width = 100,
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
            Foreground = Brushes.White
        };
        _filterCombo.ItemsSource = new[] { "All", "Types", "Methods", "Properties", "Fields", "Events" };
        _filterCombo.SelectedIndex = 0;
        _filterCombo.SelectionChanged += OnFilterChanged;

        var clearBtn = new Button
        {
            Content = "✖",
            Background = Brushes.Transparent,
            Foreground = Brushes.Gray,
            BorderThickness = new Thickness(0),
            Width = 30,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        clearBtn.Click += (_, _) => ClearSearch();

        grid.Children.Add(_searchBox);
        Grid.SetColumn(_searchBox, 0);
        grid.Children.Add(_filterCombo);
        Grid.SetColumn(_filterCombo, 1);
        grid.Children.Add(clearBtn);
        Grid.SetColumn(clearBtn, 2);

        panel.Child = grid;
        return panel;
    }

    private Border CreateStatusBar()
    {
        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
            Padding = new Thickness(10, 5)
        };

        _statusText = new TextBlock
        {
            Text = "Ready",
            FontSize = 11,
            Foreground = Brushes.Gray
        };

        statusBar.Child = _statusText;
        return statusBar;
    }

    private IDataTemplate CreateTreeTemplate()
    {
        return new FuncTreeDataTemplate<StructureNode>(
            match: _ => true,
            build: (node, _) =>
            {
                var border = new Border
                {
                    Padding = new Thickness(4, 2),
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(3),
                    Background = node.IsSelected ? new SolidColorBrush(Color.FromArgb(80, 100, 100, 255)) : Brushes.Transparent
                };

                var stack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6
                };

                // Icon
                var icon = new TextBlock
                {
                    Text = GetIcon(node.Kind),
                    FontSize = 12,
                    Foreground = GetIconColor(node.Kind)
                };

                // Name
                var name = new TextBlock
                {
                    Text = node.Name,
                    FontSize = 12,
                    Foreground = GetNodeColor(node.Kind),
                    FontWeight = IsTypeNode(node.Kind) ? FontWeight.Bold : FontWeight.Normal
                };

                // Additional info (return type, parameters)
                if (!string.IsNullOrEmpty(node.ReturnType) && node.Kind == StructureKind.Method)
                {
                    var returnType = new TextBlock
                    {
                        Text = $": {node.ReturnType}",
                        FontSize = 11,
                        Foreground = Brushes.Gray,
                        FontStyle = FontStyle.Italic
                    };
                    stack.Children.Add(returnType);
                }

                if (!string.IsNullOrEmpty(node.Parameters) && node.Kind == StructureKind.Method)
                {
                    var paramsText = new TextBlock
                    {
                        Text = $"({node.Parameters})",
                        FontSize = 11,
                        Foreground = Brushes.Gray
                    };
                    
                    // Truncate long parameter lists
                    if (paramsText.Text.Length > 50)
                        paramsText.Text = paramsText.Text[..47] + "...";
                    
                    stack.Children.Add(paramsText);
                }

                // Modifiers badge
                if (!string.IsNullOrEmpty(node.Modifiers))
                {
                    var modifiers = node.Modifiers.Split(' ').FirstOrDefault();
                    if (modifiers == "public" || modifiers == "private" || modifiers == "protected" || modifiers == "internal")
                    {
                        var badge = new Border
                        {
                            Background = GetModifierColor(modifiers),
                            CornerRadius = new CornerRadius(10),
                            Padding = new Thickness(6, 2),
                            Margin = new Thickness(5, 0, 0, 0)
                        };
                        badge.Child = new TextBlock
                        {
                            Text = modifiers,
                            FontSize = 9,
                            Foreground = Brushes.White
                        };
                        stack.Children.Add(badge);
                    }
                }

                // Member count for types
                if (IsTypeNode(node.Kind) && node.Children.Count > 0)
                {
                    var count = new TextBlock
                    {
                        Text = $"({node.Children.Count})",
                        FontSize = 10,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(5, 0, 0, 0)
                    };
                    stack.Children.Add(count);
                }

                stack.Children.Insert(0, icon);
                stack.Children.Insert(1, name);

                border.Child = stack;

                // Hover effect
                border.PointerEntered += (_, _) =>
                {
                    if (!node.IsSelected)
                        border.Background = new SolidColorBrush(Color.FromArgb(50, 100, 100, 100));
                };
                border.PointerExited += (_, _) =>
                {
                    if (!node.IsSelected)
                        border.Background = Brushes.Transparent;
                };

                return border;
            },
            itemsSelector: node => node.Children
        );
    }

    private bool IsTypeNode(StructureKind kind)
    {
        return kind == StructureKind.Class || kind == StructureKind.Struct || 
               kind == StructureKind.Interface || kind == StructureKind.Enum || 
               kind == StructureKind.Record;
    }

    private IBrush GetNodeColor(StructureKind kind)
    {
        return kind switch
        {
            StructureKind.Class => new SolidColorBrush(Color.FromRgb(78, 201, 176)),
            StructureKind.Struct => new SolidColorBrush(Color.FromRgb(150, 200, 150)),
            StructureKind.Interface => new SolidColorBrush(Color.FromRgb(185, 140, 214)),
            StructureKind.Enum => new SolidColorBrush(Color.FromRgb(214, 185, 140)),
            StructureKind.Method => new SolidColorBrush(Color.FromRgb(220, 140, 86)),
            StructureKind.Property => new SolidColorBrush(Color.FromRgb(156, 220, 254)),
            StructureKind.Field => new SolidColorBrush(Color.FromRgb(214, 157, 133)),
            StructureKind.Event => new SolidColorBrush(Color.FromRgb(200, 150, 200)),
            _ => Brushes.White
        };
    }

    private IBrush GetIconColor(StructureKind kind)
    {
        return kind switch
        {
            StructureKind.Class => new SolidColorBrush(Color.FromRgb(78, 201, 176)),
            StructureKind.Method => new SolidColorBrush(Color.FromRgb(220, 140, 86)),
            StructureKind.Property => new SolidColorBrush(Color.FromRgb(156, 220, 254)),
            _ => Brushes.Gray
        };
    }

    private IBrush GetModifierColor(string modifier)
    {
        return modifier switch
        {
            "public" => new SolidColorBrush(Color.FromRgb(0, 150, 0)),
            "private" => new SolidColorBrush(Color.FromRgb(150, 0, 0)),
            "protected" => new SolidColorBrush(Color.FromRgb(0, 0, 150)),
            "internal" => new SolidColorBrush(Color.FromRgb(150, 150, 0)),
            _ => new SolidColorBrush(Color.FromRgb(100, 100, 100))
        };
    }

    static string GetIcon(StructureKind kind) => kind switch
    {
        StructureKind.Namespace => "📦",
        StructureKind.Class => "📘",
        StructureKind.Struct => "📗",
        StructureKind.Record => "📇",
        StructureKind.Extension => "🔌",
        StructureKind.Interface => "📙",
        StructureKind.Enum => "🔢",
        StructureKind.EnumMember => "▪️",
        StructureKind.Method => "⚡",
        StructureKind.Property => "🔧",
        StructureKind.Field => "📝",
        StructureKind.Constructor => "🏗️",
        StructureKind.Event => "🔔",
        StructureKind.Delegate => "📎",
        StructureKind.Using => "📥",
        _ => "📄"
    };

    public void SetStructure(SyntaxTree tree, string? filePath = null)
    {
        var nodes = StructureBuilder.Build(tree, filePath);
        _allNodes.Clear();
        _flatNodes.Clear();
        
        foreach (var node in nodes)
        {
            _allNodes.Add(node);
            FlattenNodes(node);
        }
        
        TreeView.ItemsSource = nodes;
        UpdateStatus($"Loaded {_flatNodes.Count} members");
    }

    private void FlattenNodes(StructureNode node)
    {
        _flatNodes.Add(node);
        foreach (var child in node.Children)
        {
            FlattenNodes(child);
        }
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var searchTerm = _searchBox.Text?.ToLower() ?? "";
        var filter = _filterCombo.SelectedItem?.ToString() ?? "All";

        if (string.IsNullOrEmpty(searchTerm) && filter == "All")
        {
            TreeView.ItemsSource = _allNodes;
            UpdateStatus($"Showing {_flatNodes.Count} members");
            return;
        }

        var filtered = FilterNodes(_allNodes, searchTerm, filter);
        TreeView.ItemsSource = filtered;
        
        var filteredCount = CountFilteredNodes(filtered);
        UpdateStatus($"Found {filteredCount} members matching criteria");
    }

    private List<StructureNode> FilterNodes(IEnumerable<StructureNode> nodes, string searchTerm, string filter)
    {
        var result = new List<StructureNode>();
        
        foreach (var node in nodes)
        {
            var matchesSearch = string.IsNullOrEmpty(searchTerm) || 
                                node.Name.ToLower().Contains(searchTerm);
            
            var matchesFilter = filter == "All" ||
                               (filter == "Types" && IsTypeNode(node.Kind)) ||
                               (filter == "Methods" && node.Kind == StructureKind.Method) ||
                               (filter == "Properties" && node.Kind == StructureKind.Property) ||
                               (filter == "Fields" && node.Kind == StructureKind.Field) ||
                               (filter == "Events" && node.Kind == StructureKind.Event);
            
            var filteredChildren = FilterNodes(node.Children, searchTerm, filter);
            
            if ((matchesSearch && matchesFilter) || filteredChildren.Any())
            {
                var newNode = new StructureNode(node.Name, node.Kind, node.Span)
                {
                    Children = filteredChildren,
                    ReturnType = node.ReturnType,
                    Parameters = node.Parameters,
                    Modifiers = node.Modifiers
                };
                result.Add(newNode);
            }
        }
        
        return result;
    }

    private int CountFilteredNodes(IEnumerable<StructureNode> nodes)
    {
        int count = 0;
        foreach (var node in nodes)
        {
            count++;
            count += CountFilteredNodes(node.Children);
        }
        return count;
    }

    private void ClearSearch()
    {
        _searchBox.Text = "";
        _filterCombo.SelectedIndex = 0;
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TreeView.SelectedItem is StructureNode node)
        {
            NodeSelected?.Invoke(this, node);
        }
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (TreeView.SelectedItem is StructureNode node)
        {
            NodeDoubleClicked?.Invoke(this, node);
        }
    }

    public void ExpandAll()
    {
        SetExpandedRecursive(_allNodes, true);

        TreeView.ItemsSource = null;
        TreeView.ItemsSource = _allNodes;
    }

    private void SetExpandedRecursive(IEnumerable<StructureNode> nodes, bool expanded)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = expanded;

            if (node.Children.Count > 0)
                SetExpandedRecursive(node.Children, expanded);
        }
    }

    private void ExpandAllNodes(IEnumerable? items)
    {
        if (items == null) return;
        
        foreach (var item in items)
        {
            if (item is TreeViewItem treeItem)
            {
                treeItem.IsExpanded = true;
                ExpandAllNodes(treeItem.Items);
            }
            else if (item is StructureNode node)
            {
                node.IsExpanded = true;
            }
        }
    }

    public void CollapseAll()
    {
        SetExpandedRecursive(_allNodes, false);

        TreeView.ItemsSource = null;
        TreeView.ItemsSource = _allNodes;
    }

    private void RefreshStructure()
    {
        // Trigger refresh - this should be handled by the parent
        UpdateStatus("Refreshed");
    }

    private void UpdateStatus(string message)
    {
        _statusText.Text = message;
    }

    public void HighlightNode(StructureNode node)
    {
        // Find and scroll to node (implement based on your tree view)
        node.IsSelected = true;
    }

    public StructureNode? FindNode(string name, StructureKind kind)
    {
        return _flatNodes.FirstOrDefault(n => n.Name == name && n.Kind == kind);
    }
}