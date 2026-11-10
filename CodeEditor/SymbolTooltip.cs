using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.CodeAnalysis;
using System.Linq;
using System.Collections.Generic;

namespace CAAI;

public static class SymbolTooltip
{
    public static ToolTip CreateRichTooltip(ISymbol symbol)
    {
        var tooltip = new ToolTip
        {
            Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(68, 68, 68)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0),
            MaxWidth = 460,
            MaxHeight = 370,
        };

        tooltip.Content = new ScrollViewer
        {
            Content = CreateTooltipContent(symbol),
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        
        return tooltip;
    }

    private static Control CreateTooltipContent(ISymbol symbol)
    {
        var mainBorder = new Border
        {
            Padding = new Thickness(0),
            Child = new StackPanel { Spacing = 0 }
        };

        var stack = (StackPanel)mainBorder.Child;

        // Header with type info and copy button
        var header = CreateModernHeader(symbol);
        stack.Children.Add(header);

        // Description section
        var description = CreateDescriptionSection(symbol);
        if (description != null)
            stack.Children.Add(description);

        // Parameters section (for methods/constructors)
        var parameters = CreateParametersSection(symbol);
        if (parameters != null)
            stack.Children.Add(parameters);

        // Details grid (2 column layout)
        var details = CreateDetailsGrid(symbol);
        if (details != null)
            stack.Children.Add(details);

        // Documentation comments if available
        var documentation = CreateDocumentationSection(symbol);
        if (documentation != null)
            stack.Children.Add(documentation);

        return mainBorder;
    }

