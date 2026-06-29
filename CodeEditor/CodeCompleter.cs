using System.Collections.Immutable;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia;

namespace CAAI;

public class MyCompletionData : ICompletionData
{
    public string Text { get; }
    public ImmutableArray<string> Tags { get; }
    public string? DescriptionText { get; set; }
    public string? Namespace { get; set; }

    public MyCompletionData(string text, ImmutableArray<string> tags, string? description = null, string? ns = null)
    {
        Text = text;
        Tags = tags;
        DescriptionText = description;
        Namespace = ns;
    }

    public IImage? Image => null;

    public object Content => CreateStyledContent();

    public object Description => CreateStyledDescription();

    public double Priority => GetPriority();

    private object CreateStyledContent()
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Icon with color
        var iconBorder = new Border
        {
            Width = 24,
            Height = 20,
            CornerRadius = new CornerRadius(3),
            Background = GetIconBackgroundColor(),
            Child = new TextBlock
            {
                Text = GetIcon(),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // Text
        var textBlock = new TextBlock
        {
            Text = Text,
            FontSize = 13,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Namespace (if available)
        if (!string.IsNullOrEmpty(Namespace))
        {
            var nsText = new TextBlock
            {
                Text = $"{Namespace}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                FontStyle = FontStyle.Italic
            };
            stack.Children.Add(iconBorder);
            stack.Children.Add(textBlock);
            stack.Children.Add(nsText);
        }
        else
        {
            stack.Children.Add(iconBorder);
            stack.Children.Add(textBlock);
        }

        return stack;
    }

    private object CreateStyledDescription()
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(37, 37, 38)),
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(4),
            MaxWidth = 400
        };

        var stack = new StackPanel { Spacing = 6 };

