using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Markdig;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.VisualTree;
using Avalonia.Threading;
using System.Text;

namespace CAAI;

public enum CellType
{
    Code,
    Markdown
}

public class NotebookSaveData
{
    public List<NotebookCellData> Cells { get; set; } = new();
    public DateTime SavedAt { get; set; }
    public string? Version { get; set; }
}

public class NotebookCellData
{
    public string Code { get; set; } = "";
    public string Output { get; set; } = "";
    public CellType Type { get; set; }
    public bool IsExpanded { get; set; } = true;
}

public class NotebookCell : INotifyPropertyChanged
{
    private string _code = "";
    private string _output = "";
    private CellType _type = CellType.Code;
    private bool _isExecuting;
    private bool _isExpanded = true;

    public string Code
    {
        get => _code;
        set { _code = value; OnPropertyChanged(); }
    }

    public string Output
    {
        get => _output;
        set { _output = value; OnPropertyChanged(); }
    }

    public CellType Type
    {
        get => _type;
        set { _type = value; OnPropertyChanged(); }
    }

    public bool IsExecuting
    {
        get => _isExecuting;
        set { _isExecuting = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class NotebookGlobals
{
    public string ProjectName { get; set; } = "CAAI Notebook";
    public Dictionary<string, object> Variables { get; } = new();
    
    public void Clear() => Console.Clear();
    public void Print(object value) => Console.WriteLine(value);
    public void Display(object value) => Console.WriteLine($"📊 {value}");
}

internal sealed class NotebookConsoleWriter : TextWriter
{
    private static readonly object Sync = new();
    private static readonly AsyncLocal<CaptureTarget?> Current = new();
    private static bool _installed;
    private readonly TextWriter _fallback;

    private NotebookConsoleWriter(TextWriter fallback) => _fallback = fallback;

    public override Encoding Encoding => _fallback.Encoding;
    public override void Write(char value) => WriteToTarget(writer => writer.Write(value));
    public override void Write(string? value) => WriteToTarget(writer => writer.Write(value));
    public override void WriteLine(string? value) => WriteToTarget(writer => writer.WriteLine(value));
    public override void Flush() => WriteToTarget(writer => writer.Flush());

    private void WriteToTarget(Action<TextWriter> write)
    {
        if (Current.Value is { } target)
            target.Write(write, _fallback);
        else
            write(_fallback);
    }

    public static IDisposable Capture(TextWriter writer)
    {
        lock (Sync)
        {
            if (!_installed)
            {
                Console.SetOut(new NotebookConsoleWriter(Console.Out));
                Console.SetError(new NotebookConsoleWriter(Console.Error));
                _installed = true;
            }
        }

        var previous = Current.Value;
        var target = new CaptureTarget(writer);
        Current.Value = target;
        return new CaptureScope(target, previous);
    }

    private sealed class CaptureTarget(TextWriter writer)
    {
        private readonly object _sync = new();
        private bool _active = true;

        public void Write(Action<TextWriter> write, TextWriter fallback)
        {
            lock (_sync)
                write(_active ? writer : fallback);
        }

        public void Deactivate()
        {
            lock (_sync)
                _active = false;
        }
    }

    private sealed class CaptureScope(CaptureTarget target, CaptureTarget? previous) : IDisposable
    {
        public void Dispose()
        {
            target.Deactivate();
            Current.Value = previous;
        }
    }
}

public class NotebookWidget : UserControl, IDisposable
{
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();
    public ObservableCollection<NotebookCell> Cells { get; } = new();
    private ScriptState<object>? _lastState;
    private readonly ScriptOptions _options;
    private readonly ObjectInspectorWidget1 _inspector;
    private readonly NotebookWorkspace _analysis;
    private readonly Dictionary<NotebookCell, CodeEditor> _cellEditors = new();
    private readonly Dictionary<NotebookCell, TextBlock> _cellDiagnostics = new();
    private readonly Dictionary<NotebookCell, CancellationTokenSource> _diagnosticRequests = new();
    private readonly Dictionary<NotebookCell, TextBlock> _cellNumbers = new();
    private bool _isExecutingNotebook;
    private bool _disposeRequested;
    public string? FilePath { get; private set; }

    public NotebookWidget(ObjectInspectorWidget1 inspector)
    {
        _inspector = inspector;
        
        _options = ScriptOptions.Default
            .AddReferences(NotebookWorkspace.RuntimeReferences)
            .AddImports(
                "System", 
                "System.Collections.Generic", 
                "System.Linq",
                "System.Text",
                "System.Text.RegularExpressions",
                "System.Math",
                "System.IO"
            );

        _analysis = new NotebookWorkspace(_options.Imports);
        _analysis.WorkspaceChanged += workspace =>
        {
            foreach (var editor in _cellEditors.Values)
                editor.RebindWorkspace(workspace);
        };
        _analysis.SessionChanged += () =>
        {
            foreach (var cell in _cellEditors.Keys.ToList())
                _ = UpdateCellDiagnosticsAsync(cell);
        };
        Cells.CollectionChanged += (_, _) =>
        {
            foreach (var cell in _cellEditors.Keys.Where(cell => !Cells.Contains(cell)).ToList())
                RemoveCellEditor(cell);
            foreach (var cell in _cellNumbers.Keys.Where(cell => !Cells.Contains(cell)).ToList())
                _cellNumbers.Remove(cell);
            foreach (var (cell, number) in _cellNumbers)
                number.Text = $"[{Cells.IndexOf(cell) + 1}]";
        };

        BuildUI();
        AddDefaultCells();
    }

    private void BuildUI()
    {
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35))
        };

        // Toolbar
        var toolbar = CreateToolbar();
        mainGrid.Children.Add(toolbar);
        Grid.SetRow(toolbar, 0);

        // Scroll viewer for cells
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        var cellsControl = new ItemsControl
        {
            ItemsSource = Cells,
            ItemTemplate = CreateCellTemplate()
        };

        scrollViewer.Content = cellsControl;
        mainGrid.Children.Add(scrollViewer);
        Grid.SetRow(scrollViewer, 1);

        Content = mainGrid;
    }

