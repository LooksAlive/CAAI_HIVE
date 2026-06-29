using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using System.Text.RegularExpressions;
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

public class NotebookWidget : UserControl
{
    public ObservableCollection<NotebookCell> Cells { get; } = new();
    private ScriptState<object>? _lastState;
    private readonly ScriptOptions _options;
    private readonly ObjectInspectorWidget1 _inspector;
    private ComboBox? _cellTypeCombo;

    public NotebookWidget(ObjectInspectorWidget1 inspector)
    {
        _inspector = inspector;
        
        _options = ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(System.Collections.Generic.List<>).Assembly,
                typeof(System.Text.StringBuilder).Assembly,
                typeof(System.Text.RegularExpressions.Regex).Assembly,
                typeof(System.Linq.Expressions.Expression).Assembly,
                typeof(System.Dynamic.DynamicObject).Assembly,
                typeof(System.Data.DataTable).Assembly,
                typeof(System.Xml.XmlDocument).Assembly
            )
            .AddImports(
                "System", 
                "System.Collections.Generic", 
                "System.Linq",
                "System.Text",
                "System.Text.RegularExpressions",
                "System.Math",
                "System.IO"
            );

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
        clearAllBtn.Click += (_, _) => Cells.Clear();

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
            _cellTypeCombo = new ComboBox
            {
                Width = 120,
                ItemsSource = new[] { "Code", "Markdown" },
                SelectedIndex = cell.Type == CellType.Code ? 0 : 1,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                Foreground = Brushes.White
            };
            _cellTypeCombo.SelectionChanged += (s, e) =>
            {
                cell.Type = _cellTypeCombo.SelectedIndex == 0 ? CellType.Code : CellType.Markdown;
                RefreshCellUI(cellBorder, cell);
            };
            headerPanel.Children.Add(_cellTypeCombo);
            Grid.SetColumn(_cellTypeCombo, 0);

            // Cell number
            var cellNumber = new TextBlock
            {
                Text = $"[{Cells.IndexOf(cell) + 1}]",
                Foreground = Brushes.Gray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
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
            deleteBtn.Click += (_, _) => Cells.Remove(cell);

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

            // Input area
            if (cell.Type == CellType.Code)
            {
                var editor = new TextBox
                {
                    Text = cell.Code,
                    AcceptsReturn = true,
                    AcceptsTab = true,
                    FontFamily = new FontFamily("Cascadia Code, Consolas, Monospace"),
                    FontSize = 12,
                    Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
                    Foreground = Brushes.White,
                    CaretBrush = Brushes.White,
                    MinHeight = 100,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 5)
                };
                editor.TextChanged += (s, e) => cell.Code = editor.Text;
                contentContainer.Children.Add(editor);
            }
            else // Markdown cell
            {
                // Toggle between edit and preview
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

                var mdPreview = new MarkdownScrollViewer
                {
                    Markdown = cell.Code,
                    Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
                    Foreground = Brushes.White,
                    Padding = new Thickness(10),
                    MinHeight = 150,
                    IsVisible = false
                };

                editModeBtn.Click += (_, _) =>
                {
                    mdEditor.IsVisible = true;
                    mdPreview.IsVisible = false;
                };
                previewModeBtn.Click += (_, _) =>
                {
                    mdEditor.IsVisible = false;
                    mdPreview.IsVisible = true;
                    mdPreview.Markdown = cell.Code;
                };

                mdToolbar.Children.Add(editModeBtn);
                mdToolbar.Children.Add(previewModeBtn);
                markdownContainer.Children.Add(mdToolbar);
                markdownContainer.Children.Add(mdEditor);
                markdownContainer.Children.Add(mdPreview);

                contentContainer.Children.Add(markdownContainer);
            }

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
                    outputBorder.IsVisible = !string.IsNullOrEmpty(cell.Output);
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

