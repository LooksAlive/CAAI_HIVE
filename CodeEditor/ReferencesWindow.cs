using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CAAI;

public class ReferencesWindow : Window
{
    public event Action<string, int, int>? NavigateRequested;

    private ReferencesWindow(ISymbol? symbol)
    {
        Title = $"References to {symbol?.Name ?? "symbol"}";
        Width = 760;
        Height = 450;
        MinWidth = 420;
        MinHeight = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(35, 35, 40));
    }

    public static async Task<ReferencesWindow> CreateAsync(ISymbol? symbol, IReadOnlyList<ReferenceLocation> references)
    {
        var window = new ReferencesWindow(symbol);
        var header = new StackPanel { Margin = new Thickness(14, 12, 14, 10), Spacing = 3 };
        header.Children.Add(new TextBlock
        {
            Text = symbol?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) ?? "References",
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{references.Count} reference{(references.Count == 1 ? "" : "s")}",
            Foreground = Brushes.Gray,
            FontSize = 11
        });

        var listBox = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        var previewTitle = new TextBlock
        {
            Text = "Select a reference to preview",
            Foreground = Brushes.LightGray,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var previewCode = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 215)),
            TextWrapping = TextWrapping.NoWrap
        };
        var previewScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = previewCode
        };

        foreach (var reference in references.OrderBy(item => item.Document.FilePath ?? item.Document.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.Location.SourceSpan.Start))
        {
            var lineSpan = reference.Location.GetLineSpan();
            var filePath = reference.Document.FilePath ?? reference.Location.SourceTree?.FilePath;
            var source = await reference.Document.GetTextAsync();
            var sourceLine = source.Lines.GetLineFromPosition(Math.Clamp(reference.Location.SourceSpan.Start, 0, source.Length));
            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(filePath ?? reference.Document.Name)}  :  {lineSpan.StartLinePosition.Line + 1}, {lineSpan.StartLinePosition.Character + 1}",
                Foreground = Brushes.LightBlue,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            content.Children.Add(new TextBlock
            {
                Text = sourceLine.ToString().Trim(),
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 215)),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{reference.Document.Project.Name}  |  {filePath ?? reference.Document.Name}",
                Foreground = Brushes.Gray,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var item = new ListBoxItem
            {
                Content = content,
                Background = Brushes.Transparent,
                Padding = new Thickness(8, 6),
                Tag = (filePath, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character, source)
            };
            ToolTip.SetTip(item, filePath ?? "Source file is unavailable");
            item.DoubleTapped += (_, _) =>
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
                window.NavigateRequested?.Invoke(filePath, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character);
                window.Close();
            };
            listBox.Items.Add(item);
        }

        Control results = listBox;
        if (references.Count == 0)
        {
            results = new TextBlock
            {
                Text = "No references found.",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var previewOpen = new Button
        {
            Content = "Open",
            HorizontalAlignment = HorizontalAlignment.Right,
            IsEnabled = false
        };

        listBox.SelectionChanged += (_, _) =>
        {
            if (listBox.SelectedItem is not ListBoxItem item || item.Tag is not ValueTuple<string?, int, int, Microsoft.CodeAnalysis.Text.SourceText> selected)
                return;

            var (filePath, line, column, source) = selected;
            previewTitle.Text = $"{Path.GetFileName(filePath ?? "Reference")}:{line + 1},{column + 1}";
            previewOpen.IsEnabled = !string.IsNullOrEmpty(filePath) && File.Exists(filePath);

            var firstLine = Math.Max(0, line - 4);
            var lastLine = Math.Min(source.Lines.Count - 1, line + 4);
            var previewLines = new List<string>();
            for (var index = firstLine; index <= lastLine; index++)
            {
                var prefix = index == line ? " > " : "   ";
                previewLines.Add($"{prefix}{index + 1,5}  {source.Lines[index].ToString()}");
            }
            previewCode.Text = string.Join(Environment.NewLine, previewLines);

            previewOpen.Tag = (filePath, line, column);
        };

        previewOpen.Click += (_, _) =>
        {
            if (previewOpen.Tag is not ValueTuple<string?, int, int> selected) return;
            var (filePath, line, column) = selected;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            window.NavigateRequested?.Invoke(filePath, line, column);
            window.Close();
        };

        var previewHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(8)
        };
        previewHeader.Children.Add(previewTitle);
        Grid.SetColumn(previewOpen, 1);
        previewHeader.Children.Add(previewOpen);

        var previewPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40))
        };
        previewPanel.Children.Add(previewHeader);
        Grid.SetRow(previewScroll, 1);
        previewScroll.Margin = new Thickness(8, 0, 8, 8);
        previewPanel.Children.Add(previewScroll);

        var split = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("280,*")
        };
        split.Children.Add(results);
        var splitter = new GridSplitter
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };
        Grid.SetColumn(splitter, 0);
        split.Children.Add(splitter);
        Grid.SetColumn(previewPanel, 1);
        split.Children.Add(previewPanel);

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto, *") };
        panel.Children.Add(header);
        Grid.SetRow(split, 1);
        panel.Children.Add(split);
        window.Content = panel;
        return window;
    }
}