        // Header with icon and type
        var headerStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var typeBadge = new Border
        {
            Background = GetIconBackgroundColor(),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2)
        };
        typeBadge.Child = new TextBlock
        {
            Text = GetTypeDisplayName(),
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        };

        var nameText = new TextBlock
        {
            Text = Text,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            Foreground = GetTextColor()
        };

        headerStack.Children.Add(typeBadge);
        headerStack.Children.Add(nameText);
        stack.Children.Add(headerStack);

        // Description line
        if (!string.IsNullOrEmpty(DescriptionText))
        {
            var descText = new TextBlock
            {
                Text = DescriptionText,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(187, 187, 187)),
                TextWrapping = TextWrapping.Wrap
            };
            stack.Children.Add(descText);
        }

        // Details grid
        var detailsGrid = CreateDetailsGrid();
        if (detailsGrid != null)
        {
            stack.Children.Add(detailsGrid);
        }

        border.Child = stack;
        return border;
    }

    private Grid? CreateDetailsGrid()
    {
        var details = new List<(string Label, string Value)>();
        
        // Add namespace
        if (!string.IsNullOrEmpty(Namespace))
        {
            details.Add(("Namespace", Namespace));
        }

        // Add kind-specific details
        if (Tags.Contains("Method"))
        {
            details.Add(("Kind", "Method"));
        }
        else if (Tags.Contains("Property"))
        {
            details.Add(("Kind", "Property"));
        }
        else if (Tags.Contains("Class"))
        {
            details.Add(("Kind", "Class"));
        }
        else if (Tags.Contains("Interface"))
        {
            details.Add(("Kind", "Interface"));
        }
        else if (Tags.Contains("Struct"))
        {
            details.Add(("Kind", "Struct"));
        }
        else if (Tags.Contains("Enum"))
        {
            details.Add(("Kind", "Enum"));
        }
        else if (Tags.Contains("Field"))
        {
            details.Add(("Kind", "Field"));
        }

        if (!details.Any())
            return null;

        var grid = new Grid();
        
        for (int i = 0; i < details.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (int i = 0; i < details.Count; i++)
        {
            var (label, value) = details[i];
            
            var labelBlock = new TextBlock
            {
                Text = label + ":",
                Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
                FontSize = 10,
                Margin = new Thickness(0, i > 0 ? 4 : 0, 12, 0)
            };
            
            var valueBlock = new TextBlock
            {
                Text = value,
                Foreground = new SolidColorBrush(Color.FromRgb(156, 220, 254)),
                FontSize = 10,
                FontFamily = new FontFamily("Consolas, monospace"),
                Margin = new Thickness(0, i > 0 ? 4 : 0, 0, 0)
            };

            grid.Children.Add(labelBlock);
            Grid.SetRow(labelBlock, i);
            Grid.SetColumn(labelBlock, 0);
            
            grid.Children.Add(valueBlock);
            Grid.SetRow(valueBlock, i);
            Grid.SetColumn(valueBlock, 1);
        }

        return grid;
    }

    private string GetIcon()
    {
        if (Tags.Contains("Method")) return "⚡";
        if (Tags.Contains("Property")) return "🔧";
        if (Tags.Contains("Class")) return "📘";
        if (Tags.Contains("Interface")) return "📙";
        if (Tags.Contains("Struct")) return "📗";
        if (Tags.Contains("Enum")) return "🔢";
        if (Tags.Contains("Field")) return "📝";
        if (Tags.Contains("Event")) return "🔔";
        if (Tags.Contains("Keyword")) return "🔑";
        if (Tags.Contains("Namespace")) return "📦";
        if (Tags.Contains("Extension")) return "🔌";
        if (Tags.Contains("Constructor")) return "🏗️";
        return "📄";
    }

    private IBrush GetIconBackgroundColor()
    {
        if (Tags.Contains("Method")) return new SolidColorBrush(Color.FromRgb(220, 140, 86));
        if (Tags.Contains("Property")) return new SolidColorBrush(Color.FromRgb(156, 220, 254));
        if (Tags.Contains("Class")) return new SolidColorBrush(Color.FromRgb(78, 201, 176));
        if (Tags.Contains("Interface")) return new SolidColorBrush(Color.FromRgb(185, 140, 214));
        if (Tags.Contains("Struct")) return new SolidColorBrush(Color.FromRgb(150, 200, 150));
        if (Tags.Contains("Enum")) return new SolidColorBrush(Color.FromRgb(214, 185, 140));
        if (Tags.Contains("Field")) return new SolidColorBrush(Color.FromRgb(214, 157, 133));
        if (Tags.Contains("Event")) return new SolidColorBrush(Color.FromRgb(200, 150, 200));
        if (Tags.Contains("Keyword")) return new SolidColorBrush(Color.FromRgb(86, 156, 214));
        if (Tags.Contains("Namespace")) return new SolidColorBrush(Color.FromRgb(78, 154, 114));
        return new SolidColorBrush(Color.FromRgb(100, 100, 100));
    }

    private IBrush GetTextColor()
    {
        if (Tags.Contains("Keyword")) return new SolidColorBrush(Color.FromRgb(86, 156, 214));
        if (Tags.Contains("Class")) return new SolidColorBrush(Color.FromRgb(78, 201, 176));
        if (Tags.Contains("Method")) return new SolidColorBrush(Color.FromRgb(220, 140, 86));
        if (Tags.Contains("Property")) return new SolidColorBrush(Color.FromRgb(156, 220, 254));
        return Brushes.White;
    }

    private string GetTypeDisplayName()
    {
        if (Tags.Contains("Method")) return "method";
        if (Tags.Contains("Property")) return "property";
        if (Tags.Contains("Class")) return "class";
        if (Tags.Contains("Interface")) return "interface";
        if (Tags.Contains("Struct")) return "struct";
        if (Tags.Contains("Enum")) return "enum";
        if (Tags.Contains("Field")) return "field";
        if (Tags.Contains("Event")) return "event";
        if (Tags.Contains("Keyword")) return "keyword";
        if (Tags.Contains("Namespace")) return "namespace";
        return "symbol";
    }

    private double GetPriority()
    {
        // Keywords get higher priority
        if (Tags.Contains("Keyword")) return 2;
        // Types get medium priority
        if (Tags.Contains("Class") || Tags.Contains("Interface") || Tags.Contains("Struct") || Tags.Contains("Enum")) return 1;
        // Everything else
        return 0;
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionEventArgs)
    {
        textArea.Document.Replace(completionSegment, this.Text);
    }
}