    private static Border CreateModernHeader(ISymbol symbol)
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(44, 44, 46)),
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(6, 6, 0, 0)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };

        // Icon and type badge
        var leftStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        
        var icon = new TextBlock
        {
            Text = GetModernIcon(symbol),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        };
        
        var typeBadge = new Border
        {
            Background = GetKindColor(symbol),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        typeBadge.Child = new TextBlock
        {
            Text = symbol.Kind.ToString().ToUpperInvariant(),
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        };
        
        leftStack.Children.Add(icon);
        leftStack.Children.Add(typeBadge);

        // Name
        var name = new TextBlock
        {
            Text = symbol.Name,
            FontSize = 12,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0)
        };

        // Copy button
        var copyButton = new Button
        {
            Content = "📋",
            Background = Brushes.Transparent,
            Foreground = Brushes.Gray,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Width = 24,
            Height = 24,
            FontSize = 12,
            //ToolTip = new ToolTip { Content = "Copy symbol name" }
        };

        grid.Children.Add(leftStack);
        Grid.SetColumn(leftStack, 0);
        grid.Children.Add(name);
        Grid.SetColumn(name, 1);
        grid.Children.Add(copyButton);
        Grid.SetColumn(copyButton, 2);

        header.Child = grid;
        return header;
    }

    private static Border? CreateDescriptionSection(ISymbol symbol)
    {
        string? description = null;
        
        if (symbol is IMethodSymbol method)
        {
            var parameters = string.Join(", ", method.Parameters.Select(p => $"{GetTypeName(p.Type)} {p.Name}"));
            description = $"{GetTypeName(method.ReturnType)} {method.Name}({parameters})";
        }
        else if (symbol is IPropertySymbol property)
        {
            description = $"{GetTypeName(property.Type)} {property.Name}";
        }
        else if (symbol is IFieldSymbol field)
        {
            description = $"{GetTypeName(field.Type)} {field.Name}";
        }
        else
        {
            return null;
        }

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 31)),
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0, 0, 0, 0)
        };

        var textBlock = new TextBlock
        {
            Text = description,
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
            TextWrapping = TextWrapping.Wrap
        };

        border.Child = textBlock;
        return border;
    }

    private static Border? CreateParametersSection(ISymbol symbol)
    {
        // Check if it's a method or constructor
        IMethodSymbol? method = null;
        
        if (symbol is IMethodSymbol m)
            method = m;

        if (method == null || method.Parameters.Length == 0)
            return null;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 31)),
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0)
        };

        var stack = new StackPanel { Spacing = 4 };

        // Header with parameter count
        var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        
        var paramIcon = new TextBlock
        {
            Text = "📌",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        
        var paramHeader = new TextBlock
        {
            Text = $"Parameters ({method.Parameters.Length})",
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128))
        };
        
        headerStack.Children.Add(paramIcon);
        headerStack.Children.Add(paramHeader);
        stack.Children.Add(headerStack);

        // Parameter list with nice formatting
        foreach (var param in method.Parameters)
        {
            var paramPanel = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(40, 50, 50, 55)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3),
                Margin = new Thickness(0, 1)
            };

            var paramGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
            };

            // Parameter type
            var typeText = new TextBlock
            {
                Text = GetTypeName(param.Type),
                Foreground = new SolidColorBrush(Color.FromRgb(156, 220, 254)),
                FontFamily = new FontFamily("Consolas, monospace"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            // Parameter name
            var nameText = new TextBlock
            {
                Text = param.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                FontFamily = new FontFamily("Consolas, monospace"),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Default value if any
            if (param.HasExplicitDefaultValue)
            {
                var defaultValue = param.ExplicitDefaultValue?.ToString() ?? "null";
                var defaultText = new TextBlock
                {
                    Text = $" = {defaultValue}",
                    Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
                    FontSize = 9,
                    FontStyle = FontStyle.Italic,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                paramGrid.Children.Add(defaultText);
                Grid.SetColumn(defaultText, 2);
            }

            paramGrid.Children.Add(typeText);
            Grid.SetColumn(typeText, 0);
            paramGrid.Children.Add(nameText);
            Grid.SetColumn(nameText, 1);

            paramPanel.Child = paramGrid;
            stack.Children.Add(paramPanel);
        }

        border.Child = stack;
        return border;
    }

    private static Border? CreateDetailsGrid(ISymbol symbol)
    {
        var details = new List<(string Label, string Value, string? Color)>();

        // Accessibility
        details.Add(("Access", symbol.DeclaredAccessibility.ToString(), GetAccessibilityColor(symbol)));

        // Modifiers
        var modifiers = GetModifiers(symbol);
        if (!string.IsNullOrEmpty(modifiers))
            details.Add(("Modifiers", modifiers, null));

        // Namespace
        if (symbol.ContainingNamespace != null)
            details.Add(("Namespace", symbol.ContainingNamespace.ToString() ?? "", "#569CD6"));

        // Assembly
        if (!string.IsNullOrEmpty(symbol.ContainingAssembly?.Name))
            details.Add(("Assembly", symbol.ContainingAssembly.Name, "#6A9955"));

        // For methods - add return type info
        if (symbol is IMethodSymbol method)
        {
            // Add return type explicitly
            details.Add(("Returns", GetTypeName(method.ReturnType) ?? "", "#4EC9B0"));
            
            if (method.TypeParameters.Length > 0)
            {
                var typeParams = string.Join(", ", method.TypeParameters.Select(t => t.Name));
                details.Add(("Type Parameters", typeParams, null));
            }
        }

        // For properties - add accessors
        if (symbol is IPropertySymbol property)
        {
            var accessors = new List<string>();
            if (property.GetMethod != null) accessors.Add("get");
            if (property.SetMethod != null) accessors.Add("set");
            details.Add(("Accessors", string.Join(", ", accessors), "#4EC9B0"));
        }

        // For fields - add modifiers
        if (symbol is IFieldSymbol field)
        {
            if (field.IsConst) details.Add(("Constant", field.ConstantValue?.ToString() ?? "null", "#9CDCFE"));
            if (field.IsReadOnly) details.Add(("ReadOnly", "true", "#9CDCFE"));
        }

        if (!details.Any())
            return null;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 31)),
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0)
        };

        var grid = new Grid();
        
        // Create rows for each detail
        for (int i = 0; i < details.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (int i = 0; i < details.Count; i++)
        {
            var (label, value, color) = details[i];
            
            var labelBlock = new TextBlock
            {
                Text = label + ":",
                Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
                FontSize = 10,
                Margin = new Thickness(0, i > 0 ? 3 : 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            
            var valueBlock = new TextBlock
            {
                Text = value,
                Foreground = !string.IsNullOrEmpty(color) ? new SolidColorBrush(Color.Parse(color)) : new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                FontSize = 10,
                FontFamily = new FontFamily("Consolas, monospace"),
                Margin = new Thickness(0, i > 0 ? 3 : 0, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            grid.Children.Add(labelBlock);
            Grid.SetRow(labelBlock, i);
            Grid.SetColumn(labelBlock, 0);
            
            grid.Children.Add(valueBlock);
            Grid.SetRow(valueBlock, i);
            Grid.SetColumn(valueBlock, 1);
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        border.Child = grid;
        return border;
    }

    private static Border? CreateDocumentationSection(ISymbol symbol)
    {
        // Try to get XML documentation
        var documentation = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrEmpty(documentation))
            return null;

        // Parse summary from XML
        var summary = ExtractSummary(documentation);
        if (string.IsNullOrEmpty(summary))
            return null;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
            Padding = new Thickness(8, 6),
            CornerRadius = new CornerRadius(0, 0, 6, 6)
        };

        var stack = new StackPanel { Spacing = 3 };
        
        var header = new TextBlock
        {
            Text = "📝 Documentation",
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
            Margin = new Thickness(0, 0, 0, 1)
        };
        stack.Children.Add(header);

        var summaryText = new TextBlock
        {
            Text = summary,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(187, 187, 187)),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(summaryText);

        border.Child = stack;
        return border;
    }

    private static string GetTypeName(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named && named.TypeArguments.Length > 0)
        {
            var typeArgs = string.Join(", ", named.TypeArguments.Select(t => GetTypeName(t)));
            return $"{named.Name}<{typeArgs}>";
        }
        return type.Name;
    }

    private static string GetModernIcon(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            SymbolKind.NamedType when symbol is ITypeSymbol ts && ts.TypeKind == TypeKind.Class => "📘",
            SymbolKind.NamedType when symbol is ITypeSymbol ts && ts.TypeKind == TypeKind.Interface => "📙",
            SymbolKind.NamedType when symbol is ITypeSymbol ts && ts.TypeKind == TypeKind.Struct => "📗",
            SymbolKind.NamedType when symbol is ITypeSymbol ts && ts.TypeKind == TypeKind.Enum => "🔢",
            SymbolKind.Method => "⚡",
            SymbolKind.Property => "🔧",
            SymbolKind.Field => "📝",
            SymbolKind.Event => "🔔",
            SymbolKind.Parameter => "📌",
            SymbolKind.Local => "📍",
            //SymbolKind.Constructor => "🏗️",
            _ => "📄"
        };
    }

    private static string GetModifiers(ISymbol symbol)
    {
        var modifiers = new List<string>();
        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol.IsAbstract) modifiers.Add("abstract");
        if (symbol.IsVirtual) modifiers.Add("virtual");
        if (symbol.IsOverride) modifiers.Add("override");
        if (symbol.IsSealed) modifiers.Add("sealed");
        
        if (symbol is IMethodSymbol method)
        {
            if (method.IsAsync) modifiers.Add("async");
            if (method.IsExtensionMethod) modifiers.Add("extension");
        }
        
        return string.Join(" ", modifiers);
    }

    private static string GetAccessibilityColor(ISymbol symbol)
    {
        return symbol.DeclaredAccessibility switch
        {
            Accessibility.Public => "#4EC9B0",
            Accessibility.Private => "#CE9178",
            Accessibility.Protected => "#DCDCAA",
            Accessibility.Internal => "#9CDCFE",
            _ => "#808080"
        };
    }

    private static IBrush GetKindColor(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            SymbolKind.NamedType => new SolidColorBrush(Color.FromRgb(78, 201, 176)),
            SymbolKind.Method => new SolidColorBrush(Color.FromRgb(220, 140, 86)),
            SymbolKind.Property => new SolidColorBrush(Color.FromRgb(156, 220, 254)),
            SymbolKind.Field => new SolidColorBrush(Color.FromRgb(214, 157, 133)),
            _ => new SolidColorBrush(Color.FromRgb(86, 156, 214))
        };
    }

    private static string ExtractSummary(string xmlDoc)
    {
        if (string.IsNullOrEmpty(xmlDoc))
            return string.Empty;
        
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xmlDoc);
            var summary = doc.Descendants("summary").FirstOrDefault();
            if (summary != null)
            {
                var text = string.Concat(summary.Nodes().OfType<System.Xml.Linq.XText>().Select(t => t.Value));
                return text.Trim().Replace("\n", " ").Replace("  ", " ");
            }
        }
        catch
        {
            // Ignore parse errors
        }
        return string.Empty;
    }
}