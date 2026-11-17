using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Document;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using System;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Markup.Declarative;
using Microsoft.CodeAnalysis.FindSymbols;

namespace CAAI;

public class GoToDefinitionEventArgs : EventArgs
{
    public string? FilePath { get; set; }
    public int Line { get; set; }
    public int Column { get; set; }
    public ISymbol? Symbol { get; set; }
}

public class FindReferencesEventArgs : EventArgs
{
    public ISymbol? Symbol { get; set; }
    public List<ReferenceLocation>? References { get; set; }
}

public class CodeEditor : UserControl
{
    public TextEditor _editor;
    private CompletionWindow? _completionWindow;
    private FoldingManager? _foldingManager;
    private ToolTip _toolTip;
    private CancellationTokenSource? _toolTipCts;
    private int? _hoverOffset;
    private readonly Brush _currentLineBackground = new SolidColorBrush(Color.FromArgb(40, 100, 100, 100));
    private readonly Brush _selectionBrush = new SolidColorBrush(Color.FromArgb(80, 0, 120, 215));
    private readonly Brush _lineNumberForeground = new SolidColorBrush(Color.FromRgb(128, 128, 128));
    private readonly Brush _foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220));
    private readonly Brush _background = new SolidColorBrush(Color.FromRgb(30, 30, 35));
    private QuickActionMargin? _quickActionMargin;
    private CancellationTokenSource? _quickActionRefresh;

    public string FilePath { get; private set; }
    public string Code { get; private set; }
    private AdhocWorkspace _workspace;
    private DocumentId _documentId;
    private bool _isUpdating;
    private readonly DiagnosticRenderer _diagnosticRenderer = new();
    internal DocumentId DocumentId => _documentId;
    public event EventHandler<string>? TextChanged;
    public event EventHandler? Saved;
    public event EventHandler? SaveRequested;
    private BreadcrumbBar _breadcrumbBar = new();
    private BreakpointMargin? _breakpointMargin;

    public void ConfigureDebugging(BreakpointStore breakpoints)
    {
        if (_breakpointMargin != null || !FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return;
        _breakpointMargin = new BreakpointMargin(_editor, breakpoints, FilePath);
        _editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);
    }

    public void ToggleBreakpoint() => _breakpointMargin?.ToggleAtCaret();
    public void SetExecutionLine(int? line) => _breakpointMargin?.SetExecutionLine(line);
    
    public event EventHandler<GoToDefinitionEventArgs>? GoToDefinitionRequested;
    public event EventHandler<FindReferencesEventArgs>? FindReferencesRequested;
    public event EventHandler? FormatDocumentRequested;

    public void SetBreadcrumbBar()
    {
        _breadcrumbBar.BreadcrumbClicked += OnBreadcrumbClicked;
        
        // Update breadcrumb when caret moves
        _editor.TextArea.Caret.PositionChanged += (_, _) => UpdateBreadcrumb();
        _editor.TextChanged += (_, _) => UpdateBreadcrumb();
        
        // Initial update
        UpdateBreadcrumb();
		//DockPanel.SetDock(_breadcrumbBar, Dock.Top);
    }

    private void UpdateBreadcrumb()
    {
        if (_breadcrumbBar == null) return;
        
        var document = _workspace.CurrentSolution.GetDocument(_documentId);
        if (document == null) return;
        
        var root = document.GetSyntaxRootAsync().Result;
        if (root != null)
        {
            var offset = _editor.CaretOffset;
            _breadcrumbBar.Update(root, offset);
        }
    }

    private void OnBreadcrumbClicked(object? sender, BreadcrumbItem item)
    {
        if (item.Node != null)
        {
            // Navigate to the node's location
            var lineSpan = item.Node.GetLocation().GetLineSpan();
            var line = lineSpan.StartLinePosition.Line;
            GoToLine(line + 1);
            
            // Optional: Highlight the node text
            _editor.CaretOffset = item.Span.Start;
            _editor.SelectionStart = item.Span.Start;
            _editor.SelectionLength = item.Span.Length;
        }
    }

    private void OnTextChanged(object? sender, EventArgs e)
    {
        SetDiagnostics([]);
        if (_isUpdating) return;
        
        _isUpdating = true;
        try
        {
            var sourceText = SourceText.From(_editor.Text);
            var newSolution = _workspace.CurrentSolution.WithDocumentText(_documentId, sourceText);
            _workspace.TryApplyChanges(newSolution);
            TextChanged?.Invoke(this, _editor.Text);
            ScheduleQuickActionRefresh();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e) => ScheduleQuickActionRefresh();

    private void ScheduleQuickActionRefresh()
    {
        if (_quickActionMargin is null || this.GetVisualRoot() is null) return;

        _quickActionRefresh?.Cancel();
        _quickActionRefresh?.Dispose();
        var refresh = new CancellationTokenSource();
        _quickActionRefresh = refresh;
        _quickActionMargin.SetActionLine(null);
        _ = RefreshQuickActionMarkerAsync(refresh.Token);
    }

    private async Task RefreshQuickActionMarkerAsync(CancellationToken cancellationToken)
    {
        var caretOffset = _editor.CaretOffset;
        var caretLine = _editor.TextArea.Caret.Line;
        var snapshot = _editor.Text ?? string.Empty;
        try
        {
            await Task.Delay(180, cancellationToken);
            var document = _workspace.CurrentSolution.GetDocument(_documentId);
            if (document is null) return;
            document = document.WithText(SourceText.From(snapshot));
            var actions = await QuickActions.GetActionsAsync(
                document, caretOffset, cancellationToken, includeDocumentActions: false);

            cancellationToken.ThrowIfCancellationRequested();
            if ((_editor.Text ?? string.Empty) != snapshot ||
                _editor.TextArea.Caret.Line != caretLine || this.GetVisualRoot() is null)
                return;

            _quickActionMargin?.SetActionLine(actions.Count > 0 ? caretLine : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Quick action marker refresh failed: {exception.Message}");
            _quickActionMargin?.SetActionLine(null);
        }
    }

    public void CloseEditor()
    {
        _breakpointMargin?.Dispose();
        _quickActionRefresh?.Cancel();
        _quickActionRefresh?.Dispose();
        _quickActionMargin?.Dispose();
        _editor.TextArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _editor.TextChanged -= OnTextChanged;
        var solution = _workspace.CurrentSolution.RemoveDocument(_documentId);
        _workspace.TryApplyChanges(solution);
    }

    internal void RebindWorkspace(AdhocWorkspace workspace)
    {
        _workspace = workspace;
    }

    public CodeEditor(string filePath, AdhocWorkspace workspace, DocumentId docId, string code)
    {
        FilePath = filePath;
        _workspace = workspace;
        _documentId = docId;
        Code = code;

        Content = Build();
    }

    private object Build()
    {
        // Setup tooltip
        _toolTip = new ToolTip
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(5, 2),
            FontSize = 10
        };

        // Initialize editor
        _editor = new TextEditor
        {
            Text = Code,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            ShowLineNumbers = true,
            Background = _background,
            Foreground = _foreground,
            LineNumbersForeground = _lineNumberForeground,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("C#"),
            WordWrap = false
        };

        ConfigureEditorOptions();
        _editor.TextArea.TextView.BackgroundRenderers.Add(_diagnosticRenderer);
        SetupEventHandlers();
        if (FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            _quickActionMargin = new QuickActionMargin(_editor, () => _ = ShowQuickActionsAsync());
            _editor.TextArea.LeftMargins.Insert(0, _quickActionMargin);
        }
        SetupFolding();
        SetupContextMenu();

        SetBreadcrumbBar();

        var rootGrid = new Grid();

        rootGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        rootGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        Grid.SetRow(_breadcrumbBar, 0); 
        Grid.SetRow(_editor, 1);        

        rootGrid.Children.Add(_breadcrumbBar);
        rootGrid.Children.Add(_editor);

        AttachedToVisualTree += (_, _) =>
        {
            SettingsData.Instance.SettingsChanged += OnSettingsChanged;
            ApplyEditorSettings();
            ScheduleQuickActionRefresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            SettingsData.Instance.SettingsChanged -= OnSettingsChanged;
            _quickActionRefresh?.Cancel();
        };
        ApplyEditorSettings();

        return rootGrid;
    }

    private void ConfigureEditorOptions()
    {
        _editor.Options.EnableHyperlinks = false;
        _editor.Options.ShowBoxForControlCharacters = false;
        _editor.Options.HighlightCurrentLine = true;
        _editor.Options.IndentationSize = 4;
        _editor.Options.ConvertTabsToSpaces = false;
        _editor.Options.EnableTextDragDrop = true;
        _editor.Options.CutCopyWholeLine = true;
        _editor.Options.AllowScrollBelowDocument = true;

        // Set colors for current line highlight
        _editor.TextArea.TextView.CurrentLineBackground = _currentLineBackground;
        _editor.TextArea.TextView.CurrentLineBorder = null;
        
        // Set selection colors
        _editor.TextArea.SelectionBrush = _selectionBrush;
        _editor.TextArea.SelectionForeground = null;
        _editor.TextArea.SelectionBorder = null;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplyEditorSettings();

    public void SetDiagnostics(IEnumerable<Diagnostic> diagnostics)
    {
        HideTooltip();
        _diagnosticRenderer.Diagnostics = diagnostics
            .Where(diagnostic => diagnostic.Location.IsInSource &&
                diagnostic.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .ToArray();
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private sealed class DiagnosticRenderer : IBackgroundRenderer
    {
        public Diagnostic[] Diagnostics { get; set; } = [];
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid || textView.Document is not { } document)
                return;

            foreach (var diagnostic in Diagnostics)
            {
                var span = diagnostic.Location.SourceSpan;
                if (span.Start > document.TextLength || span.End > document.TextLength)
                    continue;

                var start = span.Start;
                var length = span.Length;
                if (length == 0 && document.TextLength > 0)
                {
                    start = Math.Min(start, document.TextLength - 1);
                    length = 1;
                }

                var pen = new Pen(diagnostic.Severity == DiagnosticSeverity.Error
                    ? Brushes.Red : Brushes.Orange, 1);
                foreach (var rectangle in BackgroundGeometryBuilder.GetRectsForSegment(
                    textView, new SimpleSegment(start, length)))
                {
                    var right = Math.Max(rectangle.Right, rectangle.Left + 4);
                    for (var position = rectangle.Left; position < right; position += 4)
                    {
                        drawingContext.DrawLine(pen, new Point(position, rectangle.Bottom - 1),
                            new Point(Math.Min(position + 2, right), rectangle.Bottom - 3));
                        drawingContext.DrawLine(pen, new Point(Math.Min(position + 2, right), rectangle.Bottom - 3),
                            new Point(Math.Min(position + 4, right), rectangle.Bottom - 1));
                    }
                }
            }
        }
    }

    private sealed class QuickActionMargin : Control, IDisposable
    {
        private readonly TextEditor _editor;
        private readonly Action _onInvoked;
        private int? _actionLine;

        public QuickActionMargin(TextEditor editor, Action onInvoked)
        {
            _editor = editor;
            _onInvoked = onInvoked;
            Width = 18;
            ToolTip.SetTip(this, "Quick actions available (Ctrl+.)");
            Avalonia.Automation.AutomationProperties.SetName(this, "Quick actions gutter");
        }

        public void SetActionLine(int? line)
        {
            _actionLine = line;
            Cursor = line.HasValue ? new Cursor(StandardCursorType.Hand) : null;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_actionLine is not { } actionLine || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            var view = _editor.TextArea.TextView;
            if (!view.VisualLinesValid) return;
            var position = e.GetPosition(view);
            var hitLine = view.VisualLines.FirstOrDefault(line =>
                position.Y >= line.VisualTop - view.ScrollOffset.Y &&
                position.Y < line.VisualTop - view.ScrollOffset.Y + line.Height);
            if (hitLine is null || actionLine < hitLine.FirstDocumentLine.LineNumber ||
                actionLine > hitLine.LastDocumentLine.LineNumber)
                return;

            _onInvoked();
            e.Handled = true;
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_actionLine is not { } actionLine) return;

            var view = _editor.TextArea.TextView;
            if (!view.VisualLinesValid) return;
            var line = view.VisualLines.FirstOrDefault(candidate =>
                actionLine >= candidate.FirstDocumentLine.LineNumber &&
                actionLine <= candidate.LastDocumentLine.LineNumber);
            if (line is null) return;

            var center = new Point(9, line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.LineMiddle) - view.ScrollOffset.Y);
            var brush = new SolidColorBrush(Color.FromRgb(255, 198, 76));
            context.DrawEllipse(brush, null, center, 5, 5);
            var pen = new Pen(brush, 1.5);
            context.DrawLine(pen, new Point(center.X - 2, center.Y + 5), new Point(center.X + 2, center.Y + 5));
            context.DrawLine(pen, new Point(center.X - 1, center.Y + 7), new Point(center.X + 1, center.Y + 7));
        }

        public void Dispose() => _editor.TextArea.LeftMargins.Remove(this);
    }

    private void ApplyEditorSettings()
    {
        var settings = SettingsData.Instance;
        _editor.FontFamily = new FontFamily(settings.FontFamily);
        _editor.FontSize = settings.FontSize;
        _editor.ShowLineNumbers = settings.ShowLineNumbers;
        _editor.WordWrap = settings.WordWrap;
        _editor.Options.IndentationSize = settings.IndentationSize;
        _editor.Options.ConvertTabsToSpaces = settings.ConvertTabsToSpaces;
        if (Color.TryParse(settings.EditorBackground, out var background))
            _editor.Background = new SolidColorBrush(background);
        if (Color.TryParse(settings.EditorForeground, out var foreground))
            _editor.Foreground = new SolidColorBrush(foreground);
    }

    private void SetupEventHandlers()
    {
        _editor.TextChanged += OnTextChanged;
        _editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        _editor.TextArea.TextEntered += TextArea_TextEntered;
        _editor.TextArea.TextEntering += TextArea_TextEntering;

        // Ctrl+S save
        _editor.KeyDown += Editor_KeyDown;
        _editor.AddHandler(InputElement.KeyDownEvent, Editor_PreviewKeyDown, RoutingStrategies.Tunnel);

        // Ctrl+Click for Go to Definition
        _editor.AddHandler(InputElement.PointerPressedEvent, Editor_PointerPressed, RoutingStrategies.Tunnel);

        // Tooltip with delay and proper positioning
        _editor.TextArea.TextView.AddHandler(InputElement.PointerMovedEvent,
            TextView_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        _editor.TextArea.TextView.PointerExited += (_, _) => HideTooltip();

        // Hide tooltip when scrolling
        _editor.TextArea.TextView.PointerWheelChanged += (_, _) => HideTooltip();

        // Also hide tooltip when the scroll viewer scrolls
        if (_editor.TextArea.TextView.Parent is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollChanged += (_, _) => HideTooltip();
        }
    }
    
    private void Editor_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var textView = _editor.TextArea.TextView;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.GetCurrentPoint(textView).Properties.IsLeftButtonPressed)
        {
            var position = e.GetPosition(textView);
            if (!new Rect(textView.Bounds.Size).Contains(position))
                return;

            var tvp = textView.GetPositionFloor(position + textView.ScrollOffset);
        
            if (tvp.HasValue)
            {
                // Set caret position
                _editor.CaretOffset = _editor.Document.GetOffset(tvp.Value.Line, tvp.Value.Column);
            
                e.Handled = true;
                GoToDefinition();
            }
        }
    }
    
    private async void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9 && e.KeyModifiers == KeyModifiers.None)
        {
            ToggleBreakpoint();
            e.Handled = true;
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Save();
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Find dialog
            var dialog = new FindReplaceDialog(_editor, false);
            if (this.GetVisualRoot() is Window owner)
                dialog.Show(owner);
            else
                dialog.Show();
            e.Handled = true;
        }
        else if (e.Key == Key.H && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Replace dialog
            var dialog = new FindReplaceDialog(_editor, true);
            if (this.GetVisualRoot() is Window owner)
                dialog.Show(owner);
            else
                dialog.Show();
            e.Handled = true;
        }
        else if (e.Key == Key.G && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ShowGoToLineDialog();
            e.Handled = true;
        }
        else if (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Ctrl+D - Go to Definition
            GoToDefinition();
            e.Handled = true;
        }
        else if (e.Key == Key.R && e.KeyModifiers.HasFlag(KeyModifiers.Control) && 
                 e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Ctrl+Shift+R - Find References
            await FindReferences();
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control) && 
                 e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            // Ctrl+Alt+F - Format Document
            FormatDocument();
            e.Handled = true;
        }
    }

    private void SetupFolding()
    {
        _foldingManager = FoldingManager.Install(_editor.TextArea);
        _editor.TextChanged += (_, __) => UpdateFoldings();
        UpdateFoldings();
    }

    private void SetupContextMenu()
    {
        var contextMenu = new ContextMenu();

        // Cut
        var cutItem = new MenuItem { Header = "Cut" };
        cutItem.Click += (_, _) => _editor.Cut();

        // Copy
        var copyItem = new MenuItem { Header = "Copy" };
        copyItem.Click += (_, _) => _editor.Copy();

        // Paste
        var pasteItem = new MenuItem { Header = "Paste" };
        pasteItem.Click += (_, _) => _editor.Paste();

        // Separator
        var separator1 = new Separator();

        // Go to Definition
        var goToDefinitionItem = new MenuItem { Header = "Go to Definition (Ctrl+Click)" };
        goToDefinitionItem.Click += (_, _) => GoToDefinition();

        // Find References
        var findReferencesItem = new MenuItem { Header = "Find All References" };
        findReferencesItem.Click += (_, _) =>_ = FindReferences();

        // Separator
        var separator2 = new Separator();

        var quickActionsItem = new MenuItem { Header = "Quick Actions (Ctrl+.)" };
        quickActionsItem.Click += async (_, _) => await ShowQuickActionsAsync();

        // Format Document
        var formatItem = new MenuItem { Header = "Format Document" };
        formatItem.Click += (_, _) => FormatDocument();

        // Format Selection (optional)
        var formatSelectionItem = new MenuItem { Header = "Format Selection" };
        formatSelectionItem.Click += (_, _) => FormatSelection();

        // Separator
        var separator3 = new Separator();

        // Select All
        var selectAllItem = new MenuItem { Header = "Select All" };
        selectAllItem.Click += (_, _) => _editor.SelectAll();

        // Go to Line
        var goToLineItem = new MenuItem { Header = "Go to Line..." };
        goToLineItem.Click += (_, _) => ShowGoToLineDialog();

        var breakpointItem = new MenuItem { Header = "Toggle Breakpoint", InputGesture = new KeyGesture(Key.F9) };
        breakpointItem.Click += (_, _) => ToggleBreakpoint();

        contextMenu.Items.Add(cutItem);
        contextMenu.Items.Add(copyItem);
        contextMenu.Items.Add(pasteItem);
        contextMenu.Items.Add(separator1);
        contextMenu.Items.Add(goToDefinitionItem);
        contextMenu.Items.Add(findReferencesItem);
        contextMenu.Items.Add(separator2);
        contextMenu.Items.Add(quickActionsItem);
        contextMenu.Items.Add(formatItem);
        contextMenu.Items.Add(formatSelectionItem);
        contextMenu.Items.Add(separator3);
        contextMenu.Items.Add(selectAllItem);
        contextMenu.Items.Add(goToLineItem);
        contextMenu.Items.Add(breakpointItem);

        _editor.ContextMenu = contextMenu;
    }

    // Optional: Format Selection
    private void FormatSelection()
    {
        if (_editor.SelectionLength > 0)
        {
            // Simple formatting - you can enhance this
            var selectedText = _editor.SelectedText;
            // Remove extra whitespace, normalize etc.
            var formatted = System.Text.RegularExpressions.Regex.Replace(selectedText, @"\s+", " ");
            _editor.SelectedText = formatted;
        }
    }

    private void Save()
    {
        if (string.IsNullOrEmpty(FilePath))
        {
            SaveRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            File.WriteAllText(FilePath, _editor.Text);
            Saved?.Invoke(this, EventArgs.Empty);
            
            // Show save confirmation
            _toolTip.Content = "Saved!";
            _toolTip.IsVisible = true;
            Dispatcher.UIThread.Post(HideTooltip, DispatcherPriority.Default);
        }
        catch (Exception ex)
        {
            _toolTip.Content = $"Save error: {ex.Message}";
            _toolTip.IsVisible = true;
            Dispatcher.UIThread.Post(HideTooltip, DispatcherPriority.Default);
        }
    }
    
    // Get symbol at current caret position
    private async Task<ISymbol?> GetSymbolAtCaret()
    {
        try
        {
            var document = _workspace.CurrentSolution.GetDocument(_documentId);
            if (document == null) return null;
            var caretOffset = _editor.CaretOffset;
            return await SymbolFinder.FindSymbolAtPositionAsync(document, caretOffset);
        }
        catch
        {
            return null;
        }
    }

    // Go to definition
    private async void GoToDefinition()
    {
        var symbol = await GetSymbolAtCaret();
        if (symbol == null) return;

        var location = symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        if (location == null)
            return;

        var lineSpan = location.GetLineSpan();
        var filePath = location.SourceTree?.FilePath;

        GoToDefinitionRequested?.Invoke(this, new GoToDefinitionEventArgs
        {
            FilePath = filePath,
            Line = lineSpan.StartLinePosition.Line,
            Column = lineSpan.StartLinePosition.Character,
            Symbol = symbol
        });
    }

    // Find references
    private async Task FindReferences()
    {
        var symbol = await GetSymbolAtCaret();
        if (symbol == null) return;

        try
        {
            var project = _workspace.CurrentSolution.Projects.FirstOrDefault();
            if (project == null) return;

            var references = await SymbolFinder.FindReferencesAsync(symbol, project.Solution);
            var allReferences = references.SelectMany(r => r.Locations).ToList();

            FindReferencesRequested?.Invoke(this, new FindReferencesEventArgs
            {
                Symbol = symbol,
                References = allReferences
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error finding references: {ex.Message}");
        }
    }

    // Format document
    private async void FormatDocument()
    {
        try
        {
            var document = _workspace.CurrentSolution.GetDocument(_documentId);
            if (document == null) return;

            // Use Roslyn formatting
            var formattedDocument = await Microsoft.CodeAnalysis.Formatting.Formatter.FormatAsync(document);
            var formattedText = await formattedDocument.GetTextAsync();

            // Update editor
            Dispatcher.UIThread.Post(() =>
            {
                _editor.Document.Replace(0, _editor.Document.TextLength, formattedText.ToString());
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error formatting: {ex.Message}");
        }

        FormatDocumentRequested?.Invoke(this, null!);
    }

    private void ShowGoToLineDialog()
    {
        var dialog = new GoToLineDialog(_editor);
        dialog.Show();
    }

    private async void UpdateFoldings()
    {
        if (_foldingManager == null) return;

        try
        {
            var newFoldings = new List<NewFolding>();
            var text = _editor.Document.Text;
            var stack = new Stack<int>();

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '{')
                    stack.Push(i);
                else if (text[i] == '}' && stack.Count > 0)
                {
                    int start = stack.Pop();
                    if (i > start + 1)
                    {
                        newFoldings.Add(new NewFolding(start, i + 1)
                        {
                            Name = GetFoldingName(text, start, i)
                        });
                    }
                }
            }

            // Also fold regions
            var regionMatches = Regex.Matches(text, @"#region\s+(.+?)$", RegexOptions.Multiline);
            var endRegionMatches = Regex.Matches(text, @"#endregion", RegexOptions.Multiline);
            
            for (int i = 0; i < regionMatches.Count && i < endRegionMatches.Count; i++)
            {
                var start = regionMatches[i].Index;
                var end = endRegionMatches[i].Index + endRegionMatches[i].Length;
                if (end > start + 1)
                {
                    newFoldings.Add(new NewFolding(start, end)
                    {
                        Name = regionMatches[i].Groups[1].Value
                    });
                }
            }

            newFoldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
            _foldingManager.UpdateFoldings(newFoldings, -1);
        }
        catch (Exception) { /* Ignore folding errors */ }
    }

    private string GetFoldingName(string text, int start, int end)
    {
        try
        {
            // Try to find method/class name before the opening brace
            int lineStart = text.LastIndexOf('\n', start);
            if (lineStart < 0) lineStart = 0;
            var line = text.Substring(lineStart, start - lineStart);
            
            var match = Regex.Match(line, @"\b(class|struct|interface|enum|method)\s+(\w+)");
            if (match.Success)
                return $"{match.Groups[1].Value} {match.Groups[2].Value} {{...}}";
        }
        catch (Exception ex)
        {
            Console.Write(ex.Message);
        }

        return "{...}";
    }

    private async void TextView_PointerMoved(object? sender, PointerEventArgs e)
    {
        var textView = _editor.TextArea.TextView;
        if (!textView.VisualLinesValid) return;
        var position = e.GetPosition(textView);
        if (!new Rect(textView.Bounds.Size).Contains(position))
        {
            HideTooltip();
            return;
        }
        var textPosition = textView.GetPositionFloor(position + textView.ScrollOffset);
        if (!textPosition.HasValue)
        {
            HideTooltip();
            return;
        }
        var line = _editor.Document.GetLineByNumber(textPosition.Value.Line);
        var offset = line.Offset + Math.Clamp(textPosition.Value.Column - 1, 0, line.Length);
        if (_hoverOffset == offset) return;
        HideTooltip();
        _toolTipCts?.Dispose();
        _toolTipCts = new CancellationTokenSource();
        var token = _toolTipCts.Token;
        _hoverOffset = offset;

        try
        {
            await Task.Delay(500, token);
            token.ThrowIfCancellationRequested();
            await ShowTooltipAtOffsetAsync(offset, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Write(ex.Message);
        }
    }

    private ToolTip? CreateDiagnosticTooltip(int offset)
    {
        var matches = _diagnosticRenderer.Diagnostics.Where(diagnostic =>
        {
            var span = diagnostic.Location.SourceSpan;
            if (span.Length > 0) return span.Contains(offset);
            var start = Math.Min(span.Start, Math.Max(0, _editor.Document.TextLength - 1));
            return offset == start;
        }).OrderByDescending(diagnostic => diagnostic.Severity).ToList();
        if (matches.Count == 0) return null;

        var content = new StackPanel { Spacing = 8, MaxWidth = 480 };
        foreach (var diagnostic in matches)
        {
            var entry = new StackPanel { Spacing = 3 };
            entry.Children.Add(new TextBlock
            {
                Text = $"{diagnostic.Severity} {diagnostic.Id}",
                Foreground = diagnostic.Severity == DiagnosticSeverity.Error ? Brushes.LightCoral : Brushes.Orange,
                FontWeight = FontWeight.SemiBold
            });
            entry.Children.Add(new TextBlock { Text = diagnostic.GetMessage(), TextWrapping = TextWrapping.Wrap });
            content.Children.Add(entry);
        }

        return new ToolTip
        {
            Content = content,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8),
            FontSize = 12
        };
    }

    private async Task ShowTooltipAtOffsetAsync(int offset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tooltip = CreateDiagnosticTooltip(offset);
        if (tooltip == null)
        {
            var document = _workspace.CurrentSolution.GetDocument(_documentId);
            if (document == null) return;
            var model = await document.GetSemanticModelAsync(cancellationToken);
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (root == null || model == null || offset < 0 || offset >= root.FullSpan.Length) return;
            var syntaxToken = root.FindToken(offset);
            if (syntaxToken.Parent == null) return;
            var symbol = model.GetSymbolInfo(syntaxToken.Parent, cancellationToken).Symbol ??
                model.GetDeclaredSymbol(syntaxToken.Parent, cancellationToken);
            if (symbol == null && syntaxToken.Parent.Parent != null)
                symbol = model.GetSymbolInfo(syntaxToken.Parent.Parent, cancellationToken).Symbol;
            if (symbol == null) return;
            tooltip = SymbolTooltip.CreateRichTooltip(symbol);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _toolTip = tooltip;
        ToolTip.SetTip(_editor, _toolTip);
        ToolTip.SetPlacement(_editor, PlacementMode.Pointer);
        ToolTip.SetIsOpen(_editor, true);
    }

    private void HideTooltip()
    {
        _toolTipCts?.Cancel();
        _hoverOffset = null;
        ToolTip.SetIsOpen(_editor, false);
    }

    private async void TextArea_TextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        var trigger = e.Text == "."
            ? CompletionTrigger.CreateInsertionTrigger('.')
            : CompletionTrigger.Invoke;
        await ShowCompletionsAsync(e.Text, trigger);
    }

    private void Editor_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemPeriod && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = ShowQuickActionsAsync();
            return;
        }

        if (e.Key != Key.Space || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;

        e.Handled = true;
        _ = ShowCompletionsAsync(null, CompletionTrigger.Invoke);
    }

    private async Task ShowQuickActionsAsync()
    {
        var document = _workspace.CurrentSolution.GetDocument(_documentId);
        if (document is null)
            return;

        var snapshot = _editor.Text ?? "";
        var caretOffset = _editor.CaretOffset;
        document = document.WithText(SourceText.From(snapshot));

        try
        {
            _completionWindow?.Close();
            var actions = await QuickActions.GetActionsAsync(document, caretOffset, CancellationToken.None);
            if ((_editor.Text ?? "") != snapshot || _editor.CaretOffset != caretOffset || this.GetVisualRoot() is null)
                return;

            var menu = new ContextMenu();
            if (actions.Count == 0)
            {
                menu.Items.Add(new MenuItem { Header = "No quick actions available", IsEnabled = false });
            }
            else
            {
                foreach (var action in actions)
                {
                    var item = new MenuItem { Header = action.Title };
                    item.Click += async (_, _) => await ApplyQuickActionAsync(action, document, snapshot);
                    menu.Items.Add(item);
                }
            }

            menu.Open(_editor);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Quick actions failed: {ex.Message}");
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Could not load quick actions", IsEnabled = false });
            menu.Open(_editor);
        }
    }

    private async Task ApplyQuickActionAsync(CodeAction action, Document originalDocument, string snapshot)
    {
        try
        {
            var operations = await action.GetOperationsAsync(CancellationToken.None);
            var changedDocument = operations.OfType<ApplyChangesOperation>()
                .SingleOrDefault()?.ChangedSolution.GetDocument(_documentId);
            if (changedDocument is null || (_editor.Text ?? "") != snapshot)
                return;

            var changes = await changedDocument.GetTextChangesAsync(originalDocument);
            if ((_editor.Text ?? "") != snapshot)
                return;

            _editor.Document.BeginUpdate();
            try
            {
                foreach (var change in changes.OrderByDescending(change => change.Span.Start))
                    _editor.Document.Replace(change.Span.Start, change.Span.Length, change.NewText ?? "");
            }
            finally
            {
                _editor.Document.EndUpdate();
            }
            _editor.Focus();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Quick action failed: {ex.Message}");
        }
    }

    private async Task ShowCompletionsAsync(string? inputText, CompletionTrigger trigger)
    {
        try
        {
            // Sync the workspace immediately
            var newSolution = _workspace.CurrentSolution.WithDocumentText(_documentId, SourceText.From(_editor.Text));
            _workspace.TryApplyChanges(newSolution);

            var document = _workspace.CurrentSolution.GetDocument(_documentId);
            if (document == null) return;

            var caretOffset = _editor.CaretOffset;
            var completionService = CompletionService.GetService(document);
            if (completionService == null) return;

            var results = await completionService.GetCompletionsAsync(document, caretOffset, trigger);
            if (results.ItemsList.Count == 0) return;

            var text = await document.GetTextAsync();
            var filterText = text.GetSubText(results.Span).ToString();

            var items = results.ItemsList;
            
            if (inputText != ".")
            {
                items = items.Where(i => i.FilterText.StartsWith(filterText, StringComparison.OrdinalIgnoreCase))
                             .ToImmutableArray();
            }

            if (!items.Any()) return;

            // Close existing completion window if any
            if (_completionWindow != null)
            {
                _completionWindow.Close();
                _completionWindow = null;
            }

            _completionWindow = new CompletionWindow(_editor.TextArea);
            var data = _completionWindow.CompletionList.CompletionData;

            foreach (var item in items.OrderByDescending(i => i.Rules.SelectionBehavior == CompletionItemSelectionBehavior.HardSelection)
                                      .ThenBy(i => i.SortText))
            {
                if (inputText == "." && item.Tags.Contains("Keyword")) continue;
                // When creating completion items
                data.Add(new MyCompletionData(
                    item.DisplayText, 
                    item.Tags, 
                    description: item.DisplayText,
                    ns: item.DisplayText
                ));
            }

            if (data.Count > 0)
            {
                _completionWindow.StartOffset = results.Span.Start;
                _completionWindow.Show();
                _completionWindow.Closed += (_, _) => _completionWindow = null;
            }
        }
        catch (Exception ex)
        {
            // Log error but don't crash
            System.Diagnostics.Debug.WriteLine($"Completion error: {ex.Message}");
            if (_completionWindow != null)
            {
                _completionWindow.Close();
                _completionWindow = null;
            }
        }
    }

    private void TextArea_TextEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Text != null && _completionWindow != null)
        {
            if (!char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '_')
            {
                _completionWindow.CompletionList.RequestInsertion(e);
            }
        }
    }

    public void FocusEditor() => _editor.Focus();
    public string GetText() => _editor.Text;
    public void SetText(string text)
    {
        _isUpdating = true;
        _editor.Text = text;
        _isUpdating = false;
    }
    
    // This is used by external calls/windows to go to a specific line
    public void GoToLine(int lineNumber)
    {
        if (lineNumber >= 1 && lineNumber <= _editor.Document.LineCount)
        {
            _editor.CaretOffset = _editor.Document.GetOffset(lineNumber, 1);
            _editor.ScrollToLine(lineNumber);
        }
    }
    
    public void FindText(string searchText, bool matchCase = false)
    {
        var options = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = _editor.Text.IndexOf(searchText, options);
        
        if (index >= 0)
        {
            _editor.CaretOffset = index;
            _editor.SelectionStart = index;
            _editor.SelectionLength = searchText.Length;
            _editor.ScrollToLine(_editor.Document.GetLineByOffset(index).LineNumber);
        }
    }
}
