using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CAAI;

public class BreadcrumbItem
{
    public string Name { get; set; } = "";
    public SyntaxNode? Node { get; set; }
    public SyntaxKind Kind { get; set; }
    public TextSpan Span { get; set; }
}

public class BreadcrumbBar : UserControl
{
    private readonly ScrollViewer _scrollViewer;
    private readonly StackPanel _container;
    private readonly Border _rootBorder;
    private SyntaxNode? _currentRoot;
    private int _currentOffset;
    
    public event EventHandler<BreadcrumbItem>? BreadcrumbClicked;

    public BreadcrumbBar()
    {
        _container = new StackPanel 
        { 
            Orientation = Orientation.Horizontal, 
            Spacing = 6, 
            Margin = new Thickness(4, 2),
            VerticalAlignment = VerticalAlignment.Center
        };

        _scrollViewer = new ScrollViewer
        {
            Content = _container,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent
        };

        _rootBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Child = _scrollViewer,
            Height = 8
        };

        Content = _rootBorder;
    }

    public void Update(SyntaxNode? root, int offset)
    {
        _currentRoot = root;
        _currentOffset = offset;
        _container.Children.Clear();
        
        if (root == null) return;

        try
        {
            // Find the token at cursor position
            var token = root.FindToken(offset);
            var node = token.Parent;
            
            if (node == null) return;

            var breadcrumbs = new List<BreadcrumbItem>();

            // Walk up the tree
            while (node != null)
            {
                var item = CreateBreadcrumbItem(node);
                if (item != null)
                    breadcrumbs.Insert(0, item);
                
                node = node.Parent;
            }

            // Add file root item
            var rootItem = new BreadcrumbItem
            {
                Name = "📁 Project",
                Node = root,
                Kind = SyntaxKind.CompilationUnit,
                Span = root.Span
            };
            
            _container.Children.Add(CreateBreadcrumbButton(rootItem, true));

            // Add separator and remaining breadcrumbs
            for (int i = 0; i < breadcrumbs.Count; i++)
            {
                if (i == 0 && _container.Children.Count > 0)
                    _container.Children.Add(CreateSeparator());

                _container.Children.Add(CreateBreadcrumbButton(breadcrumbs[i], false));
                
                if (i < breadcrumbs.Count - 1)
                    _container.Children.Add(CreateSeparator());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Breadcrumb error: {ex.Message}");
        }
    }

    private BreadcrumbItem? CreateBreadcrumbItem(SyntaxNode node)
    {
        return node switch
        {
            BaseNamespaceDeclarationSyntax ns => new BreadcrumbItem
            {
                Name = ns.Name.ToString(),
                Node = ns,
                Kind = ns.Kind(),
                Span = ns.Span
            },
            ClassDeclarationSyntax c => new BreadcrumbItem
            {
                Name = c.Identifier.Text,
                Node = c,
                Kind = c.Kind(),
                Span = c.Span
            },
            StructDeclarationSyntax s => new BreadcrumbItem
            {
                Name = s.Identifier.Text,
                Node = s,
                Kind = s.Kind(),
                Span = s.Span
            },
            InterfaceDeclarationSyntax i => new BreadcrumbItem
            {
                Name = i.Identifier.Text,
                Node = i,
                Kind = i.Kind(),
                Span = i.Span
            },
            RecordDeclarationSyntax r => new BreadcrumbItem
            {
                Name = r.Identifier.Text,
                Node = r,
                Kind = r.Kind(),
                Span = r.Span
            },
            MethodDeclarationSyntax m => new BreadcrumbItem
            {
                Name = m.Identifier.Text,
                Node = m,
                Kind = m.Kind(),
                Span = m.Span
            },
            PropertyDeclarationSyntax p => new BreadcrumbItem
            {
                Name = p.Identifier.Text,
                Node = p,
                Kind = p.Kind(),
                Span = p.Span
            },
            ConstructorDeclarationSyntax ctor => new BreadcrumbItem
            {
                Name = ctor.Identifier.Text,
                Node = ctor,
                Kind = ctor.Kind(),
                Span = ctor.Span
            },
            _ => null
        };
    }

    private Border CreateBreadcrumbButton(BreadcrumbItem item, bool isRoot)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto, Auto")
        };

        // Icon
        var icon = new TextBlock
        {
            Text = GetNodeIcon(item.Kind),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = isRoot ? new Thickness(0) : new Thickness(0)
        };

        // Text
        var text = new TextBlock
        {
            Text = item.Name,
            FontSize = 10,
            Foreground = isRoot ? new SolidColorBrush(Color.FromRgb(86, 156, 214)) : Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = isRoot ? FontWeight.Bold : FontWeight.Normal
        };

        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { icon, text }
            },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4),
            Cursor = new Cursor(StandardCursorType.Hand),
            //ToolTip = new ToolTip { Content = $"Navigate to {item.Name}" }
        };

        button.Click += (_, _) =>
        {
            BreadcrumbClicked?.Invoke(this, item);
        };

        // Hover effect
        button.PointerEntered += (_, _) =>
        {
            button.Background = new SolidColorBrush(Color.FromArgb(50, 100, 100, 255));
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = Brushes.Transparent;
        };

        var border = new Border
        {
            Child = button,
            CornerRadius = new CornerRadius(3)
        };

        return border;
    }

    private Border CreateSeparator()
    {
        return new Border
        {
            Child = new TextBlock
            {
                Text = "›",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
                VerticalAlignment = VerticalAlignment.Center
            },
            Margin = new Thickness(0)
        };
    }

    private string GetNodeIcon(SyntaxKind kind)
    {
        return kind switch
        {
            SyntaxKind.NamespaceDeclaration => "📦",
            SyntaxKind.FileScopedNamespaceDeclaration => "📦",
            SyntaxKind.ClassDeclaration => "📘",
            SyntaxKind.StructDeclaration => "📗",
            SyntaxKind.InterfaceDeclaration => "📙",
            SyntaxKind.RecordDeclaration => "📇",
            SyntaxKind.MethodDeclaration => "⚡",
            SyntaxKind.PropertyDeclaration => "🔧",
            SyntaxKind.ConstructorDeclaration => "🏗️",
            _ => "📄"
        };
    }

    public void Refresh()
    {
        if (_currentRoot != null)
            Update(_currentRoot, _currentOffset);
    }

    public void Clear()
    {
        _container.Children.Clear();
    }
}
