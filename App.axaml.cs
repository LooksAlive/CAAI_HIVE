using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Input;
using AvaloniaEdit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Avalonia.Controls.Notifications;
using Location = Microsoft.CodeAnalysis.Location;

namespace CAAI;

public sealed class App : Application
{
    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YourApp", "settings.json");

    public override void Initialize()
    {
        //AvaloniaXamlLoader.Load(this);
        //Styles.Add(new SimpleTheme());
        Styles.Add(new FluentTheme());
        /*
		Styles.Add(new Style(selector => selector.OfType<Button>())
		{
			Setters =
			{
				new Setter(TemplatedControl.FontSizeProperty, 6.0),
				new Setter(TemplatedControl.PaddingProperty, new Thickness(4, 2))
			}
		});*/

        //var semiTheme = new SemiTheme();
        //Styles.Add(semiTheme);

        RequestedThemeVariant = ThemeVariant.Default;

        //var editTheme = new Semi.Avalonia.AvaloniaEdit.AvaloniaEditSemiTheme(null);
        //Styles.Add(editTheme);

        // Provide the missing Fluent resource key that Semi doesn't define by default
        // 14 is the standard desktop font size
        Resources["ControlContentThemeFontSize"] = 14.0;

        // If it asks for 'ContentControlThemeFontFamily', add this too:
        Resources["ContentControlThemeFontFamily"] = FontFamily.Default;
        /*
        var editStyles = new StyleInclude(new Uri("avares://CAAI/App.axaml"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
        };
        Styles.Add(editStyles);
		*/
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            SettingsData.Instance.CopyFrom(SettingsData.Instance.Load(SettingsPath));
            desktop.MainWindow = new MainWindow();
#if DEBUG
			desktop.MainWindow.AttachDevTools();
#endif
            desktop.MainWindow.AddHandler(InputElement.KeyDownEvent, GlobalHotReloadInterceptor, RoutingStrategies.Tunnel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void GlobalHotReloadInterceptor(object? sender, KeyEventArgs e)
    {
        // Change to your preferred hot reload key combination
        if (e.Key == Key.F5 && e.KeyModifiers == KeyModifiers.None)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows)
                {
                    // Scan the entire UI hierarchy for your custom base controls
                    var reloadableControls = window.GetVisualDescendants()
                        .OfType<CAAIUserControl>();

                    foreach (var control in reloadableControls)
                    {
                        control.ReloadLayout();
                    }
                }

                e.Handled = true;
            }
        }
    }
}

public static class DialogHelper
{
    public static async Task<ISymbol?> ShowSymbolSearchDialog(Window parent, AdhocWorkspace workspace,
        Action<GoToDefinitionEventArgs>? navigateToLocation = null)
    {
        var searchWidget = new SymbolSearchWidget(workspace, parent);

        var dialog = new Window
        {
            Title = "Search Symbols",
            Width = 600,
            Height = 550,
            MinWidth = 400,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SystemDecorations = SystemDecorations.Full,
            ShowInTaskbar = false,
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome,
            Background = Brushes.Transparent,
            Padding = new Thickness(0),  // Remove any default padding
        };

        // Use a Grid to force the content to fill the window
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        grid.Children.Add(searchWidget);
        dialog.Content = grid;

        var tcs = new TaskCompletionSource<ISymbol?>();

        searchWidget.OnSymbolSelected += symbol =>
        {
            tcs.TrySetResult(symbol);
            dialog.Close();
        };

        searchWidget.OnGoToDefinitionRequested += (_, location) =>
        {
            if (navigateToLocation == null) return;
            tcs.TrySetResult(null);
            dialog.Close();
            navigateToLocation(location);
        };

        dialog.Opened += (_, _) => searchWidget.FocusInput();
        dialog.Closed += (_, _) => tcs.TrySetResult(null);

        searchWidget.OnCancel += () =>
        {
            tcs.TrySetResult(null);
            dialog.Close();
        };

        // Handle Escape key at dialog level too
        dialog.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
            {
                tcs.TrySetResult(null);
                dialog.Close();
            }
        };

        await dialog.ShowDialog(parent);
        return await tcs.Task;
    }
}

public class HierarchyNode
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public List<HierarchyNode> DerivedTypes { get; set; } = [];
}

