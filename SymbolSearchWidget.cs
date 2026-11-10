using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Collections.ObjectModel;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;

namespace CAAI;

public enum SymbolKindFilter
{
    All,
    Classes,
    Methods,
    Properties,
    Fields,
    Interfaces,
    Enums,
    Structs
}

public record SymbolItemViewModel(ISymbol Symbol, string ProjectName)
{
    public string DisplayName => $"{GetSymbolIcon(Symbol)} {Symbol.Name}";
    
    public string DetailInfo
    {
        get
        {
            try
            {
                var kind = Symbol.Kind.ToString();
                var location = Symbol.ContainingNamespace?.ToString() ?? "global";
                var accessibility = Symbol.DeclaredAccessibility != Accessibility.NotApplicable 
                    ? $" • {Symbol.DeclaredAccessibility.ToString().ToLower()}" 
                    : "";
            
                // Add return type for methods/properties
                if (Symbol is IMethodSymbol method && !method.MethodKind.ToString().Contains("Constructor"))
                {
                    var returnType = method.ReturnType.Name;
                    return $"{kind} • {returnType} {Symbol.Name}{accessibility} • {location}";
                }
                if (Symbol is IPropertySymbol property)
                {
                    var propertyType = property.Type.Name;
                    return $"{kind} • {propertyType} {Symbol.Name}{accessibility} • {location}";
                }
                if (Symbol is IFieldSymbol fieldSymbol)
                {
                    var fieldType = fieldSymbol.Type.Name;
                    return $"{kind} • {fieldType} {Symbol.Name}{accessibility} • {location}";
                }
                if (Symbol is ILocalSymbol local)
                {
                    var localType = local.Type.Name;
                    return $"{kind} • {localType} {Symbol.Name}{accessibility} • {location}";
                }
                if (Symbol is IParameterSymbol parameter)
                {
                    var paramType = parameter.Type.Name;
                    return $"{kind} • {paramType} {Symbol.Name}{accessibility} • {location}";
                }
                
                return $"{kind}{accessibility} • {location}";
            }
            catch (Exception)
            {
                // Fallback if anything goes wrong
                return $"{Symbol.Kind} • {Symbol.Name}";
            }
        }
    }
    
    public IBrush Color
    {
        get
        {
            return Symbol.Kind switch
            {
                SymbolKind.NamedType when Symbol.Name == "ClassDeclaration" => new SolidColorBrush(new Color(255, 78, 201, 176)),
                SymbolKind.NamedType => new SolidColorBrush(new Color(255, 78, 201, 176)),
                SymbolKind.Method => new SolidColorBrush(new Color(255, 220, 140, 86)),
                SymbolKind.Property => new SolidColorBrush(new Color(255, 156, 220, 254)),
                SymbolKind.Field => new SolidColorBrush(new Color(255, 214, 157, 133)),
                SymbolKind.Event => new SolidColorBrush(new Color(255, 200, 150, 200)),
                SymbolKind.Namespace => new SolidColorBrush(new Color(255, 185, 140, 214)),
                //SymbolKind.Enum => new SolidColorBrush(new Color(255, 214, 185, 140)),
                _ => new SolidColorBrush(new Color(255, 150, 150, 150))
            };
        }
    }
    
    private static string GetSymbolIcon(ISymbol symbol)
    {
        return symbol.Kind switch
        {
            SymbolKind.NamedType when symbol.Name == "ClassDeclaration" => "📘",
            SymbolKind.NamedType => "📘",
            SymbolKind.Method => "⚡",
            SymbolKind.Property => "🔧",
            SymbolKind.Field => "📝",
            SymbolKind.Event => "🔔",
            //SymbolKind.Interface => "📙",
            //SymbolKind.Enum => "🔢",
            //SymbolKind.Struct => "📗",
            SymbolKind.Namespace => "📁",
            _ => "📄"
        };
    }
}

public class SymbolSearchWidget : UserControl
{
    public Window? ParentWindow { get; set; }

    private readonly AdhocWorkspace _workspace;
    private TextBox _searchField = null!;
    private ListBox _resultsList = null!;
    private ProgressBar _loadingProgress = null!;
    private TextBlock _statusText = null!;
    private ComboBox _filterCombo = null!;
    private TextBlock _statsText = null!;
    private Border _searchContainer = null!;
    private CancellationTokenSource? _searchCts;
    private readonly ObservableCollection<SymbolItemViewModel> _results = new();
    