    private Border CreateToolbar()
    {
        var toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Padding = new Thickness(10)
        };

        var toolbarPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        var addCodeBtn = new Button
        {
            Content = "📝 + Code",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 5),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(addCodeBtn, "Add Code cell");
        addCodeBtn.Click += (_, _) => AddCell(CellType.Code);

        // Add Markdown Cell button
        var addMarkdownBtn = new Button
        {
            Content = "📄 + Markdown",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 5),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(addMarkdownBtn, "Add Markdown cell");
        addMarkdownBtn.Click += (_, _) => AddCell(CellType.Markdown);

        // Run all button
        var runAllBtn = new Button
        {
            Content = "▶ Run All",
            Background = new SolidColorBrush(Color.FromRgb(0, 100, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 5),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(runAllBtn, "Run all cells");
        runAllBtn.Click += async (_, _) => await RunAllCells();

        // Clear all button
        var clearAllBtn = new Button
        {
            Content = "🗑 Clear All",
            Background = new SolidColorBrush(Color.FromRgb(139, 0, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 5),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(clearAllBtn, "Clear all cells");
        clearAllBtn.Click += (_, _) =>
        {
            if (_isExecutingNotebook) return;
            Cells.Clear();
            ResetSession();
        };

        // Reset session button
        var resetBtn = new Button
        {
            Content = "🔄 Reset Session",
            Background = new SolidColorBrush(Color.FromRgb(100, 100, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 5),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(resetBtn, "Reset session state");
        resetBtn.Click += (_, _) => ResetSession();

        // NEW: Save button
        var saveBtn = new Button
        {
            Content = "💾 Save",
            Background = new SolidColorBrush(Color.FromRgb(0, 100, 150)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(saveBtn, "Save notebook to file");
        saveBtn.Click += async (_, _) => await SaveNotebookAsync();

        // NEW: Load button
        var loadBtn = new Button
        {
            Content = "📂 Load",
            Background = new SolidColorBrush(Color.FromRgb(100, 100, 150)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(loadBtn, "Load notebook from file");
        loadBtn.Click += async (_, _) => await LoadNotebookAsync();

        // NEW: Export as HTML (optional)
        var exportHtmlBtn = new Button
        {
            Content = "🌐 Export HTML",
            Background = new SolidColorBrush(Color.FromRgb(150, 100, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 5),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(exportHtmlBtn, "Export notebook as HTML");
        exportHtmlBtn.Click += async (_, _) => await ExportHtmlAsync();

        toolbarPanel.Children.Add(addCodeBtn);
        toolbarPanel.Children.Add(addMarkdownBtn);
        toolbarPanel.Children.Add(runAllBtn);
        toolbarPanel.Children.Add(clearAllBtn);
        toolbarPanel.Children.Add(resetBtn);
        toolbarPanel.Children.Add(saveBtn);
        toolbarPanel.Children.Add(loadBtn);
        toolbarPanel.Children.Add(exportHtmlBtn);

        toolbar.Child = toolbarPanel;
        return toolbar;
    }

    private IDataTemplate CreateCellTemplate()
    {
        return new FuncDataTemplate<NotebookCell>((cell, _) =>
        {
            var cellBorder = new Border
            {
                Margin = new Thickness(10, 5),
                Padding = new Thickness(10),
                Background = new SolidColorBrush(Color.FromRgb(35, 35, 40)),
                CornerRadius = new CornerRadius(5),
                BorderBrush = new SolidColorBrush(Color.FromRgb(55, 55, 60)),
                BorderThickness = new Thickness(1)
            };

            var mainPanel = new StackPanel { Spacing = 10 };

            // Cell header with type selector and controls
            var headerPanel = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"),
                Margin = new Thickness(0, 0, 0, 10)
            };

            // Cell type indicator and selector
            var cellTypeCombo = new ComboBox
            {
                Width = 120,
                ItemsSource = new[] { "Code", "Markdown" },
                SelectedIndex = cell.Type == CellType.Code ? 0 : 1,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                Foreground = Brushes.White
            };
            headerPanel.Children.Add(cellTypeCombo);
            Grid.SetColumn(cellTypeCombo, 0);

            // Cell number
            var cellNumber = new TextBlock
            {
                Text = $"[{Cells.IndexOf(cell) + 1}]",
                Foreground = Brushes.Gray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            _cellNumbers[cell] = cellNumber;
            headerPanel.Children.Add(cellNumber);
            Grid.SetColumn(cellNumber, 1);

            // Run button (for code cells)
            var runBtn = new Button
            {
                Content = "▶ Run",
                Background = new SolidColorBrush(Color.FromRgb(0, 100, 0)),
                Foreground = Brushes.White,
                Padding = new Thickness(10, 5),
                Margin = new Thickness(5, 0),
                IsVisible = cell.Type == CellType.Code,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            runBtn.Click += async (_, _) => await ExecuteCell(cell);

            // Expand/Collapse button
            var expandBtn = new Button
            {
                Content = cell.IsExpanded ? "▼" : "▶",
                Background = Brushes.Transparent,
                Foreground = Brushes.Gray,
                BorderThickness = new Thickness(0),
                Width = 30,
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            expandBtn.Click += (_, _) =>
            {
                cell.IsExpanded = !cell.IsExpanded;
                expandBtn.Content = cell.IsExpanded ? "▼" : "▶";
                UpdateCellVisibility(cellBorder, cell);
            };

            // Delete button
            var deleteBtn = new Button
            {
                Content = "🗑",
                Background = Brushes.Transparent,
                Foreground = Brushes.Red,
                BorderThickness = new Thickness(0),
                Cursor = new Cursor(StandardCursorType.Hand)
            };
            deleteBtn.Click += (_, _) =>
            {
                if (!_isExecutingNotebook)
                    Cells.Remove(cell);
            };

            headerPanel.Children.Add(runBtn);
            Grid.SetColumn(runBtn, 2);
            headerPanel.Children.Add(expandBtn);
            Grid.SetColumn(expandBtn, 3);
            headerPanel.Children.Add(deleteBtn);
            Grid.SetColumn(deleteBtn, 4);

            mainPanel.Children.Add(headerPanel);

            // Content container (collapsible)
            var contentContainer = new StackPanel
            {
                IsVisible = cell.IsExpanded
            };

            var inputHost = new ContentControl();
            contentContainer.Children.Add(inputHost);
            void UpdateInput()
            {
                inputHost.Content = null;
                RemoveCellEditor(cell);
                if (cell.Type == CellType.Code)
                {
                    inputHost.Content = CreateCodeCellEditor(cell);
                    return;
                }

                var markdownContainer = new StackPanel();

                var mdToolbar = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Margin = new Thickness(0, 5)
                };

                var editModeBtn = new Button
                {
                    Content = "✏️ Edit",
                    Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                    FontSize = 11,
                    Padding = new Thickness(5, 2)
                };
                var previewModeBtn = new Button
                {
                    Content = "👁️ Preview",
                    Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                    FontSize = 11,
                    Padding = new Thickness(5, 2)
                };

                var mdEditor = new TextBox
                {
                    Text = cell.Code,
                    AcceptsReturn = true,
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Monospace"),
                    FontSize = 12,
                    Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
                    Foreground = Brushes.White,
                    MinHeight = 150,
                    TextWrapping = TextWrapping.Wrap,
                    IsVisible = true
                };
                mdEditor.TextChanged += (s, e) => cell.Code = mdEditor.Text;

                var mdPreview = new Markdown.Avalonia.MarkdownScrollViewer
                {
                    Markdown = cell.Code,
                    MinHeight = 150
                };
                var previewBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
                    Padding = new Thickness(10),
                    IsVisible = false,
                    Child = mdPreview
                };

                editModeBtn.Click += (_, _) =>
                {
                    mdEditor.IsVisible = true;
                    previewBorder.IsVisible = false;
                };
                previewModeBtn.Click += (_, _) =>
                {
                    mdEditor.IsVisible = false;
                    previewBorder.IsVisible = true;
                    mdPreview.Markdown = cell.Code;
                };

                mdToolbar.Children.Add(editModeBtn);
                mdToolbar.Children.Add(previewModeBtn);
                markdownContainer.Children.Add(mdToolbar);
                markdownContainer.Children.Add(mdEditor);
                markdownContainer.Children.Add(previewBorder);

                inputHost.Content = markdownContainer;
            }
            UpdateInput();

            // Output area
            var outputBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(25, 25, 30)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 10, 0, 0),
                IsVisible = cell.Type == CellType.Code && !string.IsNullOrEmpty(cell.Output)
            };

            var outputLabel = new TextBlock
            {
                Text = "Output:",
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 5)
            };

            var outputText = new TextBlock
            {
                Text = cell.Output,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Monospace"),
                FontSize = 11,
                Foreground = GetOutputColor(cell.Output),
                TextWrapping = TextWrapping.Wrap
            };

            var outputPanel = new StackPanel();
            outputPanel.Children.Add(outputLabel);
            outputPanel.Children.Add(outputText);
            outputBorder.Child = outputPanel;

            contentContainer.Children.Add(outputBorder);

            cellTypeCombo.SelectionChanged += (_, _) =>
            {
                var selectedType = cellTypeCombo.SelectedIndex == 0 ? CellType.Code : CellType.Markdown;
                if (selectedType == cell.Type) return;
                if (_isExecutingNotebook)
                {
                    cellTypeCombo.SelectedIndex = cell.Type == CellType.Code ? 0 : 1;
                    return;
                }
                cell.Type = selectedType;
                UpdateInput();
                runBtn.IsVisible = selectedType == CellType.Code;
                outputBorder.IsVisible = selectedType == CellType.Code && !string.IsNullOrEmpty(cell.Output);
            };

            // Loading indicator
            var loadingBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(100, 0, 100, 0)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 10, 0, 0),
                IsVisible = false
            };
            var loadingText = new TextBlock
            {
                Text = "⏳ Executing...",
                Foreground = Brushes.Yellow
            };
            loadingBorder.Child = loadingText;
            contentContainer.Children.Add(loadingBorder);

            mainPanel.Children.Add(contentContainer);

            cell.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NotebookCell.Output))
                {
                    outputText.Text = cell.Output;
                    outputBorder.IsVisible = cell.Type == CellType.Code && !string.IsNullOrEmpty(cell.Output);
                    outputText.Foreground = GetOutputColor(cell.Output);
                }
                else if (e.PropertyName == nameof(NotebookCell.IsExecuting))
                {
                    loadingBorder.IsVisible = cell.IsExecuting;
                    runBtn.IsEnabled = !cell.IsExecuting;
                }
            };

            cellBorder.Child = mainPanel;
            return cellBorder;
        });
    }

    private Control CreateCodeCellEditor(NotebookCell cell)
    {
        var documentId = _analysis.GetOrCreateDocument(cell);
        var editor = new CodeEditor("", _analysis.Workspace, documentId, cell.Code)
        {
            MinHeight = 100,
            Height = 180,
            Margin = new Thickness(0, 5)
        };
        var diagnostics = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(235, 155, 140)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        _cellEditors[cell] = editor;
        _cellDiagnostics[cell] = diagnostics;
        editor.TextChanged += (_, text) =>
        {
            cell.Code = text;
            _ = UpdateCellDiagnosticsAsync(cell);
        };
        editor.SaveRequested += async (_, _) => await SaveNotebookAsync();
        var panel = new StackPanel();
        panel.Children.Add(editor);
        panel.Children.Add(diagnostics);
        _ = UpdateCellDiagnosticsAsync(cell);
        return panel;
    }

    private void RemoveCellEditor(NotebookCell cell)
    {
        if (_diagnosticRequests.Remove(cell, out var request))
        {
            request.Cancel();
            request.Dispose();
        }
        _cellEditors.Remove(cell);
        _cellDiagnostics.Remove(cell);
        _analysis.RemoveCell(cell);
    }

    private async Task UpdateCellDiagnosticsAsync(NotebookCell cell)
    {
        if (_disposeRequested || !_cellEditors.TryGetValue(cell, out var editor) || !_cellDiagnostics.TryGetValue(cell, out var output))
            return;
        if (_diagnosticRequests.Remove(cell, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        var request = new CancellationTokenSource();
        _diagnosticRequests[cell] = request;
        var token = request.Token;
        try
        {
            await Task.Delay(300, token);
            var document = _analysis.Workspace.CurrentSolution.GetDocument(_analysis.GetOrCreateDocument(cell));
            if (document is null) return;
            var diagnostics = (await Task.Run(() => EditorAnalysisService.GetDiagnosticsAsync(document, token), token)).ToList();
            var currentDocument = _analysis.Workspace.CurrentSolution.GetDocument(document.Id);
            if (currentDocument == null || !ReferenceEquals(document.Project.Solution, currentDocument.Project.Solution)) return;
            if (token.IsCancellationRequested ||
                !_cellEditors.TryGetValue(cell, out var currentEditor) || currentEditor != editor)
                return;
            editor.SetDiagnostics(diagnostics);
            output.Text = string.Join("\n", diagnostics.Take(3).Select(item =>
                $"{item.Location.GetLineSpan().StartLinePosition.Line + 1}: {item.Id} {item.GetMessage()}"));
            if (diagnostics.Count > 3)
                output.Text += $"\n{diagnostics.Count - 3} more";
            output.IsVisible = diagnostics.Count > 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            output.Text = $"Analysis unavailable: {ex.Message}";
            output.IsVisible = true;
        }
    }

    private void UpdateCellVisibility(Border cellBorder, NotebookCell cell)
    {
        if (cellBorder.Child is StackPanel mainPanel && mainPanel.Children.Count > 1)
        {
            if (mainPanel.Children[1] is StackPanel contentContainer)
            {
                contentContainer.IsVisible = cell.IsExpanded;
            }
        }
    }

    private IBrush GetOutputColor(string output)
    {
        if (output.Contains("Error", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.FromRgb(255, 100, 100));
        if (output.Contains("Warning", StringComparison.OrdinalIgnoreCase))
            return new SolidColorBrush(Color.FromRgb(255, 200, 100));
        return new SolidColorBrush(Color.FromRgb(150, 255, 150));
    }

    private void AddDefaultCells()
    {
        Cells.Add(new NotebookCell 
        { 
            Type = CellType.Markdown,
            Code = @"# Welcome to the Interactive Notebook!

## Features:
- **Code cells** - Write and execute C# code
- **Markdown cells** - Documentation with formatting
- **Persistent session** - Variables persist between cells
- **Object inspector** - Inspect variables

## Example Code:
```csharp
var message = ""Hello, Notebook!"";
message
```"
        });
        
        Cells.Add(new NotebookCell 
        { 
            Type = CellType.Code,
            Code = "var greeting = \"Hello from C#!\";\ngreeting"
        });
    }

    private void AddCell(CellType type)
    {
        Cells.Add(new NotebookCell { Type = type, Code = type == CellType.Code ? "// Write your code here" : "# New markdown cell" });
    }

    private async Task ExecuteCell(NotebookCell cell)
    {
        if (_isExecutingNotebook || string.IsNullOrWhiteSpace(cell.Code)) return;

        _isExecutingNotebook = true;
        try
        {
            await ExecuteCellCore(cell);
        }
        finally
        {
            _isExecutingNotebook = false;
            if (_disposeRequested)
                _analysis.Dispose();
        }
    }

    private async Task<bool> ExecuteCellCore(NotebookCell cell)
    {
        cell.IsExecuting = true;
        cell.Output = "";
        var code = cell.Code;
        using var output = new StringWriter();
        using var capture = NotebookConsoleWriter.Capture(TextWriter.Synchronized(output));

        try
        {
            if (_lastState == null)
            {
                // First execution: Start a new session
                _lastState = await CSharpScript.RunAsync(code, _options, globals: new NotebookGlobals());
            }
            else
            {
                // Subsequent execution: Continue from the previous state
                _lastState = await _lastState.ContinueWithAsync(code);
            }

            _analysis.RecordSubmission(code);
            var returnValue = _lastState.ReturnValue;
            var printed = output.ToString();
            cell.Output = printed + (returnValue is null && printed.Length > 0 ? "" : FormatOutput(returnValue));

            // Update inspector with variables
            if (_inspector != null && _lastState.Variables.Any())
            {
                _inspector.UpdateVariables(_lastState.Variables);
            }
            return true;
        }
        catch (CompilationErrorException ex)
        {
            cell.Output = output + "❌ Compilation Error:\n" + string.Join("\n", ex.Diagnostics.Select(d => d.ToString()));
            return false;
        }
        catch (Exception ex)
        {
            cell.Output = $"{output}❌ Runtime Error: {ex.Message}";
            return false;
        }
        finally
        {
            cell.IsExecuting = false;
        }
    }

    private string FormatOutput(object? value)
    {
        if (value == null) return "null";
        
        try
        {
            // Handle different return types
            if (value is IEnumerable<object> enumerable && !(value is string))
            {
                var items = enumerable.Take(11).ToList();
                return $"📊 IEnumerable: [{string.Join(", ", items.Take(10))}{(items.Count > 10 ? "..." : "")}]";
            }
            
            if (value is Exception ex)
                return $"❌ Exception: {ex.Message}";
            
            return value.ToString() ?? "null";
        }
        catch
        {
            return value.ToString() ?? "null";
        }
    }

    private async Task RunAllCells()
    {
        if (_isExecutingNotebook) return;
        _isExecutingNotebook = true;
        try
        {
            ResetSessionCore();
            foreach (var cell in Cells.Where(c => c.Type == CellType.Code).ToList())
            {
                if (_disposeRequested) break;
                if (!string.IsNullOrWhiteSpace(cell.Code) && !await ExecuteCellCore(cell))
                    break;
            }
        }
        finally
        {
            _isExecutingNotebook = false;
            if (_disposeRequested)
                _analysis.Dispose();
        }
    }

    private void ResetSession(bool clearOutputs = true)
    {
        if (_isExecutingNotebook) return;
        ResetSessionCore(clearOutputs);
    }

    private void ResetSessionCore(bool clearOutputs = true)
    {
        _lastState = null;
        _analysis.Reset();
        if (clearOutputs)
        {
            foreach (var cell in Cells)
            {
                cell.Output = "";
            }
        }
        _inspector?.Clear();
    }

    public async Task<bool> SaveNotebookAsync(string? filePath = null)
    {
        try
        {
            filePath ??= FilePath;
            // If no path provided, show save dialog
            if (string.IsNullOrEmpty(filePath))
            {
                var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storageProvider is null)
                    return false;

                var result = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save Notebook",
                    DefaultExtension = "json",
                    FileTypeChoices = new List<FilePickerFileType>
                    {
                        new("Notebook Files") { Patterns = new[] { "*.json" } },
                        new("All Files") { Patterns = new[] { "*" } }
                    }
                });
                if (result is null)
                    return false;

                filePath = result.Path.LocalPath;
            }

            // Prepare data for serialization
            var saveData = new NotebookSaveData
            {
                SavedAt = DateTime.Now,
                Version = "1.0",
                Cells = Cells.Select(c => new NotebookCellData
                {
                    Code = c.Code,
                    Output = c.Output,
                    Type = c.Type,
                    IsExpanded = c.IsExpanded
                }).ToList()
            };

            // Serialize to JSON with nice formatting
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            
            var json = JsonSerializer.Serialize(saveData, options);
            await File.WriteAllTextAsync(filePath, json);
            FilePath = filePath;
            
            // Show success message
            await ShowNotification($"Notebook saved to: {Path.GetFileName(filePath)}", OutputType.Success);
            return true;
        }
        catch (Exception ex)
        {
            await ShowNotification($"Error saving notebook: {ex.Message}", OutputType.Error);
            return false;
        }
    }

    public async Task<bool> LoadNotebookAsync(string? filePath = null)
    {
        if (_isExecutingNotebook)
            return false;

        try
        {
            // If no path provided, show open dialog
            if (string.IsNullOrEmpty(filePath))
            {
                var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storageProvider is null)
                    return false;

                var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open Notebook",
                    AllowMultiple = false,
                    FileTypeFilter = new List<FilePickerFileType>
                    {
                        new("Notebook Files") { Patterns = new[] { "*.json" } },
                        new("All Files") { Patterns = new[] { "*" } }
                    }
                });
                if (result.Count == 0)
                    return false;

                filePath = result[0].Path.LocalPath;
            }

            // Read and deserialize
            var json = await File.ReadAllTextAsync(filePath);
            var options = new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() }
            };
            