public class MainWindow : Window
{
    private TabControl _tabs = new TabControl
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
        Foreground = Brushes.White,
        TabStripPlacement = Dock.Top
    };
    private DockPanel _sideTabs = null!;
    private readonly CodeStructureWidget _structureTree = new();
    private FileExplorerWidget _filesTreeView = null!;
    private readonly ObjectInspectorWidget1 _inspector = new();
    // Inside MainWindow class
    private AdhocWorkspace _workspace = null!;
    private ProjectId _projectId = null!;
    private readonly RunDebugToolbar _toolbar = new();
    private readonly DiagnosticsPanel _diagnosticsPanel = new();
    private NuGetPackageManager _nuGetPackageManager = null!;
    private TabControl _outputTabs = null!;
    private Grid _mainGrid = null!;
    private Grid _outputDock = null!;
    private GridSplitter _outputSplitter = null!;
    private GridLength _outputHeight = new(250);
    private readonly Dictionary<DocumentId, List<Diagnostic>> _documentDiagnostics = new();
    private readonly Dictionary<DocumentId, CancellationTokenSource> _diagnosticRequests = new();
    private readonly TextBlock _statusFile = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _statusDiagnostics = new() { Width = 160, TextAlignment = TextAlignment.Right };
    private readonly TextBlock _statusPosition = new() { Width = 220, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _statusMode = new() { Width = 140, TextAlignment = TextAlignment.Right };
    private TextEditor? _statusEditor;
    private TabItem _nuGetTab = null!;
    private TerminalWidget _terminal = null!;
    private readonly MainMenuWidget _menu = new();
    private TreeView _hierarchyTree;
    private EnvironmentWidget _env = null!;
    private readonly AstViewer _astViewer = new();
    private DotnetRunner _runner;
    private readonly WindowNotificationManager _notificationManager = new WindowNotificationManager()
    {
        Position = NotificationPosition.TopRight,
        MaxItems = 3
    };


    SyntaxTree? currentSyntaxTree = null;

    private static string _baseDir = "C:\\Users\\A200542365\\OneDrive - Deutsche Telekom AG\\Desktop\\CAAI_HIVE";
    public CodeWatcher CodeWatcher = new CodeWatcher(_baseDir, file =>
    {
        Console.WriteLine($"Changed: {file}");

        var code = File.ReadAllText(file);

        var (dll, pdb) = RoslynCompiler.Compile(code);

        if (dll == null) return;

        // First load
        var assembly = Assembly.Load(dll, pdb);

        // TODO:
        // - create baseline
        // - store HotReloadService
        // - apply updates on next changes
    });

    private void ShowNotification_Click(object? sender, RoutedEventArgs e)
    {
        // Build a built-in Notification object
        var toast = new Notification(
            title: "Download Complete",
            message: "Your dataset export succeeded.",
            type: NotificationType.Success,
            expiration: TimeSpan.FromSeconds(5),
            onClick: () => Console.WriteLine("User clicked the notification!"),
            onClose: () => Console.WriteLine("Notification dismissed.")
        );

        // Send to the manager screen container
        _notificationManager.Show(toast);
    }

    private async Task LoadProjectFromFolder(string folderPath)
    {
        // 1. Get all .cs files in the directory and subdirectories
        var files = Directory.GetFiles(folderPath, "*.cs", SearchOption.AllDirectories);

        foreach (var filePath in files)
        {
            if (_workspace.CurrentSolution.GetDocumentIdsWithFilePath(Path.GetFullPath(filePath)).Any())
                continue;

            // 2. Read the file content from disk once
            string code = await File.ReadAllTextAsync(filePath);

            // 3. Create a DocumentId and add it to your existing project
            var docId = DocumentId.CreateNewId(_projectId);

            // 4. Update the current solution in memory
            var oldSolution = _workspace.CurrentSolution;
            var newSolution = oldSolution.AddDocument(docId, Path.GetFileName(filePath), code)
                .WithDocumentFilePath(docId, Path.GetFullPath(filePath));

            // Apply the change so the workspace "knows" about the file
            if (_workspace.TryApplyChanges(newSolution))
            {
                // Optional: Map the file path to the DocId so you can find it later
                // _pathMap[filePath] = docId;
            }
        }

        _nuGetPackageManager.SetProjectFolder(folderPath);
    }

    private void InitializeRoslyn()
    {
        _workspace = new AdhocWorkspace();
        _projectId = ProjectId.CreateNewId();

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithConcurrentBuild(false) // reduces caches
            .WithNullableContextOptions(NullableContextOptions.Disable)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithReportSuppressedDiagnostics(false);

        // 1. Create ProjectInfo
        var projectInfo = ProjectInfo.Create(
            _projectId,
            VersionStamp.Create(),
            "HIVE_Project",
            "HIVE_Assembly",
            LanguageNames.CSharp,
            compilationOptions: options
        );

        // 2. Add Project
        var solution = _workspace.CurrentSolution.AddProject(projectInfo);
        var references = GetDefaultReferences();
        foreach (var res in references)
        {
            solution = solution.AddMetadataReference(_projectId, res);
        }
        // 4. Apply everything back to the Workspace
        if (/*_workspace.CanApplyChange(ApplyChangesKind.) && */!_workspace.TryApplyChanges(solution))
        {
            // Handle failure if necessary
        }
    }

    public async Task PerformRename(ISymbol symbol, string newName)
    {
        var options = new SymbolRenameOptions
        {
            RenameFile = true,
            RenameInComments = true,
            RenameInStrings = true,
            RenameOverloads = true
        };
        var newSolution = await Renamer.RenameSymbolAsync(
            _workspace.CurrentSolution,
            symbol,
            options,
            newName);

        // 1. Calculate the rename across the whole workspace
        /*var newSolution = await Renamer.RenameSymbolAsync(
            _workspace.CurrentSolution,
            symbol,
            newName,
            _workspace.Options);
        */
        // 2. Apply the change to the Workspace
        // This will trigger 'TextChanged' in all open CodeEditor tabs!
        _workspace.TryApplyChanges(newSolution);
    }

    public async Task FindReferences(ISymbol symbol)
    {
        // This looks through every document in your workspace
        var references = await SymbolFinder.FindReferencesAsync(symbol, _workspace.CurrentSolution);

        foreach (var reference in references)
        {
            foreach (var location in reference.Locations)
            {
                // Location contains DocumentId, TextSpan (start/end), and Line/Column
                Console.WriteLine($"Found use in {location.Document.Name} at {location.Location.SourceSpan}");
            }
        }
    }

    private DocumentId RegisterFileInWorkspace(string filePath, string content)
    {
        var fullPath = Path.GetFullPath(filePath);
        var existingId = _workspace.CurrentSolution.GetDocumentIdsWithFilePath(fullPath).FirstOrDefault();
        if (existingId is not null)
        {
            _workspace.TryApplyChanges(_workspace.CurrentSolution.WithDocumentText(existingId, SourceText.From(content)));
            return existingId;
        }
        // You don't create the ID manually; the Workspace gives you the Document
        var name = Path.GetFileName(filePath);
        var sourceText = SourceText.From(content);

        // AddDocument returns the NEW document. We grab its ID.
        var doc = _workspace.AddDocument(_projectId, name, sourceText);
        _workspace.TryApplyChanges(_workspace.CurrentSolution.WithDocumentFilePath(doc.Id, fullPath));

        return doc.Id;
    }

    static IEnumerable<MetadataReference> GetDefaultReferences()
    {
        var trustedPlatformAssemblies =
            AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new Exception("Cannot get runtime assemblies");

        return trustedPlatformAssemblies
            .Split(Path.PathSeparator)
            .Where(p =>
                p.EndsWith("System.Runtime.dll", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith("System.Console.dll", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith("System.Linq.dll", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith("System.Collections.dll", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith("netstandard.dll", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("System.") ||
                p.Contains("Microsoft.")
            )
            .Select(p => MetadataReference.CreateFromFile(p));
    }

    private async Task<Compilation?> GetCurrentCompilation()
    {
        var project = _workspace.CurrentSolution.GetProject(_projectId);
        if (project != null)
        {
            return await project.GetCompilationAsync();
        }
        return null;
    }

    private static void PrintMemory(string label)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long managed = GC.GetTotalMemory(true);
        long privateMem = Process.GetCurrentProcess().PrivateMemorySize64;

        Console.WriteLine($"{label}");
        Console.WriteLine($"  Managed heap: {managed / 1024 / 1024} MB");
        Console.WriteLine($"  Private mem : {privateMem / 1024 / 1024} MB");
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.T && e.KeyModifiers == KeyModifiers.Control)
        {
            ShowSearchDialog();
            e.Handled = true;
        }
        /*else if (e.Key == Key.Escape && _searchWidget.IsVisible)
        {
            HideSearch();
            e.Handled = true;
        }*/
    }

    public async void JumpToSymbol(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault();

        if (location != null && location.IsInSource)
        {
            // Local file - open normally
            var filePath = location.SourceTree?.FilePath;
            if (!string.IsNullOrEmpty(filePath))
            {
                NavigateToSymbol(symbol);
            }
        }
        else
        {
            // It's a Built-in type or Library - decompile!
            await OpenMetadataSymbol(symbol);
        }
    }

    private async Task OpenMetadataSymbol(ISymbol symbol)
    {
        // 1. Check if we've already opened this metadata file to avoid duplicates
        var tabTitle = $"[Metadata] {symbol.Name}";
        foreach (var tab in _tabs.Items)
        {
            if (tab is not TabItem tabItem) continue;
            if (tabItem.Header?.ToString() != tabTitle) continue;
            _tabs.SelectedItem = tab;
            return;
        }

        // 2. Use Roslyn to "decompile" the symbol into C# code
        // This generates the class structure, properties, and method signatures
        var project = _workspace.CurrentSolution.Projects.First();
        var compilation = await project.GetCompilationAsync();

        // We use a NullWriter or similar if we just want the string, 
        // but the easiest way is using a SyntaxGenerator
        var sourceText = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        // Note: For full "Go to Definition" style source generation, 
        // most IDEs use the 'MetadataAsSourceService'. 
        // Here is a simplified version that gets the "Header" info:
        string decompiledCode = $@"#region Assembly {symbol.ContainingAssembly?.Name}
// {symbol.ContainingAssembly?.Locations}
#endregion

{symbol.ToDisplayString(new SymbolDisplayFormat(
    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
    parameterOptions: SymbolDisplayParameterOptions.IncludeName | SymbolDisplayParameterOptions.IncludeType
))}";

        // 3. Open in a new READ-ONLY tab
        AddMetadataTab(tabTitle, decompiledCode);
    }

    private void AddMetadataTab(string title, string content)
    {
        var editor = new TextEditor
        {
            Text = content,
            IsReadOnly = true, // Critical: don't let them "edit" System.String!
            Background = new SolidColorBrush(Color.FromRgb(20, 25, 30)), // Slightly bluer background
            Foreground = Brushes.Gray, // Dimmer text to indicate read-only
            FontFamily = new FontFamily("Cascadia Code,Consolas,Monospace"),
            FontSize = 13,
            ShowLineNumbers = true
        };

        var newTab = new TabItem
        {
            Header = title,
            Content = editor,
            Foreground = Brushes.LightSkyBlue // Distinguish it in the tab bar
        };

        _tabs.Items.Add(newTab);
        _tabs.SelectedItem = newTab;
    }

    private async void ShowSearchDialog()
    {
        var symbol = await DialogHelper.ShowSymbolSearchDialog(this, _workspace,
            location => OpenFileAtLine(location.FilePath, location.Line, location.Column));
        if (symbol != null)
        {
            // Navigate to the symbol
            NavigateToSymbol(symbol);
        }
    }

    // Add keyboard shortcut for search (Ctrl+Shift+F)
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ShowSearchDialog();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Close any open overlays
            /*if (_searchWidget.IsVisible)
            {
                _searchWidget.IsVisible = false;
            }*/
        }
    }

    // Add a method to create a better layout with splitter between editors and output
    private static void ConfigureTabStrip(TabControl tabControl)
    {
        tabControl.ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel
        {
            Orientation = Orientation.Horizontal
        });
        tabControl.Styles.Add(new Style(selector => selector.OfType<TabItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 3)),
                new Setter(TemplatedControl.FontSizeProperty, 12.0)
            }
        });
    }

    private Grid CreateMainLayoutWithOutputSplitter()
    {
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*, 6, 250"), // Editor, splitter, output
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(300, GridUnitType.Pixel)) { MinWidth = 100 },
                new ColumnDefinition(new GridLength(4, GridUnitType.Pixel)), // Left splitter
                new ColumnDefinition(new GridLength(1, GridUnitType.Star))   // Editor column
            }
        };
        /*
		// Left panel (Files, Structure, Hierarchy, Inspector, Environment, AST Viewer)
		_sideTabs = new TabControl
		{
			Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
			TabStripPlacement = Dock.Top,
			HorizontalContentAlignment = HorizontalAlignment.Stretch,
			VerticalContentAlignment = VerticalAlignment.Stretch,

			Items =
			{
				new TabItem { Header = "📁 Files", Content = _filesTreeView },
				new TabItem { Header = "📐 Structure", Content = _structureTree },
                new TabItem { Header = "🌲 Hierarchy", Content = _hierarchyTree },
                new TabItem { Header = "🔍 Inspector", Content = _inspector },
				new TabItem { Header = "⚙️ Environment", Content = _env },
				new TabItem { Header = "🌳 AST", Content = _astViewer }
			}
		};
		*/

        var views = new Dictionary<string, Control>
        {
            { "📁 Files", _filesTreeView },
            { "📐 Structure", _structureTree },
            { "🌲 Hierarchy", _hierarchyTree },
            { "🔍 Inspector", _inspector },
            { "⚙️ Environment", _env },
            { "🌳 AST", _astViewer }
        };

        var selector = new ComboBox
        {
            ItemsSource = views.Keys.ToList(),
            SelectedIndex = 0,
            Margin = new Thickness(2)
        };

        var content = new ContentControl
        {
            Content = views.Values.First(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        selector.SelectionChanged += (_, _) =>
        {
            if (selector.SelectedItem is string key)
                content.Content = views[key];
        };

        _sideTabs = new DockPanel();

        DockPanel.SetDock(selector, Dock.Top);
        _sideTabs.Children.Add(selector);
        _sideTabs.Children.Add(content);






        // Left splitter
        var leftSplitter = new GridSplitter
        {
            Width = 4,
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };

        // Editor area
        /*_tabs = new TabControl
		{
			HorizontalAlignment = HorizontalAlignment.Stretch,
			VerticalAlignment = VerticalAlignment.Stretch,
			Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
			Foreground = Brushes.White,
			TabStripPlacement = Dock.Top
		};*/

        // Bottom splitter (between editors and output)
        var bottomSplitter = new GridSplitter
        {
            Height = 2,
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        // Output area with tabs
        _outputTabs = new TabControl
        {
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
            TabStripPlacement = Dock.Top
        };
        ConfigureTabStrip(_outputTabs);

        _nuGetPackageManager = new NuGetPackageManager(_notificationManager, _baseDir);
        _terminal = new TerminalWidget(_baseDir);
        _nuGetPackageManager.OutputAppended += (_, message) => _terminal.AppendOutput(message + Environment.NewLine);

        _outputTabs.Items.Add(new TabItem { Header = "⚠️ Diagnostics", Content = _diagnosticsPanel });
        _outputTabs.Items.Add(new TabItem { Header = "👨‍💻 Terminal", Content = _terminal });
        _nuGetTab = new TabItem { Header = "📦 NuGet", Content = _nuGetPackageManager };
        _outputTabs.Items.Add(_nuGetTab);

        _mainGrid = mainGrid;
        _outputSplitter = bottomSplitter;
        _outputDock = new Grid { RowDefinitions = new RowDefinitions("Auto, *") };
        var closeOutput = new Button
        {
            Content = "\u00d7",
            Width = 28,
            Height = 24,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        ToolTip.SetTip(closeOutput, "Close output window");
        closeOutput.Click += (_, _) => SetOutputVisible(false);
        _outputDock.Children.Add(closeOutput);
        Grid.SetRow(_outputTabs, 1);
        _outputDock.Children.Add(_outputTabs);

        // Position everything in the grid
        Grid.SetColumn(_sideTabs, 0);
        Grid.SetRow(_sideTabs, 0);

        Grid.SetColumn(leftSplitter, 1);
        Grid.SetRow(leftSplitter, 0);

        Grid.SetColumn(_tabs, 2);
        Grid.SetRow(_tabs, 0);

        Grid.SetColumn(bottomSplitter, 0);
        Grid.SetRow(bottomSplitter, 1);
        Grid.SetColumnSpan(bottomSplitter, 3);

        Grid.SetColumn(_outputDock, 0);
        Grid.SetRow(_outputDock, 2);
        Grid.SetColumnSpan(_outputDock, 3);

        mainGrid.Children.Add(_sideTabs);
        mainGrid.Children.Add(leftSplitter);
        mainGrid.Children.Add(_tabs);
        mainGrid.Children.Add(bottomSplitter);
        mainGrid.Children.Add(_outputDock);

        return mainGrid;
    }

    private void SetOutputVisible(bool visible)
    {
        if (!visible && _outputDock.IsVisible)
            _outputHeight = new GridLength(_mainGrid.RowDefinitions[2].ActualHeight);
        _outputDock.IsVisible = visible;
        _outputSplitter.IsVisible = visible;
        _mainGrid.RowDefinitions[1].Height = new GridLength(visible ? 6 : 0);
        _mainGrid.RowDefinitions[2].Height = visible ? _outputHeight : new GridLength(0);
        _menu.SetOutputVisible(visible);
    }

    // Update your MainWindow constructor to use the new layout
    public MainWindow()
    {
        var root = new HotReloadRoot(BuildUI);
        Content = root;
        HotReloadService.Register(root);
        _tabs.SelectionChanged += OnEditorTabSelectionChanged;
        _tabs.GotFocus += OnEditorGotFocus;
        SettingsData.Instance.SettingsChanged += OnStatusSettingsChanged;
        Closed += (_, _) =>
        {
            SetStatusEditor(null);
            SettingsData.Instance.SettingsChanged -= OnStatusSettingsChanged;
        };
        UpdateActiveStatusEditor();
    }

    private Control CreateStatusBar()
    {
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto, Auto"),
            Margin = new Thickness(10, 0),
            ColumnSpacing = 12
        };
        var fields = new[] { _statusFile, _statusDiagnostics, _statusPosition, _statusMode };
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            field.FontSize = 11;
            field.Foreground = Brushes.LightGray;
            field.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(field, index);
            content.Children.Add(field);
        }
        var bar = new Border
        {
            Height = 28,
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = content
        };
        bar.SizeChanged += (_, _) =>
        {
            _statusMode.IsVisible = bar.Bounds.Width >= 760;
            _statusDiagnostics.IsVisible = bar.Bounds.Width >= 560;
        };
        UpdateStatusBar();
        return bar;
    }

    private void OnEditorTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, _tabs)) UpdateActiveStatusEditor();
    }

    private void OnEditorGotFocus(object? sender, RoutedEventArgs e)
    {
        var control = e.Source as Control;
        var editor = control as TextEditor ?? control?.GetVisualAncestors().OfType<TextEditor>().FirstOrDefault();
        if (editor != null) SetStatusEditor(editor);
    }

    private void UpdateActiveStatusEditor()
    {
        var tab = _tabs.SelectedItem as TabItem;
        SetStatusEditor(tab?.Content as TextEditor ?? FindEditorInContent(tab?.Content)?._editor);
    }

    private void SetStatusEditor(TextEditor? editor)
    {
        if (_statusEditor != null)
        {
            _statusEditor.TextArea.Caret.PositionChanged -= OnStatusEditorChanged;
            _statusEditor.TextArea.SelectionChanged -= OnStatusEditorChanged;
            _statusEditor.TextChanged -= OnStatusEditorChanged;
        }
        _statusEditor = editor;
        if (_statusEditor != null)
        {
            _statusEditor.TextArea.Caret.PositionChanged += OnStatusEditorChanged;
            _statusEditor.TextArea.SelectionChanged += OnStatusEditorChanged;
            _statusEditor.TextChanged += OnStatusEditorChanged;
        }
        UpdateStatusBar();
    }

    private void OnStatusEditorChanged(object? sender, EventArgs e) => UpdateStatusBar();

    private void OnStatusSettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(UpdateStatusBar);

    private void UpdateStatusBar()
    {
        var diagnostics = _documentDiagnostics.Values.SelectMany(items => items).ToList();
        _statusDiagnostics.Text = $"{diagnostics.Count(item => item.Severity == DiagnosticSeverity.Error)} errors  {diagnostics.Count(item => item.Severity == DiagnosticSeverity.Warning)} warnings";
        if (_statusEditor == null)
        {
            _statusFile.Text = "Ready";
            ToolTip.SetTip(_statusFile, null);
            _statusPosition.Text = "";
            _statusMode.Text = "";
            return;
        }

        var owner = _statusEditor.GetVisualAncestors().OfType<CodeEditor>().FirstOrDefault();
        var tab = _tabs.SelectedItem as TabItem;
        var codeEditor = owner ?? FindEditorInContent(tab?.Content);
        _statusFile.Text = codeEditor == null ? tab?.Header?.ToString() ?? "Editor" : Path.GetFileName(codeEditor.FilePath);
        ToolTip.SetTip(_statusFile, codeEditor?.FilePath);
        var caret = _statusEditor.TextArea.Caret;
        _statusPosition.Text = $"Ln {caret.Line}, Col {caret.Column}";
        if (_statusEditor.SelectionLength > 0)
            _statusPosition.Text += $" ({_statusEditor.SelectionLength} selected)";
        var indentation = _statusEditor.Options.ConvertTabsToSpaces ? "Spaces" : "Tab Size";
        _statusMode.Text = _statusEditor.IsReadOnly ? "Read-only" : $"{indentation}: {_statusEditor.Options.IndentationSize}";
    }

    private Control BuildUI()
    {
        Title = "CAAI IDE";
        Width = 1200;
        Height = 800;
        ConfigureTabStrip(_tabs);

        InitializeRoslyn();
        SetupLiveDiagnostics();

        _runner = new DotnetRunner(_terminal);

        // Setup toolbar events
        _toolbar.OnRunRequested += () => _runner.Run(_baseDir);
        _toolbar.OnStopRequested += () => _runner.Stop();
        _toolbar.OnDebugRequested += () => _runner.Run(_baseDir, "Debug");
        _toolbar.OnBuildRequested += () => _runner.Build(_baseDir);
        _toolbar.OnSearchRequested += ShowSearchDialog; // Add search button to toolbar

        var rootNode = new FileSystemNode(_baseDir, true);
        var rootLayout = new DockPanel();

        DockPanel.SetDock(_menu, Dock.Top);
        rootLayout.Children.Add(_menu);
        SetupMenuActions();

        DockPanel.SetDock(_toolbar, Dock.Top);
        rootLayout.Children.Add(_toolbar);

        // Create class hierarchy
        _hierarchyTree = new TreeView
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            Foreground = Brushes.White,
            ItemTemplate = new FuncTreeDataTemplate<HierarchyNode>(
                _ => true,
                (node, _) => new TextBlock { Text = $"○ {node.Name}", Margin = new Thickness(2) },
                node => node.DerivedTypes
            ),
            ItemsSource = BuildClassHierarchy()
        };

        // Setup keyboard handling
        this.KeyDown += Window_KeyDown;

        // Open initial file
        //_structureTree._treeView.ItemsSource = OpenFile("/home/adam/Desktop/HIVE/Program.cs");
        currentSyntaxTree = OpenFile(_baseDir + "\\Program.cs");

        SetupEnvironment();
        SetupStructureTree();
        SetupAstViewer();
        SetupFileTreeView();

        WireEvents(_baseDir);


        var mainGrid = CreateMainLayoutWithOutputSplitter();
        var statusBar = CreateStatusBar();
        DockPanel.SetDock(statusBar, Dock.Bottom);
        rootLayout.Children.Add(statusBar);
        // Add overlay for modal dialogs
        rootLayout.Children.Add(mainGrid);
        return rootLayout;
    }

    private void SetupEnvironment()
    {
        _env = new EnvironmentWidget(_workspace);
    }

    private void SetupAstViewer()
    {
        _astViewer.SyntaxTree = currentSyntaxTree?.GetRoot();
        _astViewer.ExpandAll();
        _astViewer.NodeSelected += (sender, args) =>
        {
            if (args.Node != null && args.Location != null)
            {
                // Navigate to the node's location in your editor
                NavigateToLocation(args.Location);
            }
        };
    }

    private void NavigateToLocation(Location location)
    {
        var lineSpan = location.GetLineSpan();
        var line = lineSpan.StartLinePosition.Line;
        OpenFileAtLine(location.SourceTree?.FilePath, line);
    }

    private void SetupMenuActions()
    {
        // === File Menu ===
        _menu.OnNewNotebook += AddNotebookTab;
        _menu.OnNewProject += () => { /* TODO: Implement new project */ };
        _menu.OnOpenFile += async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open File",
                AllowMultiple = true,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new("C# Files") { Patterns = new[] { "*.cs" } },
                    new("All Files") { Patterns = new[] { "*" } }
                }
            });
            foreach (var file in files)
            {
                OpenFile(file.Path.LocalPath);
            }
        };
        _menu.OnOpenProject += async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open Project Folder",
                AllowMultiple = false
            });
            if (folders.Count > 0)
            {
                await LoadProjectFromFolder(folders[0].Path.LocalPath);
            }
        };
        _menu.OnOpenRecentRequested += path =>
        {
            if (File.Exists(path))
            {
                OpenFile(path);
            }
            else
            {
                // Show error or remove from recent
                var toast = new Notification(
                    title: "Warning",
                    message: $"[Warning] Recent file not found: {path}",
                    type: NotificationType.Warning,
                    expiration: TimeSpan.FromSeconds(5),
                    onClick: () => Console.WriteLine("User clicked the notification!"),
                    onClose: () => Console.WriteLine("Notification dismissed.")
                );

                // Send to the manager screen container
                _notificationManager.Show(toast);
            }
        };
        _menu.OnClearRecentRequested += () =>
        {
            // Clear recent files list
            // TODO: Implement persistent recent files clearing
        };
        _menu.OnSaveFile += async () =>
        {
            if (_tabs.SelectedItem is TabItem tab)
            {
                if (tab.Content is NotebookWidget notebook)
                {
                    await notebook.SaveNotebookAsync();
                    return;
                }
                var editor = FindEditorInContent(tab.Content);
                if (editor != null && !string.IsNullOrEmpty(editor.FilePath))
                {
                    File.WriteAllText(editor.FilePath, editor._editor.Text);
                    var toast = new Notification(
                    title: "Info",
                    message: $"[Saved] {Path.GetFileName(editor.FilePath)}",
                    type: NotificationType.Information,
                    expiration: TimeSpan.FromSeconds(5),
                    onClick: () => Console.WriteLine("User clicked the notification!"),
                    onClose: () => Console.WriteLine("Notification dismissed.")
                );

                    // Send to the manager screen container
                    _notificationManager.Show(toast);
                }
            }
        };
        _menu.OnSaveAll += async () =>
        {
            var allSaved = true;
            foreach (TabItem? tab in _tabs.Items)
            {
                if (tab is null)
                    continue;

                if (tab.Content is NotebookWidget notebook)
                {
                    if (!await notebook.SaveNotebookAsync())
                        allSaved = false;
                    continue;
                }

                var editor = FindEditorInContent(tab.Content);
                if (editor != null && !string.IsNullOrEmpty(editor.FilePath))
                {
                    File.WriteAllText(editor.FilePath, editor._editor.Text);
                }
            }
            if (!allSaved) return;
            // Show error or remove from recent
            var toast = new Notification(
                title: "Info",
                message: "[Saved] All files saved",
                type: NotificationType.Information,
                expiration: TimeSpan.FromSeconds(5),
                onClick: () => Console.WriteLine("User clicked the notification!"),
                onClose: () => Console.WriteLine("Notification dismissed.")
            );

            // Send to the manager screen container
            _notificationManager.Show(toast);
        };
        _menu.OnExit += Close;

        // === Edit Menu ===
        _menu.OnUndo += () =>
        {
            if (_tabs.SelectedItem is TabItem tab)
            {
                var editor = FindEditorInContent(tab.Content);
                editor?._editor.Undo();
            }
        };
        _menu.OnRedo += () =>
        {
            if (_tabs.SelectedItem is TabItem tab)
            {
                var editor = FindEditorInContent(tab.Content);
                editor?._editor.Redo();
            }
        };
        _menu.OnCut += () =>
        {
            if (_tabs.SelectedItem is TabItem tab)
            {
                var editor = FindEditorInContent(tab.Content);
                editor?._editor.Cut();
            }
        };
        _menu.OnCopy += () =>
        {
            if (_tabs.SelectedItem is TabItem tab)
            {
                var editor = FindEditorInContent(tab.Content);
                editor?._editor.Copy();
            }
        };
        _menu.OnPaste += () =>
        {
            if (_tabs.SelectedItem is TabItem tab)
            {
                var editor = FindEditorInContent(tab.Content);
                editor?._editor.Paste();
            }
        };
        _menu.OnToggleComment += () =>
        {
            if (_tabs.SelectedItem is TabItem tab)
            {
                var editor = FindEditorInContent(tab.Content);
                if (editor != null)
                {
                    ToggleLineComment(editor._editor);
                }
            }
        };
        _menu.OnFormatDocument += () =>
        {
            if (_tabs.SelectedItem is TabItem tab)
            {
                var editor = FindEditorInContent(tab.Content);
                if (editor != null)
                {
                    FormatDocument(editor._editor);
                }
            }
        };

        // === View Menu ===
        _menu.OnToggleOutput += SetOutputVisible;
        _menu.OnToggleExplorer += (isChecked) =>
        {
            if (_filesTreeView?.Parent is Control filesPanel)
            {
                filesPanel.IsVisible = isChecked;
            }
        };
        _menu.OnToggleStructure += (isChecked) =>
        {
            if (_structureTree?.Parent is Control structurePanel)
            {
                structurePanel.IsVisible = isChecked;
            }
        };
        _menu.OnToggleWordWrap += (isChecked) =>
        {
            if (_tabs.SelectedItem is TabItem tab)
            {
                var editor = FindEditorInContent(tab.Content);
                if (editor != null)
                {
                    editor._editor.WordWrap = isChecked;
                }
            }
        };

        // === Run/Debug Menu ===
        _menu.OnRunProject += () => _runner.Run(_baseDir);
        _menu.OnRunNoDebug += () => _runner.Run(_baseDir, "Release");
        _menu.OnStepInto += () =>
        {
            // TODO: Implement debugger step-into

        };
        _menu.OnStepOver += () =>
        {
            // TODO: Implement debugger step-over
        };
        _menu.OnToggleBreakpoint += () =>
        {
            if (_tabs.SelectedItem is TabItem tab)
            {
                var editor = FindEditorInContent(tab.Content);
                if (editor != null)
                {
                    var line = editor._editor.Document.GetLineByOffset(editor._editor.CaretOffset).LineNumber;
                    // TODO: Toggle breakpoint at line
                    var toast = new Notification(
                        title: "Info",
                        message: $"[Debug] Breakpoint toggled at line {line}",
                        type: NotificationType.Information,
                        expiration: TimeSpan.FromSeconds(5),
                        onClick: () => Console.WriteLine("User clicked the notification!"),
                        onClose: () => Console.WriteLine("Notification dismissed.")
                    );

                    // Send to the manager screen container
                    _notificationManager.Show(toast);
                }
            }
        };

        // === Tools/Help Menu ===
        _menu.OnOpenNuGetManager += () =>
        {
            SetOutputVisible(true);
            _outputTabs.SelectedItem = _nuGetTab;
        };
        _menu.OnOpenSettings += async () => await ShowSettings();

        _menu.OnOpenAbout += () =>
        {
            var aboutDialog = new Window
            {
                Title = "About CAAI IDE",
                Width = 400,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(35, 35, 40))
            };

            var content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            content.Children.Add(new TextBlock
            {
                Text = "CAAI IDE",
                FontSize = 24,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            content.Children.Add(new TextBlock
            {
                Text = "C# Advanced AI-powered IDE",
                FontSize = 14,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            content.Children.Add(new TextBlock
            {
                Text = "Version 1.0.0",
                FontSize = 12,
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var closeBtn = new Button
            {
                Content = "Close",
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 100,
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
                Foreground = Brushes.White
            };
            closeBtn.Click += (_, _) => aboutDialog.Close();
            content.Children.Add(closeBtn);

            aboutDialog.Content = content;
            aboutDialog.ShowDialog(this);
        };
    }

    private async Task ShowSettings()
    {
        var dialog = new SettingsDialog(SettingsData.Instance, App.SettingsPath);
        await dialog.ShowDialog(this);
    }

    // Helper methods for menu actions
    private void ToggleLineComment(TextEditor editor)
    {
        var document = editor.Document;
        var line = document.GetLineByOffset(editor.CaretOffset);
        var text = document.GetText(line.Offset, line.Length);

        if (text.TrimStart().StartsWith("//"))
        {
            // Uncomment
            var newText = text.Replace("//", "").TrimStart();
            document.Replace(line.Offset, line.Length, newText);
        }
        else
        {
            // Comment
            var indentation = text.Length - text.TrimStart().Length;
            var spaces = new string(' ', indentation);
            document.Replace(line.Offset, line.Length, spaces + "// " + text.TrimStart());
        }
    }

    private void FormatDocument(TextEditor editor)
    {
        // Basic formatting - you could integrate Roslyn formatting here
        var syntaxTree = CSharpSyntaxTree.ParseText(editor.Text);
        var root = syntaxTree.GetRoot();
        var formatted = root.NormalizeWhitespace().ToFullString();
        editor.Text = formatted;
    }

    // Add a method to navigate to symbol
    private void NavigateToSymbol(ISymbol symbol)
    {
        // Implement navigation to symbol location
        foreach (var location in symbol.Locations)
        {
            if (location.IsInSource)
            {
                var filePath = location.SourceTree?.FilePath;
                var lineSpan = location.GetLineSpan();
                var line = lineSpan.StartLinePosition.Line;

                // Open the file and navigate to the line
                OpenFileAtLine(filePath, line, lineSpan.StartLinePosition.Character);
                break;
            }
        }
    }

    private void OpenFileAtLine(string? filePath, int line, int? column = null)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        OpenFile(filePath);
        if (_tabs.SelectedItem is TabItem tab && FindEditorInContent(tab.Content) is CodeEditor editor &&
            string.Equals(editor.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(_tabs.SelectedItem, tab)) return;
                var lineNumber = Math.Clamp(line + 1, 1, editor._editor.Document.LineCount);
                var lineSegment = editor._editor.Document.GetLineByNumber(lineNumber);
                editor._editor.Select(lineSegment.Offset + Math.Clamp(column ?? 0, 0, lineSegment.Length), 0);
                editor.FocusEditor();
                editor._editor.ScrollTo(lineNumber, Math.Clamp(column ?? 0, 0, lineSegment.Length) + 1);
            }, DispatcherPriority.Loaded);
        }
    }

    private void SetupStructureTree()
    {
        //_structureTree.SelectionChanged += (_, _) =>
        _structureTree.NodeSelected += (_, _) =>
        {
            if (_structureTree.TreeView.SelectedItem is not StructureNode node) return;
            // 1. Get the currently active tab
            if (_tabs.SelectedItem is not TabItem activeTab) return;
            // 2. Find the editor within that tab
            // This assumes you stored your editor in the Content property 
            // or as a variable you can reach.
            var activeEditor = FindEditorInContent(activeTab.Content);

            if (activeEditor == null) return;
            // 3. Perform the navigation
            activeEditor._editor.Select(node.Span.Start, node.Span.Length);

            var line = activeEditor._editor.Document.GetLineByOffset(node.Span.Start).LineNumber;
            activeEditor._editor.ScrollToLine(line);

            // Optional: Give the editor focus so the user can start typing
            activeEditor.Focus();
        };

        if (currentSyntaxTree != null)
        {
            _structureTree.SetStructure(currentSyntaxTree, "/home/adam/Desktop/HIVE/Program.cs");
        }
    }

    private void SetupFileTreeView()
    {
        _filesTreeView = new FileExplorerWidget(_baseDir);

        _filesTreeView.OnFileOpened += (path) =>
        {
            currentSyntaxTree = OpenFile(path);
            if (currentSyntaxTree != null)
            {
                //_structureTree._treeView.ItemsSource = currentSyntaxTree;
                _structureTree.SetStructure(currentSyntaxTree, path);
            }
        };

        _filesTreeView.OnFileDeleted += (path) =>
        {
            // if it is in tabs, remove it
            foreach (TabItem? t in _tabs.Items)
            {
                if (t?.Header?.ToString() == path)
                {
                    _tabs.Items.Remove(t);
                    break;
                }
            }
        };

        _filesTreeView.OnFileRenamed += (oldPath, newPath) =>
        {
            // if it is in tabs, rename it
            foreach (TabItem? t in _tabs.Items)
            {
                if (t?.Header?.ToString() == oldPath)
                {
                    t.Header = newPath;
                    break;
                }
            }
        };

        _filesTreeView.OnFileCreated += (path) =>
        {
            OpenFile(path);
        };
    }

    private void WireEvents(string baseDir)
    {
        // 5. Global Keyboard Shortcuts
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

        _diagnosticsPanel.OnDiagnosticClicked += (diag) =>
        {
            OpenFileAtLine(diag.FilePath, diag.Line - 1, diag.Column - 1);
        };
    }

    private CodeEditor? FindEditorInContent(object? content)
    {
        // Case A: The content IS the editor
        if (content is CodeEditor editor) return editor;
        if (content is Panel panel)
            return panel.Children.Select(FindEditorInContent).FirstOrDefault(child => child is not null);
        if (content is ContentControl contentControl)
            return FindEditorInContent(contentControl.Content);

        // Case B: The content is a container (like a UserControl or Panel)
        if (content is Control control)
            return control.GetVisualDescendants().OfType<CodeEditor>().FirstOrDefault();

        // Case C: If you use a custom NotebookWidget, you'd check for that here
        return null;
    }

    private void AddNotebookTab()
    {
        var notebook = new NotebookWidget(_inspector);

        var tab = new TabItem();

        // Header with text + close button
        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5
        };

        var headerText = new TextBlock
        {
            Text = "Notebook",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeButton = new Button
        {
            Content = "×",
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.Bold
        };

        closeButton.Click += (_, _) =>
        {
            _tabs.Items.Remove(tab);
            notebook.Dispose();
        };

        headerPanel.Children.Add(headerText);
        headerPanel.Children.Add(closeButton);

        tab.Header = headerPanel;
        tab.Content = notebook;

        _tabs.Items.Add(tab);
        _tabs.SelectedItem = tab; // Switch to the new notebook
    }

    private List<HierarchyNode> BuildClassHierarchy()
    {
        var hierarchy = new List<HierarchyNode>();

        // Get all named types (classes, interfaces, etc.) from the assembly being compiled
        //var allTypes = GetCurrentCompilation().Result?.GlobalNamespace
        var allTypes = GetCurrentCompilation().Result?.Assembly.GlobalNamespace
            .GetAllTypes()
            .Where(t => t.TypeKind == TypeKind.Class)
            .ToList();

        // Find "Root" classes (those whose base class is Object or not in our assembly)
        if (allTypes == null || allTypes?.Count == 0) return hierarchy;
        {
            var rootTypes = allTypes?.Where(t =>
                t.BaseType == null ||
                t.BaseType.SpecialType == SpecialType.System_Object ||
                !allTypes.Contains(t.BaseType, SymbolEqualityComparer.Default));
            if (rootTypes == null) return hierarchy;
            foreach (var rootType in rootTypes)
            {
                hierarchy.Add(BuildNodeRecursively(rootType, allTypes ?? []));
            }
        }

        return hierarchy;
    }

    private HierarchyNode BuildNodeRecursively(INamedTypeSymbol symbol, List<INamedTypeSymbol> allTypes)
    {
        var node = new HierarchyNode
        {
            Name = symbol.Name,
            FullName = symbol.ToDisplayString()
        };

        // Find types that name this symbol as their BaseType
        var derived = allTypes.Where(t => SymbolEqualityComparer.Default.Equals(t.BaseType, symbol));

        foreach (var child in derived)
        {
            node.DerivedTypes.Add(BuildNodeRecursively(child, allTypes));
        }

        return node;
    }

    public async Task<List<Diagnostic>> GetDiagnosticsForDocument(DocumentId docId)
    {
        var document = _workspace.CurrentSolution.GetDocument(docId);
        if (document == null) return new List<Diagnostic>();

        // 1. Get the Semantic Model (this triggers the 'real' analysis)
        var semanticModel = await document.GetSemanticModelAsync();

        // 2. Get both Syntactic (grammar) and Semantic (logic/type) errors
        if (semanticModel != null)
        {
            var diagnostics = semanticModel.GetDiagnostics();

            // 3. Update the UI
            foreach (var diag in diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning))
            {
                string location = diag.Location.GetLineSpan().StartLinePosition.ToString();
                //_outputPanel.AppendOutput($"[{diag.Severity}] Line {location}: {diag.GetMessage()}");
            }

            return diagnostics.ToList();
        }
        else
        {
            return [];
        }
    }

    private void HighlightDiagnostics(CodeEditor editor, IEnumerable<Diagnostic> diagnostics)
    {
        editor.SetDiagnostics(diagnostics);
    }

    private static DiagnosticItem Convert(Microsoft.CodeAnalysis.Diagnostic d)
    {
        var span = d.Location.GetLineSpan();

        return new DiagnosticItem
        {
            Code = d.Id,
            Message = d.GetMessage(),
            FilePath = d.Location.IsInSource ? span.Path ?? "" : "",
            Line = d.Location.IsInSource ? span.StartLinePosition.Line + 1 : 0,
            Column = d.Location.IsInSource ? span.StartLinePosition.Character + 1 : 0,
            Severity = d.Severity switch
            {
                DiagnosticSeverity.Error => DiagnosticSeverityType.Error,
                DiagnosticSeverity.Warning => DiagnosticSeverityType.Warning,
                _ => DiagnosticSeverityType.Info
            }
        };
    }

    private void SetupLiveDiagnostics()
    {
        _workspace.RegisterWorkspaceChangedHandler(change =>
        {
            if (change.DocumentId is not { } documentId) return;
            if (change.Kind is WorkspaceChangeKind.DocumentChanged or WorkspaceChangeKind.DocumentAdded)
                Dispatcher.UIThread.Post(() => _ = RefreshDiagnosticsAsync(documentId));
            else if (change.Kind == WorkspaceChangeKind.DocumentRemoved)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_diagnosticRequests.Remove(documentId, out var request))
                        request.Cancel();
                    _documentDiagnostics.Remove(documentId);
                    _diagnosticsPanel.SetDiagnostics(_documentDiagnostics.Values.SelectMany(items => items).Select(Convert));
                    UpdateStatusBar();
                });
            }
        });
    }

    private async Task RefreshDiagnosticsAsync(DocumentId documentId)
    {
        if (_diagnosticRequests.Remove(documentId, out var previousRequest))
            previousRequest.Cancel();
        using var request = new CancellationTokenSource();
        _diagnosticRequests[documentId] = request;
        var cancellationToken = request.Token;
        try
        {
            await Task.Delay(300, cancellationToken);
            var document = _workspace.CurrentSolution.GetDocument(documentId);
            if (document == null) return;
            var text = await document.GetTextAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (model == null) return;
            var diagnostics = await Task.Run(() => model.GetDiagnostics(cancellationToken: cancellationToken).ToList(), cancellationToken);
            var currentDocument = _workspace.CurrentSolution.GetDocument(documentId);
            if (currentDocument == null || !text.ContentEquals(await currentDocument.GetTextAsync(cancellationToken))) return;
            cancellationToken.ThrowIfCancellationRequested();

            _documentDiagnostics[documentId] = diagnostics;
            _diagnosticsPanel.SetDiagnostics(_documentDiagnostics.Values.SelectMany(items => items).Select(Convert));
            UpdateStatusBar();
            foreach (var tab in _tabs.Items.OfType<TabItem>())
            {
                if (FindEditorInContent(tab.Content) is { } editor && editor.DocumentId == documentId)
                    HighlightDiagnostics(editor, diagnostics);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Diagnostic analysis failed: {exception}");
        }
        finally
        {
            if (_diagnosticRequests.TryGetValue(documentId, out var pending) && ReferenceEquals(pending, request))
                _diagnosticRequests.Remove(documentId);
        }
    }

    private SyntaxTree? OpenFile(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            foreach (var existingTab in _tabs.Items.OfType<TabItem>())
            {
                if (FindEditorInContent(existingTab.Content) is { } existingEditor &&
                    string.Equals(existingEditor.FilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _tabs.SelectedItem = existingTab;
                    return CSharpSyntaxTree.ParseText(existingEditor.GetText(), path: path);
                }
            }
            if (!File.Exists(path)) return null;
            return OpenExistingFile(path, File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _notificationManager.Show(new Notification("Cannot open file", exception.Message, NotificationType.Error));
            return null;
        }
    }

    private SyntaxTree? OpenExistingFile(string path, string src)
    {
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            var tree = CSharpSyntaxTree.ParseText(src, path: path);
            currentSyntaxTree = tree;
        }

        var docId = RegisterFileInWorkspace(path, src);

        var editor = new CodeEditor(path, _workspace, docId, src);
        // Subscribe to events
        editor.GoToDefinitionRequested += OnGoToDefinitionRequested;
        editor.FindReferencesRequested += OnFindReferencesRequested;
        editor.FormatDocumentRequested += OnFormatDocumentRequested;

        var tab = new TabItem();

        // Header with text + close button
        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5
        };

        var headerText = new TextBlock
        {
            Text = Path.GetFileName(path),
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };

        var closeButton = new Button
        {
            Content = "×",
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.Bold
        };

        closeButton.Click += (_, _) =>
        {
            _tabs.Items.Remove(tab);
        };

        headerPanel.Children.Add(headerText);
        headerPanel.Children.Add(closeButton);

        tab.Header = headerPanel;

        // Wrap editor in Grid to ensure styles propagate
        var container = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        container.Children.Add(editor);
        tab.Content = container;

        _tabs.Items.Add(tab);
        _tabs.SelectedItem = tab;

        _ = RefreshDiagnosticsAsync(docId);
        //return structureNodes;
        return currentSyntaxTree;
    }

    private void OnGoToDefinitionRequested(object? sender, GoToDefinitionEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.FilePath) && File.Exists(e.FilePath))
        {
            OpenFileAtLine(e.FilePath, e.Line, e.Column);
        }
    }

    private async void OnFindReferencesRequested(object? sender, FindReferencesEventArgs e)
    {
        var referencesWindow = await ReferencesWindow.CreateAsync(e.Symbol, e.References ?? new List<ReferenceLocation>());
        referencesWindow.NavigateRequested += (filePath, line, column) => OpenFileAtLine(filePath, line, column);
        referencesWindow.Show(this);
    }

    private void OnFormatDocumentRequested(object? sender, EventArgs e)
    {
        // Update the document in the workspace if needed
        // The formatting is already done in the editor
        var toast = new Notification(
            title: "Info",
            message: "Document formatted",
            type: NotificationType.Information,
            expiration: TimeSpan.FromSeconds(5),
            onClick: () => Console.WriteLine("User clicked the notification!"),
            onClose: () => Console.WriteLine("Notification dismissed.")
        );

        // Send to the manager screen container
        _notificationManager.Show(toast);
    }
}
