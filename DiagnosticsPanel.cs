using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using Avalonia.Layout;

namespace CAAI;

public enum DiagnosticSeverityType
{
    Info,
    Warning,
    Error
}

public class DiagnosticItem
{
    public string Message { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public DiagnosticSeverityType Severity { get; set; }
}

public class DiagnosticsPanel : UserControl
{
    private readonly ObservableCollection<DiagnosticItem> _items = new();
    private readonly ListBox _listBox = new();

    public event Action<DiagnosticItem>? OnDiagnosticClicked;

    public DiagnosticsPanel()
    {
        _listBox.ItemsSource = _items;

        _listBox.ItemTemplate = new FuncDataTemplate<DiagnosticItem?>((x, _) =>
        {
            if (x == null)
                return new TextBlock { Text = "" };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8
            };

            // Severity icon
            var icon = new TextBlock
            {
                Text = GetIcon(x.Severity),
                Foreground = GetBrush(x.Severity),
                FontWeight = FontWeight.Bold
            };

            // Message
            var message = new TextBlock
            {
                Text = x.Message,
                Foreground = Brushes.White
            };

            // Location (clickable style)
            var location = new TextBlock
            {
                Text = $"({Path.GetFileName(x.FilePath)}:{x.Line},{x.Column})",
                Foreground = Brushes.LightBlue,
                TextDecorations = TextDecorations.Underline,
                Cursor = new Cursor(StandardCursorType.Hand)
            };

            panel.Children.Add(icon);
            panel.Children.Add(message);
            panel.Children.Add(location);

            // Click handling
            panel.PointerPressed += (_, _) =>
            {
                OnDiagnosticClicked?.Invoke(x);
            };

            return panel;
        });

        Content = _listBox;
    }

    // Thread-safe update
    public void SetDiagnostics(IEnumerable<DiagnosticItem> diagnostics)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _items.Clear();

            foreach (var d in diagnostics)
                _items.Add(d);
        });
    }

    private string GetIcon(DiagnosticSeverityType severity) => severity switch
    {
        DiagnosticSeverityType.Error => "❌",
        DiagnosticSeverityType.Warning => "⚠",
        _ => "ℹ"
    };

    private IBrush GetBrush(DiagnosticSeverityType severity) => severity switch
    {
        DiagnosticSeverityType.Error => Brushes.Red,
        DiagnosticSeverityType.Warning => Brushes.Orange,
        _ => Brushes.LightGray
    };
}