            var saveData = JsonSerializer.Deserialize<NotebookSaveData>(json, options);
            if (saveData == null || saveData.Cells == null)
            {
                await ShowNotification("Invalid notebook file format.", OutputType.Error);
                return false;
            }

            if (_isExecutingNotebook || _disposeRequested)
                return false;

            // Clear existing cells
            Cells.Clear();
            
            // Add loaded cells
            foreach (var cellData in saveData.Cells)
            {
                Cells.Add(new NotebookCell
                {
                    Code = cellData.Code,
                    Output = cellData.Output,
                    Type = cellData.Type,
                    IsExpanded = cellData.IsExpanded
                });
            }

            // Reset session
            ResetSession(clearOutputs: false);
            FilePath = filePath;
            
            await ShowNotification($"Loaded notebook: {Path.GetFileName(filePath)}", OutputType.Success);
            return true;
        }
        catch (Exception ex)
        {
            await ShowNotification($"Error loading notebook: {ex.Message}", OutputType.Error);
            return false;
        }
    }

    // Helper method to show notifications in the output panel
    private async Task ShowNotification(string message, OutputType type = OutputType.Normal)
    {
        // You can integrate this with your output panel
        Console.WriteLine($"[{type}] {message}");
        
        // Or show a temporary status message in the toolbar
        Dispatcher.UIThread.Post(async () =>
        {
            // If you have a status bar or notification area, update it here
            // For now, we'll just use a tooltip or message box
            if (type == OutputType.Error)
            {
                var dialog = new MessageDialog("Notebook", message, true);
                await dialog.ShowDialog(this.GetVisualRoot() as Window ?? new Window());
            }
        });
    }

    public void Dispose()
    {
        if (_disposeRequested) return;
        _disposeRequested = true;
        foreach (var request in _diagnosticRequests.Values)
        {
            request.Cancel();
            request.Dispose();
        }
        _diagnosticRequests.Clear();
        if (!_isExecutingNotebook)
            _analysis.Dispose();
    }

    public async Task ExportHtmlAsync(string? filePath = null)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
            {
                var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storageProvider is null)
                    return;

                var result = await storageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export Notebook as HTML",
                    DefaultExtension = "html",
                    FileTypeChoices = new List<FilePickerFileType>
                    {
                        new("HTML Files") { Patterns = new[] { "*.html" } },
                        new("All Files") { Patterns = new[] { "*" } }
                    }
                });
                if (result is null)
                    return;

                filePath = result.Path.LocalPath;
            }

            var html = GenerateHtml();
            await File.WriteAllTextAsync(filePath, html);
            await ShowNotification($"Exported to: {Path.GetFileName(filePath)}", OutputType.Success);
        }
        catch (Exception ex)
        {
            await ShowNotification($"Error exporting: {ex.Message}", OutputType.Error);
        }
    }

    private string GenerateHtml()
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html>");
        sb.AppendLine("<head>");
        sb.AppendLine("    <meta charset='utf-8'>");
        sb.AppendLine("    <title>Notebook Export</title>");
        sb.AppendLine("    <style>");
        sb.AppendLine("        body { font-family: 'Segoe UI', sans-serif; background: #1e1e1e; color: #d4d4d4; padding: 20px; }");
        sb.AppendLine("        .cell { margin: 20px 0; padding: 15px; background: #252526; border-radius: 6px; border: 1px solid #333; }");
        sb.AppendLine("        .cell-code { background: #1e1e1e; padding: 10px; border-radius: 4px; font-family: Consolas, monospace; }");
        sb.AppendLine("        .cell-markdown { background: #2d2d30; }");
        sb.AppendLine("        .cell-output { background: #1e1e1e; padding: 10px; border-radius: 4px; margin-top: 10px; font-family: Consolas, monospace; }");
        sb.AppendLine("        .cell-error { color: #f48771; }");
        sb.AppendLine("        .cell-warning { color: #dcdcaa; }");
        sb.AppendLine("        .cell-success { color: #6a9955; }");
        sb.AppendLine("    </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine($"    <h1>Notebook Export</h1>");
        sb.AppendLine($"    <p>Exported at: {DateTime.Now}</p>");

        foreach (var cell in Cells)
        {
            sb.AppendLine("    <div class='cell'>");
            
            if (cell.Type == CellType.Code)
            {
                sb.AppendLine($"        <div class='cell-code'>");
                sb.AppendLine($"            <pre><code>{HtmlEncode(cell.Code)}</code></pre>");
                sb.AppendLine($"        </div>");
                
                if (!string.IsNullOrEmpty(cell.Output))
                {
                    var outputClass = GetOutputClass(cell.Output);
                    sb.AppendLine($"        <div class='cell-output {outputClass}'>");
                    sb.AppendLine($"            <strong>Output:</strong><br>");
                    sb.AppendLine($"            <pre>{HtmlEncode(cell.Output)}</pre>");
                    sb.AppendLine($"        </div>");
                }
            }
            else // Markdown
            {
                sb.AppendLine($"        <div class='cell-markdown'>");
                sb.AppendLine($"            <div>{ConvertMarkdownToHtml(cell.Code)}</div>");
                sb.AppendLine($"        </div>");
            }
            
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        
        return sb.ToString();
    }

    private string HtmlEncode(string text)
    {
        return System.Net.WebUtility.HtmlEncode(text);
    }

    private string GetOutputClass(string output)
    {
        if (output.Contains("Error", StringComparison.OrdinalIgnoreCase))
            return "cell-error";
        if (output.Contains("Warning", StringComparison.OrdinalIgnoreCase))
            return "cell-warning";
        return "cell-success";
    }

    private string ConvertMarkdownToHtml(string markdown)
    {
        return Markdig.Markdown.ToHtml(markdown, MarkdownPipeline);
    }
}

public class ObjectInspectorWidget1 : ObjectInspectorWidget
{
}