global using Symbol = FluentAvalonia.UI.Controls.FASymbol;
global using SymbolIcon = FluentAvalonia.UI.Controls.FASymbolIcon;
global using Avalonia.Input.Platform;

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
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
        Resources["ControlContentThemeFontSize"] = 14.0;
        Resources["ContentControlThemeFontFamily"] = FontFamily.Default;

        Window.KeyDownEvent.AddClassHandler<Window>((window, e) =>
        {
            if (e.Handled || e.Key != Key.Escape || window is MainWindow) return;
            e.Handled = true;
            window.Close();
        }, RoutingStrategies.Bubble, handledEventsToo: true);
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
        if (e.Key == Key.F5 && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
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
            WindowDecorations = WindowDecorations.Full,
            ShowInTaskbar = false,
            ExtendClientAreaToDecorationsHint = true,
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

public partial class MainWindow : Window
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
    private string _activeSideView = "Files";
    private CodeStructureWidget _structureTree = new();
    private FileExplorerWidget _filesTreeView = null!;
    private GitWidget _gitWidget = null!;
    private readonly ObjectInspectorWidget1 _inspector = new();
    // Inside MainWindow class
    private AdhocWorkspace _workspace = null!;
    private ProjectId _projectId = null!;
    private RunDebugToolbar _toolbar = new();
    private DiagnosticsPanel _diagnosticsPanel = new();
    private NuGetPackageManager _nuGetPackageManager = null!;
    private TabControl _outputTabs = null!;
    private Grid _mainGrid = null!;
    private Grid _outputDock = null!;
    private GridSplitter _outputSplitter = null!;
    private GridLength _outputHeight = new(250);
    private readonly Dictionary<DocumentId, List<Diagnostic>> _documentDiagnostics = new();
    private readonly Dictionary<DocumentId, CancellationTokenSource> _diagnosticRequests = new();
    private TextBlock _statusFile = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private TextBlock _statusGit = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private TextBlock _statusDiagnostics = new() { Width = 160, TextAlignment = TextAlignment.Right };
    private TextBlock _statusPosition = new() { Width = 220, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
    private TextBlock _statusMode = new() { Width = 140, TextAlignment = TextAlignment.Right };
    private TextEditor? _statusEditor;
    private TabItem _nuGetTab = null!;
    private TerminalWidget _terminal = null!;
    private MainMenuWidget _menu = new();
    private ClassHierarchyWidget _hierarchyTree = null!;
    private Compilation? _hierarchyCompilation;
    private int _hierarchyRefreshVersion;
    private string? _selectedHierarchyTypeName;
    private EnvironmentWidget _env = null!;
    private readonly EnvironmentStore _environmentStore = new();
    private ConnectionsWidget _connectionsWidget = null!;
    private Action<string>? _activateSideView;
    public EnvironmentStore EntityStore => _environmentStore;
    private AstViewer _astViewer = new();
    private DotnetRunner _runner;
    private readonly WindowNotificationManager _notificationManager = new WindowNotificationManager()
    {
        Position = NotificationPosition.TopRight,
        MaxItems = 3
    };


    SyntaxTree? currentSyntaxTree = null;

    private static string _baseDir = GetInitialProjectPath();
    private string _gitDirectory = _baseDir;
    public CodeWatcher? CodeWatcher = Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1"
        ? null
        : new CodeWatcher(_baseDir, file =>
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
        folderPath = Path.GetFullPath(folderPath);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Project folder does not exist: {folderPath}");
        if (_debugSession.IsActive)
            throw new InvalidOperationException("Stop the current debug session before opening another project.");
        if (_runner.IsRunning)
            throw new InvalidOperationException("Stop the running process before opening another project.");

        var openEditors = OpenCodeEditors().ToArray();
        if (openEditors.Any(editor => !File.Exists(editor.FilePath) || File.ReadAllText(editor.FilePath) != editor.GetText()))
            throw new InvalidOperationException("Save or close modified editor tabs before opening another project.");
        if (_tabs.Items.OfType<TabItem>().Select(tab => tab.Content).OfType<FilePreviewControl>().Any(preview => preview.IsDirty))
            throw new InvalidOperationException("Save or close modified preview tabs before opening another project.");

        var sources = GetProjectSourceFiles(folderPath)
            .AsParallel()
            .AsOrdered()
            .Select(file => (Path: Path.GetFullPath(file), Content: File.ReadAllText(file)))
            .ToArray();

        var solution = _workspace.CurrentSolution;
        var oldDocumentIds = solution.GetProject(_projectId)?.DocumentIds.ToArray() ?? Array.Empty<DocumentId>();
        foreach (var documentId in oldDocumentIds)
            solution = solution.RemoveDocument(documentId);

        foreach (var tab in _tabs.Items.OfType<TabItem>().Where(tab => FindEditorInContent(tab.Content) != null).ToArray())
            _tabs.Items.Remove(tab);

        foreach (var (filePath, source) in sources)
        {
            var documentId = DocumentId.CreateNewId(_projectId);
            solution = solution.AddDocument(documentId, Path.GetFileName(filePath), source)
                .WithDocumentFilePath(documentId, filePath);
        }

        if (!_workspace.TryApplyChanges(solution))
            throw new InvalidOperationException("The project files could not be loaded into the workspace.");

        _baseDir = folderPath;
        _gitDirectory = folderPath;
        _filesTreeView.SetRootPath(folderPath);
        _terminal.SetWorkingDirectory(folderPath);
        _nuGetPackageManager.SetProjectFolder(folderPath);
        _gitWidget.SetDirectory(folderPath);
        CodeWatcher?.ChangePath(folderPath);

        var debugProjects = Directory.GetFiles(folderPath, "*.csproj", SearchOption.TopDirectoryOnly);
        _debugProjectPath = debugProjects.Length == 1 ? Path.GetFullPath(debugProjects[0]) : null;
        _debugPanel.SetProject(_debugProjectPath ?? string.Empty);

        _hierarchyCompilation = null;
        _selectedHierarchyTypeName = null;
        currentSyntaxTree = null;
        _structureTree.SetStructure(CSharpSyntaxTree.ParseText(string.Empty));
        _astViewer.SyntaxTree = null;
        RememberProjectFolder(folderPath);
        _ = RefreshClassHierarchyAsync();
        UpdateStatusBar();
        ShowNotification("Project loaded", $"{Path.GetFileName(folderPath)} ({sources.Length} C# files)", NotificationType.Success);
    }

    private static string GetInitialProjectPath()
    {
        var settings = SettingsData.Instance;
        if (!string.IsNullOrWhiteSpace(settings.LastProjectPath) && Directory.Exists(settings.LastProjectPath))
            return Path.GetFullPath(settings.LastProjectPath);
        if (Directory.Exists(settings.DevelopmentDefaultProjectPath))
            return Path.GetFullPath(settings.DevelopmentDefaultProjectPath);
        return Environment.CurrentDirectory;
    }

    private static bool IsProjectSourceFile(string rootPath, string filePath)
    {
        var excludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".vs", "bin", "obj" };
        return !Path.GetRelativePath(rootPath, filePath)
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Any(excludedDirectories.Contains);
    }

    private static string[] GetProjectSourceFiles(string rootPath) =>
        Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
            .Where(file => IsProjectSourceFile(rootPath, file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void RememberProjectFolder(string folderPath)
    {
        var settings = SettingsData.Instance;
        var recent = settings.GetRecentProjectPaths()
            .Where(Directory.Exists)
            .Where(path => !string.Equals(path, folderPath, StringComparison.OrdinalIgnoreCase))
            .Prepend(folderPath)
            .ToArray();
        settings.LastProjectPath = folderPath;
        settings.SetRecentProjectPaths(recent);
        _menu.SetRecentProjects(settings.GetRecentProjectPaths());
        SaveApplicationSettings();
    }

    private void SaveApplicationSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(App.SettingsPath));
            if (directory != null) Directory.CreateDirectory(directory);
            SettingsData.Instance.Save(App.SettingsPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _notificationManager.Show(new Notification(
                title: "Settings not saved",
                message: exception.Message,
                type: NotificationType.Warning,
                expiration: TimeSpan.FromSeconds(5)));
        }
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

        var sources = GetProjectSourceFiles(_baseDir)
            .AsParallel()
            .AsOrdered()
            .Select(filePath =>
            {
                var fullPath = Path.GetFullPath(filePath);
                return (FullPath: fullPath, Content: File.ReadAllText(fullPath));
            })
            .ToArray();

        var sourceSolution = _workspace.CurrentSolution;
        foreach (var source in sources)
        {
            var documentId = DocumentId.CreateNewId(_projectId);
            sourceSolution = sourceSolution.AddDocument(documentId, Path.GetFileName(source.FullPath), SourceText.From(source.Content))
                .WithDocumentFilePath(documentId, source.FullPath);
        }
        sourceSolution = EditorAnalysisService.AddEditorConfigs(sourceSolution, _projectId, _baseDir);
        if (!_workspace.TryApplyChanges(sourceSolution))
            throw new InvalidOperationException("The default project sources could not be loaded into the workspace.");
    }

    public async Task PerformRename(ISymbol symbol, string newName)
    {
        var original = _workspace.CurrentSolution;
        var renamed = await Task.Run(() => EditorRefactoringService.RenameSymbolAsync(original, symbol, newName));
        await EditorRefactoringService.ApplyTextChangesAsync(_workspace, original, renamed);
        await RevealRefactoredDocumentsAsync(renamed.GetChanges(original).GetProjectChanges()
            .SelectMany(change => change.GetChangedDocuments()).ToArray());
    }

    private async Task RevealRefactoredDocumentsAsync(IReadOnlyList<DocumentId> documentIds)
    {
        var selectedTab = _tabs.SelectedItem;
        try
        {
            foreach (var id in documentIds)
            {
                var document = _workspace.CurrentSolution.GetDocument(id);
                if (document?.FilePath is not { } path || !File.Exists(path)) continue;
                var text = await document.GetTextAsync();
                var savedText = await File.ReadAllTextAsync(path);
                if (_tabs.Items.OfType<TabItem>().Any(tab => FindEditorInContent(tab.Content)?.DocumentId == id)) continue;
                OpenExistingFile(path, text.ToString(), savedText);
            }
        }
        catch (Exception exception)
        {
            ShowNotification("Could not open all refactored files", exception.Message, NotificationType.Warning);
        }
        finally
        {
            if (selectedTab != null && _tabs.Items.Contains(selectedTab)) _tabs.SelectedItem = selectedTab;
        }
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

        var views = new (string Name, Symbol Icon, Control View)[]
        {
            ("Files", Symbol.Folder, _filesTreeView),
            ("Git", Symbol.Share, _gitWidget),
            ("Structure", Symbol.List, _structureTree),
            ("Hierarchy", Symbol.Library, _hierarchyTree),
            ("Inspector", Symbol.Find, _inspector),
            ("Environment", Symbol.Setting, _env),
            ("Connections", Symbol.Link, _connectionsWidget),
            ("AST", Symbol.Document, _astViewer)
        };
        var selectedView = views.First(view => view.Name == _activeSideView);
        var heading = new TextBlock
        {
            Text = selectedView.Name,
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(10, 10, 8, 8),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var headingBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(38, 38, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = heading
        };
        var content = new ContentControl
        {
            Content = selectedView.View,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var tools = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4) };
        tools.Styles.Add(new Style(selector => selector.OfType<RadioButton>().Class(":pointerover"))
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.FromRgb(50, 50, 55))) }
        });
        tools.Styles.Add(new Style(selector => selector.OfType<RadioButton>().Class(":checked"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.FromRgb(42, 59, 65))),
                new Setter(TemplatedControl.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(80, 190, 200)))
            }
        });
        var group = Guid.NewGuid().ToString();
        var viewButtons = new Dictionary<string, RadioButton>();
        foreach (var view in views)
        {
            var button = new RadioButton
            {
                GroupName = group,
                IsChecked = view.Name == _activeSideView,
                Width = 40,
                Height = 40,
                Margin = new Thickness(2, 0),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Content = new SymbolIcon { Symbol = view.Icon, FontSize = 20 },
                Template = new FuncControlTemplate<RadioButton>((owner, _) =>
                {
                    var border = new Border
                    {
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        Child = new Avalonia.Controls.Presenters.ContentPresenter
                        {
                            Content = owner.Content,
                            HorizontalContentAlignment = HorizontalAlignment.Center,
                            VerticalContentAlignment = VerticalAlignment.Center
                        }
                    };
                    border.Bind(Border.BackgroundProperty, new Avalonia.Data.Binding(nameof(Background)) { Source = owner });
                    border.Bind(Border.BorderBrushProperty, new Avalonia.Data.Binding(nameof(BorderBrush)) { Source = owner });
                    return border;
                })
            };
            ToolTip.SetTip(button, view.Name);
            Avalonia.Automation.AutomationProperties.SetName(button, view.Name);
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked != true) return;
                _activeSideView = view.Name;
                heading.Text = view.Name;
                content.Content = view.View;
                if (view.Name == "AST") RefreshActiveAst();
                if (view.Name == "Git") _gitWidget.Refresh();
            };
            tools.Children.Add(button);
            viewButtons.Add(view.Name, button);
        }
        _activateSideView = name => { if (viewButtons.TryGetValue(name, out var button)) button.IsChecked = true; };
        var rail = new ScrollViewer
        {
            Width = 44,
            Background = new SolidColorBrush(Color.FromRgb(27, 27, 31)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = tools
        };
        var viewPanel = new Grid { RowDefinitions = new RowDefinitions("Auto, *") };
        viewPanel.Children.Add(headingBar);
        Grid.SetRow(content, 1);
        viewPanel.Children.Add(content);
        _sideTabs = new DockPanel();
        DockPanel.SetDock(rail, Dock.Left);
        _sideTabs.Children.Add(rail);
        _sideTabs.Children.Add(viewPanel);

        // Left splitter
        var leftSplitter = new GridSplitter
        {
            Width = 4,
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };

        // Editor area

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

        _outputTabs.Items.Add(new TabItem { Header = "⚠️ Diagnostics", Content = _diagnosticsPanel });
        _outputTabs.Items.Add(new TabItem { Header = "👨‍💻 Terminal", Content = _terminal });
        _debugTab = new TabItem { Header = "Debug", Content = _debugPanel };
        _outputTabs.Items.Add(_debugTab);
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
        Title = "CAAI IDE";
        Width = 1200;
        Height = 800;
        ConfigureTabStrip(_tabs);
        InitializeRoslyn();
        SetupLiveDiagnostics();
        _terminal = new TerminalWidget(_baseDir);
        _nuGetPackageManager = new NuGetPackageManager(_notificationManager, _baseDir);
        _nuGetPackageManager.OutputAppended += (_, message) => _terminal.AppendOutput(message + Environment.NewLine);
        _runner = new DotnetRunner(_terminal);
        InitializeDebugging();
        KeyDown += Window_KeyDown;
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        var root = new HotReloadRoot(BuildUI);
        Content = root;
        HotReloadService.Register(root);
        var startupFile = File.Exists(Path.Combine(_baseDir, "test.cs"))
            ? Path.Combine(_baseDir, "test.cs")
            : GetProjectSourceFiles(_baseDir).FirstOrDefault();
        if (startupFile != null)
            currentSyntaxTree = OpenFile(startupFile);
        SetupStructureTreeContent();
        _tabs.SelectionChanged += OnEditorTabSelectionChanged;
        _tabs.GotFocus += OnEditorGotFocus;
        SettingsData.Instance.SettingsChanged += OnStatusSettingsChanged;
        Closed += (_, _) =>
        {
            SetStatusEditor(null);
            CodeWatcher?.Dispose();
            SettingsData.Instance.SettingsChanged -= OnStatusSettingsChanged;
        };
        UpdateActiveStatusEditor();
        if (Directory.Exists(_baseDir))
            RememberProjectFolder(_baseDir);
    }

    private Control CreateStatusBar()
    {
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto, Auto, Auto"),
            Margin = new Thickness(10, 0),
            ColumnSpacing = 12
        };
        var fields = new[] { _statusFile, _statusGit, _statusDiagnostics, _statusPosition, _statusMode };
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
            _statusGit.IsVisible = bar.Bounds.Width >= 700;
        };
        UpdateStatusBar();
        return bar;
    }

    private void OnEditorTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, _tabs)) return;
        UpdateActiveStatusEditor();
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
        if (_hierarchyTree?.IsFollowingCaret == true) _ = RefreshClassHierarchyAsync();
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
        if (_activeSideView == "AST") RefreshActiveAst();
    }

    private void OnStatusEditorChanged(object? sender, EventArgs e)
    {
        UpdateStatusBar();
        if (_hierarchyTree?.IsFollowingCaret == true || _selectedHierarchyTypeName != null)
            _ = RefreshClassHierarchyAsync();
    }

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
        var previous = (_menu, _toolbar, _structureTree, _diagnosticsPanel, _astViewer,
            _statusFile, _statusGit, _statusDiagnostics, _statusPosition, _statusMode,
            _sideTabs, _filesTreeView, _outputTabs, _mainGrid, _outputDock,
            _outputSplitter, _nuGetTab, _hierarchyTree, _env, _debugTab, _gitWidget, _connectionsWidget, _activateSideView);
        var restoreControls = new Stack<Action>();
        var outputVisible = _outputDock?.IsVisible ?? true;
        var outputHeight = _mainGrid?.RowDefinitions[2].Height ?? _outputHeight;
        var selectedOutput = (_outputTabs?.SelectedItem as TabItem)?.Header?.ToString();
        try
        {
            foreach (var control in new Control[] { _tabs, _inspector, _terminal, _nuGetPackageManager, _debugPanel })
                restoreControls.Push(DetachForRebuild(control));

            _menu = new MainMenuWidget();
            _toolbar = new RunDebugToolbar();
            _structureTree = new CodeStructureWidget();
            _diagnosticsPanel = new DiagnosticsPanel();
            _astViewer = new AstViewer();
            _statusFile = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            _statusGit = new TextBlock { MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
            _statusDiagnostics = new TextBlock { Width = 160, TextAlignment = TextAlignment.Right };
            _statusPosition = new TextBlock { Width = 220, TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis };
            _statusMode = new TextBlock { Width = 140, TextAlignment = TextAlignment.Right };

            var layout = BuildLayout();
            var outputTabs = _outputTabs!;
            if (selectedOutput != null)
                outputTabs.SelectedItem = outputTabs.Items.OfType<TabItem>().FirstOrDefault(tab => tab.Header?.ToString() == selectedOutput);
            _mainGrid!.RowDefinitions[2].Height = outputHeight;
            _outputDock!.IsVisible = outputVisible;
            _outputSplitter.IsVisible = outputVisible;
            _menu.SetOutputVisible(outputVisible);
            _diagnosticsPanel.SetDiagnostics(_documentDiagnostics.Values.SelectMany(items => items).Select(Convert));
            UpdateStatusBar();
            return layout;
        }
        catch
        {
            (_menu, _toolbar, _structureTree, _diagnosticsPanel, _astViewer,
                _statusFile, _statusGit, _statusDiagnostics, _statusPosition, _statusMode,
                _sideTabs, _filesTreeView, _outputTabs, _mainGrid, _outputDock,
                _outputSplitter, _nuGetTab, _hierarchyTree, _env, _debugTab, _gitWidget, _connectionsWidget, _activateSideView) = previous;
            while (restoreControls.TryPop(out var restore))
                restore();
            throw;
        }
    }

    private static Action DetachForRebuild(Control control)
    {
        switch (control.Parent)
        {
            case Panel panel:
                var index = panel.Children.IndexOf(control);
                panel.Children.Remove(control);
                return () =>
                {
                    DetachForRebuild(control);
                    panel.Children.Insert(index, control);
                };
            case ContentControl contentControl:
                contentControl.Content = null;
                return () =>
                {
                    DetachForRebuild(control);
                    contentControl.Content = control;
                };
            case Decorator decorator:
                decorator.Child = null;
                return () =>
                {
                    DetachForRebuild(control);
                    decorator.Child = control;
                };
            case null:
                return () => { DetachForRebuild(control); };
            default:
                throw new InvalidOperationException($"Cannot transfer {control.GetType().Name} from {control.Parent.GetType().Name}.");
        }
    }

    private Control BuildLayout()
    {

        // Setup toolbar events
        _toolbar.OnRunRequested += () => RunDebugAction(RunWithoutDebuggingAsync);
        _toolbar.OnStopRequested += () => RunDebugAction(StopExecutionAsync);
        _toolbar.OnDebugRequested += () => RunDebugAction(StartOrContinueDebuggingAsync);
        _toolbar.OnBuildRequested += () => RunDebugAction(BuildDebugProjectAsync);
        _toolbar.OnPauseRequested += () => RunDebugAction(_debugSession.PauseAsync);
        _toolbar.OnStepOverRequested += () => RunDebugAction(() => ExecuteDebugCommandAsync("next"));
        _toolbar.OnStepIntoRequested += () => RunDebugAction(() => ExecuteDebugCommandAsync("stepIn"));
        _toolbar.OnStepOutRequested += () => RunDebugAction(() => ExecuteDebugCommandAsync("stepOut"));
        _toolbar.OnRestartRequested += () => RunDebugAction(RestartDebuggingAsync);
        _toolbar.SetDebugState(_debugSession.State);
        _toolbar.OnSearchRequested += ShowSearchDialog; // Add search button to toolbar

        var rootNode = new FileSystemNode(_baseDir, true);
        var rootLayout = new DockPanel();

        DockPanel.SetDock(_menu, Dock.Top);
        rootLayout.Children.Add(_menu);
        SetupMenuActions();

        DockPanel.SetDock(_toolbar, Dock.Top);
        rootLayout.Children.Add(_toolbar);

        _hierarchyTree = new ClassHierarchyWidget();
        SetupClassHierarchy();

        SetupEnvironment();
        SetupStructureTree();
        SetupAstViewer();
        SetupFileTreeView();
        _gitWidget = new GitWidget(_gitDirectory, path => OpenFile(path), PrepareGitWorkTreeChange);
        var gitWidget = _gitWidget;
        gitWidget.StatusChanged += status =>
        {
            if (!ReferenceEquals(_gitWidget, gitWidget)) return;
            _statusGit.Text = status;
            ToolTip.SetTip(_statusGit, status);
        };
        gitWidget.Refresh();

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
        _env = new EnvironmentWidget(_workspace, _environmentStore);
        _connectionsWidget = new ConnectionsWidget(_environmentStore);
        _env.ConnectionRequested += async (_, entity) =>
        {
            _activateSideView?.Invoke("Connections");
            await _connectionsWidget.CreateConnectionAsync(entity);
        };
        _connectionsWidget.RevealEntityRequested += (_, entity) =>
        {
            _activateSideView?.Invoke("Environment");
            _env.RevealEntity(entity);
        };
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

    private void RefreshActiveAst()
    {
        if (_statusEditor == null)
        {
            _astViewer.SyntaxTree = null;
            return;
        }

        var tab = _tabs.SelectedItem as TabItem;
        var path = FindEditorInContent(tab?.Content)?.FilePath ?? "";
        var tree = CSharpSyntaxTree.ParseText(_statusEditor.Text, path: path);
        currentSyntaxTree = tree;
        _astViewer.SyntaxTree = tree.GetRoot();
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
        _menu.SetRecentProjects(SettingsData.Instance.GetRecentProjectPaths().Where(Directory.Exists));
        _menu.OnNewNotebook += AddNotebookTab;
        _menu.OnNewCodeAnalysis += AddCodeAnalysisTab;
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
                try
                {
                    await LoadProjectFromFolder(folders[0].Path.LocalPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    ShowProjectLoadError(exception.Message);
                }
            }
        };
        _menu.OnOpenRecentRequested += async path =>
        {
            if (File.Exists(path))
            {
                OpenFile(path);
            }
            else if (Directory.Exists(path))
            {
                try
                {
                    await LoadProjectFromFolder(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    ShowProjectLoadError(exception.Message);
                }
            }
            else
            {
                ShowProjectLoadError($"Recent project or file not found: {path}");
            }
        };
        _menu.OnClearRecentRequested += () =>
        {
            SettingsData.Instance.SetRecentProjectPaths(Array.Empty<string>());
            _menu.SetRecentProjects(Array.Empty<string>());
            SaveApplicationSettings();
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
                if (tab.Content is FilePreviewControl preview)
                {
                    await SavePreviewFileAsync(preview);
                    return;
                }
                var editor = FindEditorInContent(tab.Content);
                if (editor != null && !string.IsNullOrEmpty(editor.FilePath))
                {
                    if (await editor.SaveAsync())
                        ShowNotification("Saved", Path.GetFileName(editor.FilePath), NotificationType.Success);
                    else
                        ShowNotification("Save failed", editor.SaveError ?? $"Could not save {editor.FilePath}", NotificationType.Error);
                }
            }
        };
        _menu.OnSaveAll += async () =>
        {
            var allSaved = true;
            try
            {
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

                    if (tab.Content is FilePreviewControl preview)
                    {
                        if (!await SavePreviewFileAsync(preview))
                            allSaved = false;
                        continue;
                    }

                    var editor = FindEditorInContent(tab.Content);
                    if (editor != null && !string.IsNullOrEmpty(editor.FilePath))
                    {
                        if (!await editor.SaveAsync())
                            allSaved = false;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ShowNotification("Save All failed", exception.Message, NotificationType.Error);
                return;
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
        _menu.OnRunProject += () => RunDebugAction(StartOrContinueDebuggingAsync);
        _menu.OnRunNoDebug += () => RunDebugAction(RunWithoutDebuggingAsync);
        _menu.OnStepInto += () => RunDebugAction(() => ExecuteDebugCommandAsync("stepIn"));
        _menu.OnStepOver += () => RunDebugAction(() => ExecuteDebugCommandAsync("next"));
        _menu.OnToggleBreakpoint += ToggleActiveBreakpoint;
        _menu.OnPauseDebug += () => RunDebugAction(_debugSession.PauseAsync);
        _menu.OnStopDebug += () => RunDebugAction(StopExecutionAsync);
        _menu.OnRestartDebug += () => RunDebugAction(RestartDebuggingAsync);
        _menu.OnStepOut += () => RunDebugAction(() => ExecuteDebugCommandAsync("stepOut"));
        _menu.OnSelectDebugProject += () => RunDebugAction(async () => { await SelectDebugProjectAsync(forcePicker: true); });
        _menu.OnDebugSettings += () => RunDebugAction(OpenDebugSettingsAsync);
        _menu.SetDebugState(_debugSession.State);

        // === Tools/Help Menu ===
        _menu.OnOpenNuGetManager += () =>
        {
            SetOutputVisible(true);
            _outputTabs.SelectedItem = _nuGetTab;
        };
        _menu.OnOpenSettings += async () =>
        {
            await ShowSettings();
            _menu.SetRecentProjects(SettingsData.Instance.GetRecentProjectPaths().Where(Directory.Exists));
        };

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

    private void ShowProjectLoadError(string message)
    {
        ShowNotification("Project not opened", message, NotificationType.Warning);
    }

    private void ShowNotification(string title, string message, NotificationType type) =>
        _notificationManager.Show(new Notification(title, message, type, expiration: TimeSpan.FromSeconds(5)));

    private async Task<bool> SavePreviewFileAsync(FilePreviewControl preview)
    {
        if (await preview.SaveAsync())
        {
            ShowNotification("Saved", Path.GetFileName(preview.FilePath), NotificationType.Success);
            return true;
        }

        ShowNotification("Save failed", preview.SaveError ?? $"Could not save {preview.FilePath}", NotificationType.Error);
        return false;
    }

    private Task<bool> ConfirmClosePreviewAsync(FilePreviewControl preview) =>
        ConfirmCloseUnsavedFileAsync(preview.FilePath, preview.IsDirty, () => SavePreviewFileAsync(preview));

    private Task<bool> ConfirmCloseEditorAsync(CodeEditor editor) =>
        ConfirmCloseUnsavedFileAsync(editor.FilePath, editor.IsDirty, async () =>
        {
            if (await editor.SaveAsync())
            {
                ShowNotification("Saved", Path.GetFileName(editor.FilePath), NotificationType.Success);
                return true;
            }

            ShowNotification("Save failed", editor.SaveError ?? $"Could not save {editor.FilePath}", NotificationType.Error);
            return false;
        });

    private async Task<bool> ConfirmCloseUnsavedFileAsync(string filePath, bool isDirty, Func<Task<bool>> saveAsync)
    {
        if (!isDirty) return true;

        var dialog = new Window
        {
            Title = "Unsaved changes",
            Width = 420,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40))
        };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 14 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Save changes to {Path.GetFileName(filePath)} before closing?",
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        var save = new Button { Content = "Save" };
        var discard = new Button { Content = "Discard" };
        var cancel = new Button { Content = "Cancel" };
        save.Click += (_, _) => dialog.Close(1);
        discard.Click += (_, _) => dialog.Close(2);
        cancel.Click += (_, _) => dialog.Close(0);
        buttons.Children.Add(save);
        buttons.Children.Add(discard);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        var result = await dialog.ShowDialog<int>(this);
        if (result == 1)
            return await saveAsync();
        if (result == 2)
        {
            ShowNotification("Changes discarded", Path.GetFileName(filePath), NotificationType.Warning);
            return true;
        }

        ShowNotification("File kept open", $"Unsaved changes remain in {Path.GetFileName(filePath)}.", NotificationType.Warning);
        return false;
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

        SetupStructureTreeContent();
    }

    private void SetupStructureTreeContent()
    {
        if (currentSyntaxTree != null)
        {
            _structureTree.SetStructure(currentSyntaxTree, currentSyntaxTree.FilePath);
        }
    }

    private void SetupFileTreeView()
    {
        _filesTreeView = new FileExplorerWidget(_baseDir);

        _filesTreeView.OnFileOpened += (path) =>
        {
            currentSyntaxTree = OpenFile(path);
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

    private Action PrepareGitWorkTreeChange(string root)
    {
        var editors = OpenCodeEditors().Where(editor =>
        {
            var relative = Path.GetRelativePath(root, editor.FilePath);
            return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }).ToArray();
        var snapshots = new Dictionary<CodeEditor, string>();
        foreach (var editor in editors)
        {
            var text = editor.GetText();
            if (!File.Exists(editor.FilePath) || File.ReadAllText(editor.FilePath) != text)
                throw new InvalidOperationException("Save or close modified and deleted open files before switching branches or pulling.");
            snapshots.Add(editor, text);
        }
        return () =>
        {
            var conflicts = false;
            foreach (var (editor, text) in snapshots)
            {
                if (!File.Exists(editor.FilePath)) { conflicts = true; continue; }
                var updated = File.ReadAllText(editor.FilePath);
                if (updated == text) continue;
                if (editor.GetText() != text) { conflicts = true; continue; }
                editor._editor.Text = updated;
            }
            if (conflicts)
                throw new InvalidOperationException("Git completed, but some open files were deleted or edited during the operation. Review their buffers before saving.");
        };
    }

    private void WireEvents(string baseDir)
    {
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

    private void AddCodeAnalysisTab()
    {
        var analysis = new CodeAnalysisWidget(_workspace);
        analysis.SourceRequested += (path, line, column) => OpenFileAtLine(path, line - 1, column - 1);
        var tab = new TabItem();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        header.Children.Add(new TextBlock
        {
            Text = "Code Analysis",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        var closeButton = new Button
        {
            Content = "×",
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeight.Bold
        };
        closeButton.Click += (_, e) =>
        {
            e.Handled = true;
            _tabs.Items.Remove(tab);
            analysis.Dispose();
        };
        header.Children.Add(closeButton);
        tab.Header = header;
        tab.Content = analysis;
        _tabs.Items.Add(tab);
        _tabs.SelectedItem = tab;
    }

    private void SetupClassHierarchy()
    {
        _hierarchyTree.TypePicked += symbol =>
        {
            _selectedHierarchyTypeName = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            _ = RefreshClassHierarchyAsync(symbol);
        };
        _hierarchyTree.FollowCaretRequested += () =>
        {
            _selectedHierarchyTypeName = null;
            _ = RefreshClassHierarchyAsync();
        };
        _ = RefreshClassHierarchyAsync();
    }

    private async Task RefreshClassHierarchyAsync(INamedTypeSymbol? selectedType = null)
    {
        var requestVersion = Interlocked.Increment(ref _hierarchyRefreshVersion);
        var compilation = await GetCurrentCompilation();
        if (compilation == null || requestVersion != Volatile.Read(ref _hierarchyRefreshVersion)) return;

        var types = GetHierarchyTypes(compilation.Assembly.GlobalNamespace)
            .Where(type => type.TypeKind == TypeKind.Class && !type.IsImplicitlyDeclared)
            .ToList();

        if (_hierarchyTree.IsFollowingCaret)
        {
            selectedType = await GetClassAtCaretAsync();
            _selectedHierarchyTypeName = null;
        }
        else if (selectedType == null && _selectedHierarchyTypeName != null)
        {
            selectedType = types.FirstOrDefault(type =>
                type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == _selectedHierarchyTypeName);
        }

        if (requestVersion != Volatile.Read(ref _hierarchyRefreshVersion)) return;

        if (!ReferenceEquals(_hierarchyCompilation, compilation))
        {
            _hierarchyCompilation = compilation;
            _hierarchyTree.SetTypes(types, selectedType);
        }

        _hierarchyTree.ShowHierarchy(types, selectedType);
    }

    private static IEnumerable<INamedTypeSymbol> GetHierarchyTypes(INamespaceSymbol root)
    {
        foreach (var type in root.GetTypeMembers())
        {
            yield return type;
            foreach (var nestedType in GetNestedHierarchyTypes(type))
                yield return nestedType;
        }

        foreach (var childNamespace in root.GetNamespaceMembers())
        {
            foreach (var type in GetHierarchyTypes(childNamespace))
                yield return type;
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedHierarchyTypes(INamedTypeSymbol containingType)
    {
        foreach (var nestedType in containingType.GetTypeMembers())
        {
            yield return nestedType;
            foreach (var childType in GetNestedHierarchyTypes(nestedType))
                yield return childType;
        }
    }

    private async Task<INamedTypeSymbol?> GetClassAtCaretAsync()
    {
        if (_tabs.SelectedItem is not TabItem activeTab || FindEditorInContent(activeTab.Content) is not CodeEditor editor)
            return null;

        var document = _workspace.CurrentSolution.GetDocument(editor.DocumentId);
        if (document == null) return null;

        var root = await document.GetSyntaxRootAsync();
        var semanticModel = await document.GetSemanticModelAsync();
        if (root == null || semanticModel == null || root.FullSpan.IsEmpty) return null;

        var offset = Math.Clamp(editor._editor.CaretOffset, root.FullSpan.Start, root.FullSpan.End - 1);
        var declaration = root.FindToken(offset).Parent?
            .AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault();

        if (declaration == null) return null;
        var symbol = semanticModel.GetDeclaredSymbol(declaration) as INamedTypeSymbol;
        return symbol?.TypeKind == TypeKind.Class ? symbol : null;
    }

    public async Task<List<Diagnostic>> GetDiagnosticsForDocument(DocumentId docId)
    {
        var document = _workspace.CurrentSolution.GetDocument(docId);
        if (document == null) return new List<Diagnostic>();
        return (await Task.Run(() => EditorAnalysisService.GetDiagnosticsAsync(document))).ToList();
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
            var diagnostics = (await Task.Run(() => EditorAnalysisService.GetDiagnosticsAsync(document, cancellationToken), cancellationToken)).ToList();
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
            var existingTab = _tabs.Items.OfType<TabItem>().FirstOrDefault(tab =>
                string.Equals(tab.Tag as string, path, StringComparison.OrdinalIgnoreCase) ||
                (FindEditorInContent(tab.Content) is { } editor &&
                 string.Equals(editor.FilePath, path, StringComparison.OrdinalIgnoreCase)));
            if (existingTab != null)
            {
                _tabs.SelectedItem = existingTab;
                var existingTree = FindEditorInContent(existingTab.Content) is { } existingEditor
                    ? CSharpSyntaxTree.ParseText(existingEditor.GetText(), path: path)
                    : null;
                UpdateStructureForFile(existingTree, path);
                return existingTree;
            }
            if (!File.Exists(path)) return null;

            if (!IsCodeEditorFile(path))
            {
                OpenPreviewTab(path);
                UpdateStructureForFile(null, path);
                return null;
            }

            var syntaxTree = OpenExistingFile(path, File.ReadAllText(path));
            UpdateStructureForFile(syntaxTree, path);
            return syntaxTree;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _notificationManager.Show(new Notification("Cannot open file", exception.Message, NotificationType.Error));
            return null;
        }
    }

    private void UpdateStructureForFile(SyntaxTree? syntaxTree, string filePath)
    {
        currentSyntaxTree = syntaxTree;
        _structureTree.SetStructure(syntaxTree ?? CSharpSyntaxTree.ParseText(string.Empty), filePath);
    }

    private static bool IsCSharpSourceFile(string path) =>
        Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".csx", StringComparison.OrdinalIgnoreCase);

    private static bool IsCodeEditorFile(string path) =>
        IsCSharpSourceFile(path) ||
        (Path.GetFileName(path).Equals("launch.json", StringComparison.OrdinalIgnoreCase) &&
         path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
             .Any(segment => segment.Equals(".caai", StringComparison.OrdinalIgnoreCase)));

    private void OpenPreviewTab(string path)
    {
        var preview = new FilePreviewControl(path);
        var tab = new TabItem
        {
            Header = Path.GetFileName(path),
            Content = preview,
            Tag = Path.GetFullPath(path)
        };
        var closeButton = new Button
        {
            Content = "×",
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeight.Bold
        };
        closeButton.Click += async (_, e) =>
        {
            e.Handled = true;
            if (!await ConfirmClosePreviewAsync(preview)) return;
            _tabs.Items.Remove(tab);
            Dispatcher.UIThread.Post(preview.Dispose, DispatcherPriority.Background);
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var title = new TextBlock
        {
            Text = Path.GetFileName(path),
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        preview.DirtyStateChanged += isDirty => title.Text = isDirty
            ? $"{Path.GetFileName(path)} *"
            : Path.GetFileName(path);
        preview.SaveRequested += async (_, _) => await SavePreviewFileAsync(preview);
        header.Children.Add(title);
        header.Children.Add(closeButton);
        tab.Header = header;
        _tabs.Items.Add(tab);
        _tabs.SelectedItem = tab;
    }

    private SyntaxTree? OpenExistingFile(string path, string src, string? savedText = null)
    {
        SyntaxTree? syntaxTree = null;
        if (IsCSharpSourceFile(path))
        {
            syntaxTree = CSharpSyntaxTree.ParseText(src, path: path);
            currentSyntaxTree = syntaxTree;
        }

        var docId = RegisterFileInWorkspace(path, src);

        var editor = new CodeEditor(path, _workspace, docId, src, savedText);
        editor.ConfigureDebugging(_debugBreakpoints);
        // Subscribe to events
        editor.GoToDefinitionRequested += OnGoToDefinitionRequested;
        editor.FindReferencesRequested += OnFindReferencesRequested;
        editor.FormatDocumentRequested += OnFormatDocumentRequested;
        editor.AddSymbolToEnvironmentRequested += (_, symbol) =>
        {
            _activateSideView?.Invoke("Environment");
            _env.AddSymbolEntity(symbol);
        };
        editor.WorkspaceChangesApplied += async (_, documentIds) => await RevealRefactoredDocumentsAsync(documentIds);

        var tab = new TabItem();

        // Header with text + close button
        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5
        };

        var headerText = new TextBlock
        {
            Text = editor.IsDirty ? $"{Path.GetFileName(path)} *" : Path.GetFileName(path),
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        var editorFileName = Path.GetFileName(path);
        editor.DirtyStateChanged += isDirty => headerText.Text = isDirty ? $"{editorFileName} *" : editorFileName;

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

        closeButton.Click += (_, e) =>
        {
            e.Handled = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (!await ConfirmCloseEditorAsync(editor)) return;
                _tabs.Items.Remove(tab);
                try
                {
                    editor.CloseEditor();
                }
                catch (Exception exception)
                {
                    ShowNotification("Could not fully close editor", exception.Message, NotificationType.Warning);
                }
            });
        };

        headerPanel.Children.Add(headerText);
        headerPanel.Children.Add(closeButton);

        tab.Header = headerPanel;
        tab.Tag = Path.GetFullPath(path);

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
        return syntaxTree;
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
        try
        {
            var referencesWindow = await ReferencesWindow.CreateAsync(e.Symbol, e.References ?? new List<ReferenceLocation>());
            referencesWindow.NavigateRequested += (filePath, line, column) => OpenFileAtLine(filePath, line, column);
            if (!referencesWindow.IsVisible)
                referencesWindow.Show(this);
        }
        catch (Exception exception)
        {
            ShowNotification("References", $"Could not open references: {exception.Message}", NotificationType.Error);
        }
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