    // Recent searches
    private readonly List<string> _recentSearches = new();
    private ComboBox _recentSearchCombo = null!;
    private int _totalSymbols;

    public event Action<ISymbol>? OnSymbolSelected;
    public event Action? OnCancel;
    
    public event EventHandler<GoToDefinitionEventArgs>? OnGoToDefinitionRequested;
    public event EventHandler<FindReferencesEventArgs>? OnFindReferencesRequested;

    public SymbolSearchWidget(AdhocWorkspace workspace, Window? parentWindow = null)
    {
        _workspace = workspace;
        ParentWindow = parentWindow;
    
        // ADD THESE - make the UserControl itself stretch
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
    
        var mainLayout = CreateMainLayout();
        Content = mainLayout;
    
        InitializeEvents();
        LoadRecentSearches();
        _ = AnalyzeWorkspace();
    }
    
    private void CloseDialog()
    {
        var window = this.FindAncestorOfType<Window>();
        if (window != null && window != ParentWindow)
        {
            window.Close();
        }
        OnCancel?.Invoke();
    }

    private Border CreateMainLayout()
    {
        var mainBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow 
            { 
                Blur = 15, 
                OffsetX = 0, 
                OffsetY = 5, 
                Color = Color.Parse("#44000000") 
            }),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Use Grid instead of StackPanel for better splitter support
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto, Auto, Auto, *, Auto, Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Header with title (Row 0)
        var header = CreateHeader();
        mainGrid.Children.Add(header);
        Grid.SetRow(header, 0);

        // Statistics bar (Row 1)
        var statsBar = CreateStatisticsBar();
        mainGrid.Children.Add(statsBar);
        Grid.SetRow(statsBar, 1);

        // Search container (Row 2)
        _searchContainer = CreateSearchContainer();
        mainGrid.Children.Add(_searchContainer);
        Grid.SetRow(_searchContainer, 2);

        // Results container (Row 3 - fills remaining space)
        var resultsContainer = CreateResultsContainer();
        mainGrid.Children.Add(resultsContainer);
        Grid.SetRow(resultsContainer, 3);

