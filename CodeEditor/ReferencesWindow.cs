using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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
            BorderThickness = new Thickness(0),
            Margin = new Thickness(10, 0, 10, 10)
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

            var button = new Button
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 6),
                IsEnabled = !string.IsNullOrEmpty(filePath) && File.Exists(filePath)
            };
            ToolTip.SetTip(button, filePath ?? "Source file is unavailable");
            button.Click += (_, _) =>
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
                window.NavigateRequested?.Invoke(filePath, lineSpan.StartLinePosition.Line, lineSpan.StartLinePosition.Character);
            };
            listBox.Items.Add(button);
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

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto, *") };
        panel.Children.Add(header);
        Grid.SetRow(results, 1);
        panel.Children.Add(results);
        window.Content = panel;
        return window;
    }
}