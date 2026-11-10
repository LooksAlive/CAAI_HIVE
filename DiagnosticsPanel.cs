using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using Avalonia.Layout;
using Avalonia;

namespace CAAI;

public enum DiagnosticSeverityType
{
    Info,
    Warning,
    Error
}

public class DiagnosticItem
{
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public DiagnosticSeverityType Severity { get; set; }
}

public class DiagnosticsPanel : UserControl
{
    private readonly ObservableCollection<DiagnosticItem> _items = new();
    private readonly ListBox _listBox = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0)
    };
    private readonly TextBlock _countText = new();
    private readonly TextBlock _emptyText = new()
    {
        Text = "No diagnostics to show.",
        Foreground = Brushes.Gray,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    public event Action<DiagnosticItem>? OnDiagnosticClicked;

    public DiagnosticsPanel()
    {
        _listBox.ItemsSource = _items;

        _listBox.ItemTemplate = new FuncDataTemplate<DiagnosticItem?>((x, _) =>
        {
            if (x == null)
                return new TextBlock { Text = "" };

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto, Auto, *, Auto"),
                MinHeight = 28,
                Margin = new Thickness(2, 0)
            };

            var severity = new TextBlock
            {
                Text = GetLabel(x.Severity),
                Foreground = GetBrush(x.Severity),
                FontWeight = FontWeight.Bold,
                FontSize = 10,
                Width = 22,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };

            var code = new TextBlock
            {
                Text = x.Code,
                Foreground = Brushes.Gray,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 10, 0)
            };
            Grid.SetColumn(code, 1);

            var message = new TextBlock
            {
                Text = x.Message,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3)
            };
            Grid.SetColumn(message, 2);

            var location = new Button
            {
                Content = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(x.FilePath)
                        ? "No source"
                        : $"{Path.GetFileName(x.FilePath)}:{x.Line}:{x.Column}",
                    Foreground = Brushes.LightBlue,
                    TextDecorations = TextDecorations.Underline,
                    FontSize = 10
                },
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(3, 1),
                Cursor = new Cursor(StandardCursorType.Hand),
                IsEnabled = !string.IsNullOrWhiteSpace(x.FilePath) && x.Line > 0,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(location, x.FilePath);
            Grid.SetColumn(location, 3);
            location.Click += (_, _) => OnDiagnosticClicked?.Invoke(x);

            row.Children.Add(severity);
            row.Children.Add(code);
            row.Children.Add(message);
            row.Children.Add(location);

            row.PointerPressed += (_, e) =>
            {
                if (!location.IsPointerOver)
                    OnDiagnosticClicked?.Invoke(x);
            };

            return row;
        });

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto"),
            Margin = new Thickness(8, 4, 8, 5)
        };
        header.Children.Add(new TextBlock
        {
            Text = "Problems",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        _countText.Foreground = Brushes.Gray;
        _countText.FontSize = 10;
        _countText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_countText, 1);
        header.Children.Add(_countText);

        var listArea = new Grid();
        listArea.Children.Add(_listBox);
        listArea.Children.Add(_emptyText);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto, *") };
        root.Children.Add(header);
        var separator = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(55, 55, 62)),
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(separator, 0);
        root.Children.Add(separator);
        Grid.SetRow(listArea, 1);
        root.Children.Add(listArea);

        _listBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _listBox.SelectedItem is DiagnosticItem selected)
            {
                OnDiagnosticClicked?.Invoke(selected);
                e.Handled = true;
            }
        };

        Content = root;
    }

    // Thread-safe update
    public void SetDiagnostics(IEnumerable<DiagnosticItem> diagnostics)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _items.Clear();

            var items = diagnostics
                .OrderByDescending(item => item.Severity)
                .ThenBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Line)
                .ToList();

            foreach (var d in items)
                _items.Add(d);

            var errors = items.Count(item => item.Severity == DiagnosticSeverityType.Error);
            var warnings = items.Count(item => item.Severity == DiagnosticSeverityType.Warning);
            var info = items.Count - errors - warnings;
            _countText.Text = $"{errors} errors  {warnings} warnings  {info} info";
            _listBox.IsVisible = items.Count > 0;
            _emptyText.IsVisible = items.Count == 0;
        });
    }

    private string GetLabel(DiagnosticSeverityType severity) => severity switch
    {
        DiagnosticSeverityType.Error => "ERR",
        DiagnosticSeverityType.Warning => "WRN",
        _ => "INFO"
    };

    private IBrush GetBrush(DiagnosticSeverityType severity) => severity switch
    {
        DiagnosticSeverityType.Error => Brushes.Red,
        DiagnosticSeverityType.Warning => Brushes.Orange,
        _ => Brushes.LightGray
    };
}