        // Splitter between results and footer (Row 4)
        var splitter = new GridSplitter
        {
            Height = 4,
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 2, 0, 2)
        };
        mainGrid.Children.Add(splitter);
        Grid.SetRow(splitter, 4);

        // Footer (Row 5 - fixed height, can be resized with splitter)
        var footer = CreateFooter();
        mainGrid.Children.Add(footer);
        Grid.SetRow(footer, 5);

        mainBorder.Child = mainGrid;
        return mainBorder;
    }

    private Border CreateHeader()
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Padding = new Thickness(15, 10),
            CornerRadius = new CornerRadius(8, 8, 0, 0)
        };

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto")
        };

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titlePanel.Children.Add(new TextBlock { Text = "🔍", FontSize = 18 });
        titlePanel.Children.Add(new TextBlock 
        { 
            Text = "Symbol Search", 
            FontSize = 16, 
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.White
        });

        var closeButton = new Button
        {
            Content = "✖",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 14,
            Foreground = Brushes.Gray,
            Cursor = new Cursor(StandardCursorType.Hand),
            Width = 30,
            Height = 30
        };
        closeButton.Click += (_, _) => OnCancel?.Invoke();

        headerGrid.Children.Add(titlePanel);
        Grid.SetColumn(titlePanel, 0);
        headerGrid.Children.Add(closeButton);
        Grid.SetColumn(closeButton, 1);

        header.Child = headerGrid;
        return header;
    }

    private Border CreateStatisticsBar()
    {
        var statsBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40)),
            Padding = new Thickness(15, 5)
        };

        _statsText = new TextBlock
        {
            Text = "📊 Analyzing workspace...",
            FontSize = 11,
            Foreground = Brushes.Gray
        };

        statsBar.Child = _statsText;
        return statsBar;
    }

    private Border CreateSearchContainer()
    {
        var searchBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40)),
            Padding = new Thickness(15, 10)
        };

        var searchPanel = new StackPanel { Spacing = 10 };

        // Search input row
        var inputRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto")
        };

        _searchField = new TextBox
        {
            Watermark = "Search symbols (type to search...)",
            FontSize = 14,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 65))
        };

        // Filter combo
        _filterCombo = new ComboBox
        {
            Width = 120,
            Margin = new Thickness(5, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White
        };
        _filterCombo.ItemsSource = Enum.GetValues<SymbolKindFilter>();
        _filterCombo.SelectedItem = SymbolKindFilter.All;

        // Loading indicator
        _loadingProgress = new ProgressBar
        {
            IsIndeterminate = true,
            IsVisible = false,
            Width = 20,
            Height = 20,
            Margin = new Thickness(5, 0, 0, 0)
        };

        inputRow.Children.Add(_searchField);
        Grid.SetColumn(_searchField, 0);
        inputRow.Children.Add(_filterCombo);
        Grid.SetColumn(_filterCombo, 1);
        inputRow.Children.Add(_loadingProgress);
        Grid.SetColumn(_loadingProgress, 2);

        searchPanel.Children.Add(inputRow);

        // Recent searches
        var recentPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        recentPanel.Children.Add(new TextBlock 
        { 
            Text = "Recent:", 
            FontSize = 11, 
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        });
        
        _recentSearchCombo = new ComboBox
        {
            Width = 200,
            FontSize = 11,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White
        };
        recentPanel.Children.Add(_recentSearchCombo);
        
        var clearRecentBtn = new Button
        {
            Content = "Clear",
            FontSize = 11,
            Background = Brushes.Transparent,
            Foreground = Brushes.Gray,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        clearRecentBtn.Click += (_, _) => ClearRecentSearches();
        recentPanel.Children.Add(clearRecentBtn);
        
        searchPanel.Children.Add(recentPanel);

        searchBorder.Child = searchPanel;
        return searchBorder;
    }

    private ListBox CreateResultsContainer()
    {
        var resultsBorder = new Border
        {
            MinHeight = 100,  // REMOVE THIS - let it fill available space
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
            Padding = new Thickness(15),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch 
        };

        var resultsPanel = new StackPanel 
        { 
            Spacing = 20,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Results header
        var resultsHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        
        _statusText = new TextBlock
        {
            Text = "No results",
            FontSize = 12,
            Foreground = Brushes.Gray
        };
        
        var clearButton = new Button
        {
            Content = "Clear Results",
            FontSize = 11,
            Background = Brushes.Transparent,
            Foreground = Brushes.Gray,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        clearButton.Click += (_, _) => ClearResults();
        
        resultsHeader.Children.Add(_statusText);
        Grid.SetColumn(_statusText, 0);
        resultsHeader.Children.Add(clearButton);
        Grid.SetColumn(clearButton, 1);
        
        resultsPanel.Children.Add(resultsHeader);

        // TODO: do this ???

        // Results list - REMOVE MaxHeight constraint
        _resultsList = new ListBox
        {
            //Height = 100,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
            BorderThickness = new Thickness(0),
            ItemsSource = _results,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            SelectionMode = SelectionMode.Single
        };
        
        // Custom item template
        _resultsList.ItemTemplate = new FuncDataTemplate<SymbolItemViewModel>((vm, _) =>
        {
            if (vm == null) return new Border();
            
            var border = new Border
            {
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 1, 0, 1),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            
            var stack = new StackPanel();
            
            var nameText = new TextBlock
            {
                Text = vm.DisplayName,
                FontWeight = FontWeight.Bold,
                FontSize = 13,
                Foreground = vm.Color
            };
            
            var detailText = new TextBlock
            {
                Text = vm.DetailInfo,
                FontSize = 11,
                Foreground = Brushes.Gray
            };
            
            stack.Children.Add(nameText);
            stack.Children.Add(detailText);
            
            border.Child = stack;
            
            // Hover effect
            border.PointerEntered += (_, _) => border.Background = new SolidColorBrush(Color.FromArgb(50, 100, 100, 255));
            border.PointerExited += (_, _) => border.Background = Brushes.Transparent;
            
            return border;
        });

        ScrollViewer.SetVerticalScrollBarVisibility(_resultsList, ScrollBarVisibility.Visible);

        //resultsPanel.Children.Add(_resultsList);
        //resultsBorder.Child = resultsPanel;
        return _resultsList;//resultsBorder;
    }

    private Border CreateFooter()
    {
        var footer = new Border
        {
            MinHeight = 100, // Fixed height, can be resized via splitter
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40)),
            Padding = new Thickness(15, 8),
            CornerRadius = new CornerRadius(0, 0, 8, 8)
        };

        var footerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };

        var shortcut1 = new TextBlock { Text = "↑/↓: Navigate", FontSize = 10, Foreground = Brushes.Gray };
        var shortcut2 = new TextBlock { Text = "Enter: Select", FontSize = 10, Foreground = Brushes.Gray };
        var shortcut3 = new TextBlock { Text = "Esc: Close", FontSize = 10, Foreground = Brushes.Gray };
        var shortcut4 = new TextBlock { Text = "Ctrl+F: Focus", FontSize = 10, Foreground = Brushes.Gray };

        footerPanel.Children.Add(shortcut1);
        footerPanel.Children.Add(shortcut2);
        footerPanel.Children.Add(shortcut3);
        footerPanel.Children.Add(shortcut4);

        footer.Child = footerPanel;
        return footer;
    }

    private void InitializeEvents()
    {
        _searchField.TextChanged += (_, _) => UpdateSearch();
        _filterCombo.SelectionChanged += (_, _) => UpdateSearch();

        _recentSearchCombo.SelectionChanged += (_, _) =>
        {
            if (_recentSearchCombo.SelectedItem is string search)
            {
                _searchField.Text = search;
                UpdateSearch();
            }
        };

        _searchField.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) _resultsList.Focus();
            if (e.Key == Key.Escape) OnCancel?.Invoke();
            if (e.Key == Key.Enter) SelectCurrent();
            if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                _searchField.Focus();
                _searchField.SelectAll();
                e.Handled = true;
            }
        };

        _resultsList.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter) SelectCurrent();
            if (e.Key == Key.Escape) OnCancel?.Invoke();
        };
    }

    private async Task AnalyzeWorkspace()
    {
        await Task.Delay(100);
        var project = _workspace.CurrentSolution.Projects.FirstOrDefault();
        if (project == null) return;

        var compilation = await project.GetCompilationAsync();
        if (compilation == null) return;

        var allSymbols = GetAllSymbols(compilation.GlobalNamespace);
        _totalSymbols = allSymbols.Count();
        
        var classCount = allSymbols.Count(s => s.Kind == SymbolKind.NamedType && !s.IsAbstract);
        var methodCount = allSymbols.Count(s => s.Kind == SymbolKind.Method);
        var propertyCount = allSymbols.Count(s => s.Kind == SymbolKind.Property);
        //var interfaceCount = allSymbols.Count(s => s.Kind == SymbolKind.Interface);
        //var enumCount = allSymbols.Count(s => s.Kind == SymbolKind.Enum);
        
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _statsText.Text = $"📊 Workspace Stats: {_totalSymbols} total symbols | " +
                              $"📘 {classCount} classes | ⚡ {methodCount} methods | " +
                              $"🔧 {propertyCount} properties";// | 📙 {interfaceCount} interfaces | 🔢 {enumCount} enums";
        });
    }

    private IEnumerable<ISymbol> GetAllSymbols(INamespaceSymbol namespaceSymbol)
    {
        foreach (var member in namespaceSymbol.GetMembers())
        {
            yield return member;
            
            if (member is INamespaceSymbol ns)
            {
                foreach (var child in GetAllSymbols(ns))
                    yield return child;
            }
            else if (member is INamedTypeSymbol type)
            {
                foreach (var child in type.GetMembers())
                {
                    if (!child.IsImplicitlyDeclared)
                        yield return child;
                }
            }
        }
    }

    private async void UpdateSearch()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        var query = _searchField.Text;
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
        {
            _results.Clear();
            _statusText.Text = "Type at least 2 characters to search";
            _loadingProgress.IsVisible = false;
            return;
        }

        _loadingProgress.IsVisible = true;
        _statusText.Text = "Searching...";

        try
        {
            await Task.Delay(200, token); // Debounce typing

            var project = _workspace.CurrentSolution.Projects.FirstOrDefault();
            if (project == null) return;

            var symbols = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                project.Solution, query, SymbolFilter.All, token);

            var filter = (SymbolKindFilter)(_filterCombo.SelectedItem ?? SymbolKindFilter.All);
            var filteredSymbols = ApplyFilter(symbols, filter);
            
            var projectName = project.Name;
            var viewModels = filteredSymbols.Select(s => new SymbolItemViewModel(s, projectName)).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _results.Clear();
                foreach (var vm in viewModels)
                    _results.Add(vm);
                    
                _statusText.Text = $"Found {_results.Count} result(s)";
                if (_results.Any())
                    _resultsList.SelectedIndex = 0;
                    
                // Add to recent searches
                if (!_recentSearches.Contains(query))
                {
                    _recentSearches.Insert(0, query);
                    if (_recentSearches.Count > 10)
                        _recentSearches.RemoveAt(_recentSearches.Count - 1);
                    SaveRecentSearches();
                    UpdateRecentSearchesList();
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { _statusText.Text = $"Error: {ex.Message}"; });
        }
        finally
        {
            _loadingProgress.IsVisible = false;
        }
    }

    private IEnumerable<ISymbol> ApplyFilter(IEnumerable<ISymbol> symbols, SymbolKindFilter filter)
    {
        return filter switch
        {
            SymbolKindFilter.Classes => symbols.Where(s => s.Kind == SymbolKind.NamedType && !s.IsAbstract),
            SymbolKindFilter.Methods => symbols.Where(s => s.Kind == SymbolKind.Method),
            SymbolKindFilter.Properties => symbols.Where(s => s.Kind == SymbolKind.Property),
            SymbolKindFilter.Fields => symbols.Where(s => s.Kind == SymbolKind.Field),
            //SymbolKindFilter.Interfaces => symbols.Where(s => s.Kind == SymbolKind.Interface),
            //SymbolKindFilter.Enums => symbols.Where(s => s.Kind == SymbolKind.Enum),
            //SymbolKindFilter.Structs => symbols.Where(s => s.Kind == SymbolKind.Struct),
            _ => symbols
        };
    }

    private void SelectCurrent()
    {
        if (_resultsList.SelectedItem is SymbolItemViewModel vm)
            OnSymbolSelected?.Invoke(vm.Symbol);
    }

    private void ClearResults()
    {
        _results.Clear();
        _searchField.Text = "";
        _statusText.Text = "No results";
    }

    private void ClearRecentSearches()
    {
        _recentSearches.Clear();
        SaveRecentSearches();
        UpdateRecentSearchesList();
    }

    private void LoadRecentSearches()
    {
        // Load from settings/preferences
        UpdateRecentSearchesList();
    }

    private void SaveRecentSearches()
    {
        // Save to settings/preferences
    }

    private void UpdateRecentSearchesList()
    {
        _recentSearchCombo.ItemsSource = _recentSearches.ToList();
    }

    public void FocusInput() => _searchField?.Focus();
    public void ClearInput() => _searchField.Text = "";
    public void SetSearchText(string text) => _searchField.Text = text;
    
    private async Task FindReferences(ISymbol? symbol)
    {
        if (symbol == null) return;

        try
        {
            var project = _workspace.CurrentSolution.Projects.FirstOrDefault();
            if (project == null) return;

            var references = await SymbolFinder.FindReferencesAsync(symbol, project.Solution);
            var allReferences = references.SelectMany(r => r.Locations).ToList();

            OnFindReferencesRequested?.Invoke(this, new FindReferencesEventArgs
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
    
    private async void GoToDefinition(ISymbol? symbol)
    {
        if (symbol == null) return;

        var location = symbol.Locations.FirstOrDefault();
        if (location == null || !location.IsInSource)
            return;

        var lineSpan = location.GetLineSpan();
        var filePath = location.SourceTree?.FilePath;

        OnGoToDefinitionRequested?.Invoke(this, new GoToDefinitionEventArgs
        {
            FilePath = filePath,
            Line = lineSpan.StartLinePosition.Line,
            Column = lineSpan.StartLinePosition.Character,
            Symbol = symbol
        });
    }
}