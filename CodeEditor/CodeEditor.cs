using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Folding;
using Microsoft.CodeAnalysis;
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
    private readonly Brush _currentLineBackground = new SolidColorBrush(Color.FromArgb(40, 100, 100, 100));
    private readonly Brush _selectionBrush = new SolidColorBrush(Color.FromArgb(80, 0, 120, 215));
    private readonly Brush _lineNumberForeground = new SolidColorBrush(Color.FromRgb(128, 128, 128));
    private readonly Brush _foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220));
    private readonly Brush _background = new SolidColorBrush(Color.FromRgb(30, 30, 35));

    public string FilePath { get; private set; }
    public string Code { get; private set; }
    private AdhocWorkspace _workspace;
    private DocumentId _documentId;
    private bool _isUpdating;
    public event EventHandler<string>? TextChanged;
    public event EventHandler? Saved;
    private BreadcrumbBar _breadcrumbBar = new();
    
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
        if (_isUpdating) return;
        
        _isUpdating = true;
        try
        {
            var sourceText = SourceText.From(_editor.Text);
            var newSolution = _workspace.CurrentSolution.WithDocumentText(_documentId, sourceText);
            _workspace.TryApplyChanges(newSolution);
            TextChanged?.Invoke(this, _editor.Text);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    public void CloseEditor()
    {
        _editor.TextChanged -= OnTextChanged;
        var solution = _workspace.CurrentSolution.RemoveDocument(_documentId);
        _workspace.TryApplyChanges(solution);
    }

    public CodeEditor(string filePath, AdhocWorkspace workspace, DocumentId docId, string code)
    {
        FilePath = filePath;
        _workspace = workspace;
        _documentId = docId;
        Code = code;

        // Setup tooltip
        _toolTip = new ToolTip
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5),
            FontSize = 12,
            //Placement = PlacementMode.MousePoint
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
        SetupEventHandlers();
        SetupFolding();
        SetupContextMenu();

        SetBreadcrumbBar();

        var rootGrid = new Grid();

        // 2. Definícia riadkov (Hlavička = Auto, Editor = Zvyšok)
        rootGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        rootGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));

        // 3. Priradenie prvkov do konkrétnych riadkov Gridu
        Grid.SetRow(_breadcrumbBar, 0); // Riadok 0 (hore)
        Grid.SetRow(_editor, 1);        // Riadok 1 (pod breadcrumbom)

        // 4. Pridanie prvkov ako Children do Gridu
        rootGrid.Children.Add(_breadcrumbBar);
        rootGrid.Children.Add(_editor);

        // settings
        SettingsData.Instance.SettingsChanged += (_, _) =>
        {
            var inst = SettingsData.Instance;
            _editor.FontFamily = inst.FontFamily;
            _editor.FontSize = inst.FontSize;
            // ...
        };

        // 5. Nastavenie Gridu ako hlavný obsah widgetu
        Content = rootGrid;
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

    private void SetupEventHandlers()
    {
        _editor.TextChanged += OnTextChanged;
        _editor.TextArea.TextEntered += TextArea_TextEntered;
        _editor.TextArea.TextEntering += TextArea_TextEntering;

        // Ctrl+S save
        _editor.KeyDown += Editor_KeyDown;

        // Ctrl+Click for Go to Definition
        _editor.TextArea.TextView.PointerPressed += Editor_PointerPressed;

        // Tooltip with delay and proper positioning
        _editor.TextArea.TextView.PointerMoved += TextView_PointerMoved;
        _editor.TextArea.TextView.PointerExited += (_, _) => HideTooltip();

        // Hide tooltip when scrolling
        _editor.TextArea.TextView.PointerWheelChanged += (_, _) => HideTooltip();

        // Also hide tooltip when the scroll viewer scrolls
        if (_editor.TextArea.TextView.Parent is ScrollViewer scrollViewer)
        {
            scrollViewer.ScrollChanged += (_, _) => HideTooltip();
        }
    }
    
    private async void Editor_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Check for Ctrl+Click
        var keyModifiers = e.KeyModifiers;
        if (keyModifiers.HasFlag(KeyModifiers.Control))
        {
            var textView = _editor.TextArea.TextView;
            var position = e.GetPosition(textView);
            var tvp = textView.GetPosition(position);
        
            if (tvp.HasValue)
            {
                // Set caret position
                _editor.CaretOffset = _editor.Document.GetOffset(tvp.Value.Line, tvp.Value.Column);
            
                // Go to definition
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    GoToDefinition();
                });
            
                e.Handled = true;
            }
        }
    }
    
    private async void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Save();
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Find dialog
            var dialog = new FindReplaceDialog(_editor, false);
            dialog.Show();
            e.Handled = true;
        }
        else if (e.Key == Key.H && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Replace dialog
            var dialog = new FindReplaceDialog(_editor, true);
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

        contextMenu.Items.Add(cutItem);
        contextMenu.Items.Add(copyItem);
        contextMenu.Items.Add(pasteItem);
        contextMenu.Items.Add(separator1);
        contextMenu.Items.Add(goToDefinitionItem);
        contextMenu.Items.Add(findReferencesItem);
        contextMenu.Items.Add(separator2);
        contextMenu.Items.Add(formatItem);
        contextMenu.Items.Add(formatSelectionItem);
        contextMenu.Items.Add(separator3);
        contextMenu.Items.Add(selectAllItem);
        contextMenu.Items.Add(goToLineItem);

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

            var model = await document.GetSemanticModelAsync();
            var root = await document.GetSyntaxRootAsync();
            if (root == null || model == null) return null;

            var caretOffset = _editor.CaretOffset;
            var token = root.FindToken(caretOffset);
            if (token.Parent == null) return null;

            var symbolInfo = model.GetSymbolInfo(token.Parent);
            var symbol = symbolInfo.Symbol ?? model.GetDeclaredSymbol(token.Parent);

            if (symbol == null && token.Parent.Parent != null)
            {
                symbol = model.GetSymbolInfo(token.Parent.Parent).Symbol;
            }

            return symbol;
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

        var location = symbol.Locations.FirstOrDefault();
        if (location == null || !location.IsInSource)
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
                _isUpdating = true;
                _editor.Text = formattedText.ToString();
                _isUpdating = false;
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
        // Cancel previous tooltip request
        _toolTipCts?.Cancel();
        _toolTipCts = new CancellationTokenSource();
        var token = _toolTipCts.Token;

        var textView = _editor.TextArea.TextView;
        
        // Get the position relative to the text view
        var position = e.GetPosition(textView);
        
        // Also get the scroll offset to adjust position
        var scrollOffset = textView.ScrollOffset;

        try
        {
            await Task.Delay(500, token);

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (token.IsCancellationRequested) return;

                // Convert the mouse position to document coordinates accounting for scroll
                var visualPosition = new Point(position.X + scrollOffset.X, position.Y + scrollOffset.Y);
                
                // Get the position in the text view
                var tvp = textView.GetPosition(visualPosition);
                if (!tvp.HasValue)
                {
                    HideTooltip();
                    return;
                }

                int documentOffset = _editor.Document.GetOffset(tvp.Value.Line, tvp.Value.Column);
                var document = _workspace.CurrentSolution.GetDocument(_documentId);
                if (document == null) return;

                var model = await document.GetSemanticModelAsync();
                var root = await document.GetSyntaxRootAsync();

                if (root == null || model == null || documentOffset < 0 || documentOffset >= root.FullSpan.Length)
                {
                    HideTooltip();
                    return;
                }

                try
                {
                    var token2 = root.FindToken(documentOffset);
                    if (token2.Parent == null)
                    {
                        HideTooltip();
                        return;
                    }

                    var symbolInfo = model.GetSymbolInfo(token2.Parent);
                    var symbol = symbolInfo.Symbol ?? model.GetDeclaredSymbol(token2.Parent);

                    if (symbol == null && token2.Parent.Parent != null)
                    {
                        symbol = model.GetSymbolInfo(token2.Parent.Parent).Symbol;
                    }

                    if (symbol != null)
                    {
                        // Use the extracted SymbolTooltip class
                        _toolTip = SymbolTooltip.CreateRichTooltip(symbol);
                        ToolTip.SetTip(_editor, _toolTip);
                        ToolTip.SetPlacement(_editor, PlacementMode.Pointer);
                        ToolTip.SetIsOpen(_editor, true);
                    }
                    else
                    {
                        HideTooltip();
                    }
                }
                catch (Exception)
                {
                    HideTooltip();
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Write(ex.Message);
        }
    }

    private void HideTooltip()
    {
        _toolTipCts?.Cancel();
        ToolTip.SetIsOpen(_editor, false);
    }

    private async void TextArea_TextEntered(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

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

            CompletionTrigger trigger = e.Text == "."
                ? CompletionTrigger.CreateInsertionTrigger('.')
                : CompletionTrigger.Invoke;

            var results = await completionService.GetCompletionsAsync(document, caretOffset, trigger);
            if (results.ItemsList.Count == 0) return;

            var text = await document.GetTextAsync();
            var filterText = text.GetSubText(results.Span).ToString();

            var items = results.ItemsList;
            
            if (e.Text != ".")
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
                if (e.Text == "." && item.Tags.Contains("Keyword")) continue;
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