    private void RefreshCellUI(Border cellBorder, NotebookCell cell)
    {
        // Simple refresh by rebuilding - in production you'd update specific elements
        var parent = cellBorder.Parent as StackPanel;
        if (parent != null)
        {
            var newContent = CreateCellTemplate().Build(cell);
            var index = parent.Children.IndexOf(cellBorder);
            if (index >= 0)
            {
                parent.Children[index] = newContent;
            }
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
        if (string.IsNullOrWhiteSpace(cell.Code)) return;

        cell.IsExecuting = true;
        cell.Output = "";

        try
        {
            if (_lastState == null)
            {
                // First execution: Start a new session
                _lastState = await CSharpScript.RunAsync(cell.Code, _options, globals: new NotebookGlobals());
            }
            else
            {
                // Subsequent execution: Continue from the previous state
                _lastState = await _lastState.ContinueWithAsync(cell.Code);
            }

            var returnValue = _lastState.ReturnValue;
            cell.Output = FormatOutput(returnValue);

            // Update inspector with variables
            if (_inspector != null && _lastState.Variables.Any())
            {
                _inspector.UpdateVariables(_lastState.Variables);
            }
        }
        catch (CompilationErrorException ex)
        {
            cell.Output = "❌ Compilation Error:\n" + string.Join("\n", ex.Diagnostics.Select(d => d.ToString()));
        }
        catch (Exception ex)
        {
            cell.Output = $"❌ Runtime Error: {ex.Message}";
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
                var items = string.Join(", ", enumerable.Take(10));
                var count = enumerable.Count();
                return $"📊 IEnumerable ({count} items): [{items}{(count > 10 ? "..." : "")}]";
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
        ResetSession();
        foreach (var cell in Cells.Where(c => c.Type == CellType.Code))
        {
            await ExecuteCell(cell);
        }
    }

    private void ResetSession()
    {
        _lastState = null;
        foreach (var cell in Cells)
        {
            cell.Output = "";
        }
        _inspector?.Clear();
    }

    public async Task SaveNotebookAsync(string? filePath = null)
    {
        try
        {
            // If no path provided, show save dialog
            if (string.IsNullOrEmpty(filePath))
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Save Notebook",
                    DefaultExtension = "json",
                    Filters = new List<FileDialogFilter>
                    {
                        new() { Name = "Notebook Files", Extensions = { "json" } },
                        new() { Name = "All Files", Extensions = { "*" } }
                    }
                };
                
                var result = await dialog.ShowAsync(this.GetVisualRoot() as Window ?? new Window());
                if (string.IsNullOrEmpty(result))
                    return;
                
                filePath = result;
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
            
            // Show success message
            await ShowNotification($"Notebook saved to: {Path.GetFileName(filePath)}", OutputType.Success);
        }
        catch (Exception ex)
        {
            await ShowNotification($"Error saving notebook: {ex.Message}", OutputType.Error);
        }
    }

    public async Task<bool> LoadNotebookAsync(string? filePath = null)
    {
        try
        {
            // If no path provided, show open dialog
            if (string.IsNullOrEmpty(filePath))
            {
                var dialog = new OpenFileDialog
                {
                    Title = "Open Notebook",
                    AllowMultiple = false,
                    Filters = new List<FileDialogFilter>
                    {
                        new() { Name = "Notebook Files", Extensions = { "json" } },
                        new() { Name = "All Files", Extensions = { "*" } }
                    }
                };
                
                var result = await dialog.ShowAsync(this.GetVisualRoot() as Window ?? new Window());
                if (result == null || result.Length == 0)
                    return false;
                
                filePath = result[0];
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
            ResetSession();
            
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

    public async Task ExportHtmlAsync(string? filePath = null)
    {
        try
        {
            if (string.IsNullOrEmpty(filePath))
            {
                var dialog = new SaveFileDialog
                {
                    Title = "Export Notebook as HTML",
                    DefaultExtension = "html",
                    Filters = new List<FileDialogFilter>
                    {
                        new() { Name = "HTML Files", Extensions = { "html" } },
                        new() { Name = "All Files", Extensions = { "*" } }
                    }
                };
                
                var result = await dialog.ShowAsync(this.GetVisualRoot() as Window ?? new Window());
                if (string.IsNullOrEmpty(result))
                    return;
                
                filePath = result;
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
        // Simple markdown to HTML conversion
        var result = HtmlEncode(markdown);
        // Add simple conversions
        result = Regex.Replace(result, @"\*\*(.*?)\*\*", "<strong>$1</strong>");
        result = Regex.Replace(result, @"\*(.*?)\*", "<em>$1</em>");
        result = Regex.Replace(result, @"^# (.*?)$", "<h1>$1</h1>", RegexOptions.Multiline);
        result = Regex.Replace(result, @"^## (.*?)$", "<h2>$1</h2>", RegexOptions.Multiline);
        result = Regex.Replace(result, @"^### (.*?)$", "<h3>$1</h3>", RegexOptions.Multiline);
        result = result.Replace("\n", "<br>");
        return result;
    }
}

// Simple Markdown viewer for Avalonia
public class MarkdownScrollViewer : ScrollViewer
{
    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownScrollViewer, string>(nameof(Markdown));

    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    static MarkdownScrollViewer()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownScrollViewer>((x, e) => x.OnMarkdownChanged(e));
    }

    private void OnMarkdownChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is string markdown)
        {
            // Simple markdown rendering (you can replace with a proper markdown control)
            var textBlock = new TextBlock
            {
                Text = ConvertMarkdownToPlainText(markdown),
                TextWrapping = TextWrapping.Wrap
            };
            Content = textBlock;
        }
    }

    private string ConvertMarkdownToPlainText(string markdown)
    {
        // Simple conversion - you can use a proper markdown library here
        var result = markdown;
        
        // Headers
        result = Regex.Replace(result, @"^# (.*?)$", "█ $1", RegexOptions.Multiline);
        result = Regex.Replace(result, @"^## (.*?)$", "  ▪ $1", RegexOptions.Multiline);
        result = Regex.Replace(result, @"^### (.*?)$", "    • $1", RegexOptions.Multiline);
        
        // Bold
        result = Regex.Replace(result, @"\*\*(.*?)\*\*", "$1", RegexOptions.Multiline);
        
        // Italic
        result = Regex.Replace(result, @"\*(.*?)\*", "$1", RegexOptions.Multiline);
        
        // Code blocks
        result = Regex.Replace(result, @"```csharp\n(.*?)\n```", "$1", RegexOptions.Singleline);
        result = Regex.Replace(result, @"`(.*?)`", "$1", RegexOptions.Multiline);
        
        return result;
    }
}


public class ObjectInspectorWidget1 : UserControl
{
    private TreeView _treeView;
    private ObservableCollection<TreeViewItem> _items = new();

    public ObjectInspectorWidget1()
    {
        _treeView = new TreeView
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
            Foreground = Brushes.White,
            ItemsSource = _items
        };
        Content = _treeView;
    }

    public void Inspect(string name, object? value)
    {
        var item = new TreeViewItem { Header = $"{name}: {value?.ToString() ?? "null"}" };
        _items.Add(item);
    }

    public void UpdateVariables(IEnumerable<ScriptVariable> variables)
    {
        _items.Clear();
        foreach (var variable in variables)
        {
            var item = new TreeViewItem 
            { 
                Header = $"{variable.Name} ({variable.Type.Name}) = {variable.Value?.ToString() ?? "null"}" 
            };
            _items.Add(item);
        }
    }

    public void Clear()
    {
        _items.Clear();
    }
}