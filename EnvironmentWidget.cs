using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using System.Text.Json;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;

namespace CAAI;

public enum EntityType
{
    CodeSymbol,
    File,
    DataValue,
    Folder,
    Project
}

public class EnvironmentEntity : INotifyPropertyChanged
{
    private string _name = "";
    private EntityType _type;
    private object? _value;
    private string _description = "";
    private bool _isExpanded = true;
    private bool _isSelected;
    private ObservableCollection<EnvironmentEntity> _children = new();

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public EntityType Type
    {
        get => _type;
        set { _type = value; OnPropertyChanged(); }
    }

    public object? Value
    {
        get => _value;
        set { _value = value; OnPropertyChanged(); }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public ObservableCollection<EnvironmentEntity> Children
    {
        get => _children;
        set { _children = value; OnPropertyChanged(); }
    }

    public ISymbol? RoslynSymbol { get; set; }
    public string? FilePath { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class EnvironmentWidget : UserControl
{
    private readonly AdhocWorkspace _workspace;
    private readonly ObservableCollection<EnvironmentEntity> _entities = new();
    private TreeView _entityTree = null!;
    private TextBox _searchBox = null!;
    private ComboBox _typeFilter = null!;
    private TextBlock _statusText = null!;
    private Border _detailsPanel = null!;
    private ContextMenu _contextMenu = null!;

    public event EventHandler<EnvironmentEntity>? EntitySelected;
    public event EventHandler<EnvironmentEntity>? EntityDoubleClicked;

    public EnvironmentWidget(AdhocWorkspace workspace)
    {
        _workspace = workspace;
        BuildUI();
        SetupKeyboardHandling();
        BuildContextMenu();
        AddDefaultEntities();
    }

    #region UI Construction

    private void BuildUI()
    {
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35))
        };

        // Header
        mainGrid.Children.Add(BuildHeader());
        Grid.SetRow(mainGrid.Children[^1], 0);

        // Toolbar
        mainGrid.Children.Add(BuildToolbar());
        Grid.SetRow(mainGrid.Children[^1], 1);

        // Content area with tree and details
        mainGrid.Children.Add(BuildContentArea());
        Grid.SetRow(mainGrid.Children[^1], 2);

        // Status bar
        mainGrid.Children.Add(BuildStatusBar());
        Grid.SetRow(mainGrid.Children[^1], 3);

        Content = mainGrid;
    }

    private Border BuildHeader()
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Padding = new Thickness(10, 8)
        };

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto")
        };

        var title = new TextBlock
        {
            Text = "🔧 Environment",
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };

        _searchBox = new TextBox
        {
            Watermark = "Search entities...",
            Width = 200,
            Margin = new Thickness(10, 0),
            Background = new SolidColorBrush(Color.FromRgb(55, 55, 60)),
            Foreground = Brushes.White
        };
        _searchBox.TextChanged += OnSearchTextChanged;

        // Action buttons as icons
        var actionsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Margin = new Thickness(10, 0, 5, 0)
        };

        var exportBtn = CreateIconButton("💾", "Export environment (Ctrl+E)");
        exportBtn.Click += (_, _) => ExportEnvironment();
        
        var importBtn = CreateIconButton("📂", "Import environment (Ctrl+I)");
        importBtn.Click += (_, _) => ImportEnvironment();

        var clearBtn = CreateIconButton("🧹", "Clear all entities (Ctrl+Shift+X)");
        clearBtn.Click += (_, _) => ClearAll();

        actionsPanel.Children.Add(exportBtn);
        actionsPanel.Children.Add(importBtn);
        actionsPanel.Children.Add(clearBtn);

        // More options button
        var moreBtn = CreateIconButton("⋮", "More options");
        moreBtn.Click += (_, _) => ShowContextMenu(moreBtn);
        actionsPanel.Children.Add(moreBtn);

        headerGrid.Children.Add(title);
        Grid.SetColumn(title, 0);
        headerGrid.Children.Add(_searchBox);
        Grid.SetColumn(_searchBox, 1);
        headerGrid.Children.Add(actionsPanel);
        Grid.SetColumn(actionsPanel, 2);

        header.Child = headerGrid;
        return header;
    }

    private StackPanel BuildToolbar()
    {
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 8)
        };

        // Add entity buttons as clickable icons
        var addLabel = new TextBlock
        {
            Text = "Add:",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };

        var addSymbolBtn = CreateIconButton("📘", "Add code symbol (Ctrl+Shift+S)");
        addSymbolBtn.Click += (_, _) => ShowAddSymbolDialog();

        var addFileBtn = CreateIconButton("📄", "Add file (Ctrl+Shift+F)");
        addFileBtn.Click += (_, _) => ShowAddFileDialog();

        var addDataBtn = CreateIconButton("📊", "Add data value (Ctrl+Shift+D)");
        addDataBtn.Click += (_, _) => ShowAddDataDialog();

        var addFolderBtn = CreateIconButton("📁", "Add folder (Ctrl+Shift+G)");
        addFolderBtn.Click += (_, _) => ShowAddFolderDialog();

        // Separator
        var separator = new Border
        {
            Width = 1,
            Height = 20,
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Margin = new Thickness(5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Filter combo
        var filterLabel = new TextBlock
        {
            Text = "Filter:",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 5, 0)
        };

        _typeFilter = new ComboBox
        {
            Width = 130,
            Background = new SolidColorBrush(Color.FromRgb(55, 55, 60)),
            Foreground = Brushes.White,
            ItemsSource = new[] { "All", "Code Symbol", "File", "Data Value", "Folder", "Project" },
            SelectedIndex = 0
        };
        _typeFilter.SelectionChanged += OnFilterChanged;

        toolbar.Children.Add(addLabel);
        toolbar.Children.Add(addSymbolBtn);
        toolbar.Children.Add(addFileBtn);
        toolbar.Children.Add(addDataBtn);
        toolbar.Children.Add(addFolderBtn);
        toolbar.Children.Add(separator);
        toolbar.Children.Add(filterLabel);
        toolbar.Children.Add(_typeFilter);

        return toolbar;
    }

    private Button CreateIconButton(string icon, string tooltip)
    {
        var btn = new Button
        {
            Content = icon,
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4),
            FontSize = 16,
            Cursor = new Cursor(StandardCursorType.Hand),
            //ToolTip = tooltip
        };

        // Hover effect
        btn.PointerEntered += (_, _) =>
            btn.Background = new SolidColorBrush(Color.FromArgb(50, 100, 100, 255));
        btn.PointerExited += (_, _) =>
            btn.Background = Brushes.Transparent;

        return btn;
    }

    private Grid BuildContentArea()
    {
        var splitter = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,4,280")
        };

        // Entity tree with context menu
        _entityTree = new TreeView
        {
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            ItemsSource = _entities,
            ItemTemplate = CreateEntityTemplate(),
            ContextMenu = _contextMenu
        };
        _entityTree.SelectionChanged += OnEntitySelectionChanged;
        _entityTree.DoubleTapped += OnEntityDoubleTapped;
        _entityTree.PointerPressed += OnEntityTreePointerPressed;

        var treeContainer = new ScrollViewer
        {
            Content = _entityTree,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        // Splitter
        var treeSplitter = new GridSplitter
        {
            Width = 4,
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            ResizeBehavior = GridResizeBehavior.PreviousAndNext
        };

        // Details panel
        _detailsPanel = BuildDetailsPanel();

        splitter.Children.Add(treeContainer);
        Grid.SetColumn(treeContainer, 0);
        splitter.Children.Add(treeSplitter);
        Grid.SetColumn(treeSplitter, 1);
        splitter.Children.Add(_detailsPanel);
        Grid.SetColumn(_detailsPanel, 2);

        return splitter;
    }

    private Border BuildDetailsPanel()
    {
        var panel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40)),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(5)
        };

        var content = new StackPanel { Spacing = 10 };

        var title = new TextBlock
        {
            Text = "Details",
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 5)
        };
        content.Children.Add(title);

        var detailsText = new TextBlock
        {
            Text = "Select an entity to view details\n\n📘 Add Symbol: Ctrl+Shift+S\n📄 Add File: Ctrl+Shift+F\n📊 Add Data: Ctrl+Shift+D\n📁 Add Folder: Ctrl+Shift+G\n\nDelete: Remove selected\nCtrl+Shift+X: Clear all\nCtrl+E: Export\nCtrl+I: Import",
            Foreground = Brushes.Gray,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap
        };
        content.Children.Add(detailsText);

        panel.Child = content;
        return panel;
    }

    private Border BuildStatusBar()
    {
        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40)),
            Padding = new Thickness(10, 5)
        };

        var statusGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        _statusText = new TextBlock
        {
            Text = "Ready",
            FontSize = 11,
            Foreground = Brushes.Gray
        };

        var entityCount = new TextBlock
        {
            Text = $"{_entities.Count} entities",
            FontSize = 10,
            Foreground = Brushes.DarkGray
        };

        statusGrid.Children.Add(_statusText);
        Grid.SetColumn(_statusText, 0);
        statusGrid.Children.Add(entityCount);
        Grid.SetColumn(entityCount, 1);

        statusBar.Child = statusGrid;
        return statusBar;
    }

    #endregion

    #region Context Menu

    private void BuildContextMenu()
    {
        _contextMenu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
            Foreground = Brushes.White,
            FontSize = 12
        };

        var addMenu = new MenuItem { Header = "➕ Add" };
        addMenu.Items.Add(CreateMenuItem("📘 Add Symbol", (_, _) => ShowAddSymbolDialog(), Key.S, KeyModifiers.Control | KeyModifiers.Shift));
        addMenu.Items.Add(CreateMenuItem("📄 Add File", (_, _) => ShowAddFileDialog(), Key.F, KeyModifiers.Control | KeyModifiers.Shift));
        addMenu.Items.Add(CreateMenuItem("📊 Add Data", (_, _) => ShowAddDataDialog(), Key.D, KeyModifiers.Control | KeyModifiers.Shift));
        addMenu.Items.Add(CreateMenuItem("📁 Add Folder", (_, _) => ShowAddFolderDialog(), Key.G, KeyModifiers.Control | KeyModifiers.Shift));

        var removeMenu = new MenuItem { Header = "🗑 Remove" };
        removeMenu.Items.Add(CreateMenuItem("Remove Selected", (_, _) => RemoveSelectedEntity(), Key.Delete));
        removeMenu.Items.Add(CreateMenuItem("Remove All of Type", (_, _) => RemoveAllOfSelectedType()));
        removeMenu.Items.Add(CreateMenuItem("Clear All", (_, _) => ClearAll(), Key.X, KeyModifiers.Control | KeyModifiers.Shift));

        var ioMenu = new MenuItem { Header = "📂 Import/Export" };
        ioMenu.Items.Add(CreateMenuItem("💾 Export Environment", (_, _) => ExportEnvironment(), Key.E, KeyModifiers.Control));
        ioMenu.Items.Add(CreateMenuItem("📂 Import Environment", (_, _) => ImportEnvironment(), Key.I, KeyModifiers.Control));

        var viewMenu = new MenuItem { Header = "👁 View" };
        viewMenu.Items.Add(CreateMenuItem("Expand All", (_, _) => ExpandAll()));
        viewMenu.Items.Add(CreateMenuItem("Collapse All", (_, _) => CollapseAll()));
        viewMenu.Items.Add(CreateMenuItem("Refresh", (_, _) => RefreshView(), Key.F5));

        _contextMenu.Items.Add(addMenu);
        _contextMenu.Items.Add(removeMenu);
        _contextMenu.Items.Add(ioMenu);
        _contextMenu.Items.Add(viewMenu);
        _contextMenu.Items.Add(new Separator());
        _contextMenu.Items.Add(CreateMenuItem("📋 Copy Name", (_, _) => CopySelectedName()));
        _contextMenu.Items.Add(CreateMenuItem("📋 Copy Value", (_, _) => CopySelectedValue()));
    }

    private MenuItem CreateMenuItem(string header, EventHandler<object> handler, Key? gestureKey = null, KeyModifiers? gestureModifiers = null)
    {
        var menuItem = new MenuItem
        {
            Header = header,
            Foreground = Brushes.White,
            Background = Brushes.Transparent
        };
        menuItem.Click += handler;

        if (gestureKey.HasValue)
        {
            menuItem.InputGesture = new KeyGesture(gestureKey.Value, gestureModifiers ?? KeyModifiers.None);
        }

        return menuItem;
    }

    private void ShowContextMenu(Button button)
    {
        _contextMenu.Open(button);
    }

    #endregion

    #region Data Templates

    private IDataTemplate CreateEntityTemplate()
    {
        return new FuncTreeDataTemplate<EnvironmentEntity>(
            match: _ => true,
            build: (entity, _) =>
            {
                var border = new Border
                {
                    Padding = new Thickness(5, 3),
                    Margin = new Thickness(2),
                    CornerRadius = new CornerRadius(3)
                };

                var stack = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8
                };

                // Icon
                var icon = new TextBlock
                {
                    Text = GetEntityIcon(entity.Type),
                    FontSize = 14
                };

                // Name
                var name = new TextBlock
                {
                    Text = entity.Name,
                    FontWeight = entity.Type == EntityType.CodeSymbol ? FontWeight.Bold : FontWeight.Normal,
                    FontSize = 12,
                    Foreground = GetEntityColor(entity)
                };

                // Type badge
                var badge = new Border
                {
                    Background = GetTypeBadgeColor(entity.Type),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(6, 2),
                    Margin = new Thickness(5, 0, 0, 0),
                    IsVisible = entity.Type != EntityType.CodeSymbol
                };
                badge.Child = new TextBlock
                {
                    Text = entity.Type.ToString().Replace("CodeSymbol", "Symbol"),
                    FontSize = 9,
                    Foreground = Brushes.White
                };

                // Value preview for data
                if (entity.Type == EntityType.DataValue && entity.Value != null)
                {
                    var preview = new TextBlock
                    {
                        Text = $" = {GetValuePreview(entity.Value)}",
                        FontSize = 10,
                        Foreground = Brushes.Gray,
                        FontStyle = FontStyle.Italic
                    };
                    stack.Children.Add(preview);
                }

                stack.Children.Add(icon);
                stack.Children.Add(name);
                if (badge.IsVisible) stack.Children.Add(badge);

                border.Child = stack;

                // Hover effect
                border.PointerEntered += (_, _) =>
                    border.Background = new SolidColorBrush(Color.FromArgb(50, 100, 100, 255));
                border.PointerExited += (_, _) =>
                    border.Background = Brushes.Transparent;

                return border;
            },
            itemsSelector: entity => entity.Children
        );
    }

    #endregion

    #region Visual Helpers

    private string GetEntityIcon(EntityType type) => type switch
    {
        EntityType.CodeSymbol => "📘",
        EntityType.File => "📄",
        EntityType.DataValue => "📊",
        EntityType.Folder => "📁",
        EntityType.Project => "🎯",
        _ => "🔧"
    };

    private IBrush GetEntityColor(EnvironmentEntity entity)
    {
        if (entity.Type == EntityType.CodeSymbol && entity.RoslynSymbol != null)
        {
            return entity.RoslynSymbol.Kind switch
            {
                SymbolKind.NamedType => new SolidColorBrush(Color.FromRgb(78, 201, 176)),
                SymbolKind.Method => new SolidColorBrush(Color.FromRgb(220, 140, 86)),
                SymbolKind.Property => new SolidColorBrush(Color.FromRgb(156, 220, 254)),
                _ => Brushes.White
            };
        }

        return entity.Type switch
        {
            EntityType.File => new SolidColorBrush(Color.FromRgb(100, 150, 200)),
            EntityType.DataValue => new SolidColorBrush(Color.FromRgb(200, 150, 100)),
            EntityType.Folder => new SolidColorBrush(Color.FromRgb(150, 150, 100)),
            _ => Brushes.White
        };
    }

    private IBrush GetTypeBadgeColor(EntityType type) => type switch
    {
        EntityType.File => new SolidColorBrush(Color.FromRgb(100, 100, 200)),
        EntityType.DataValue => new SolidColorBrush(Color.FromRgb(200, 100, 100)),
        EntityType.Folder => new SolidColorBrush(Color.FromRgb(150, 100, 50)),
        EntityType.Project => new SolidColorBrush(Color.FromRgb(100, 150, 50)),
        _ => new SolidColorBrush(Color.FromRgb(100, 100, 100))
    };

    private string GetValuePreview(object value)
    {
        var str = value.ToString() ?? "null";
        return str.Length > 50 ? str[..47] + "..." : str;
    }

    #endregion

    #region Context Menu Actions

    private void RemoveAllOfSelectedType()
    {
        var selected = _entityTree.SelectedItem as EnvironmentEntity;
        if (selected != null)
        {
            var toRemove = _entities.Where(e => e.Type == selected.Type).ToList();
            foreach (var entity in toRemove)
            {
                _entities.Remove(entity);
            }
            UpdateStatus($"Removed {toRemove.Count} entities of type {selected.Type}");
        }
    }

    private void ExpandAll()
    {
        foreach (var entity in _entities)
        {
            ExpandEntity(entity, true);
        }
        UpdateStatus("Expanded all entities");
    }

    private void CollapseAll()
    {
        foreach (var entity in _entities)
        {
            ExpandEntity(entity, false);
        }
        UpdateStatus("Collapsed all entities");
    }

    private void ExpandEntity(EnvironmentEntity entity, bool expand)
    {
        entity.IsExpanded = expand;
        foreach (var child in entity.Children)
        {
            ExpandEntity(child, expand);
        }
    }

    private void RefreshView()
    {
        var selectedItem = _entityTree.SelectedItem;
        var itemsSource = _entityTree.ItemsSource;
        _entityTree.ItemsSource = null;
        _entityTree.ItemsSource = itemsSource;
        
        if (selectedItem != null)
        {
            _entityTree.SelectedItem = selectedItem;
        }
        UpdateStatus("View refreshed");
    }

    private void CopySelectedName()
    {
        var selected = _entityTree.SelectedItem as EnvironmentEntity;
        if (selected != null)
        {
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(selected.Name);
            UpdateStatus($"Copied: {selected.Name}");
        }
    }

    private void CopySelectedValue()
    {
        var selected = _entityTree.SelectedItem as EnvironmentEntity;
        if (selected?.Value != null)
        {
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(selected.Value.ToString() ?? "");
            UpdateStatus($"Copied value of: {selected.Name}");
        }
    }

    #endregion

    #region Keyboard Handling

    private void SetupKeyboardHandling()
    {
        this.KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Delete: Remove selected entity
        if (e.Key == Key.Delete)
        {
            RemoveSelectedEntity();
            e.Handled = true;
        }
        // Ctrl+Shift+X: Clear all
        else if (e.Key == Key.X && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            ClearAll();
            e.Handled = true;
        }
        // Ctrl+E: Export
        else if (e.Key == Key.E && e.KeyModifiers == KeyModifiers.Control)
        {
            ExportEnvironment();
            e.Handled = true;
        }
        // Ctrl+I: Import
        else if (e.Key == Key.I && e.KeyModifiers == KeyModifiers.Control)
        {
            ImportEnvironment();
            e.Handled = true;
        }
        // Ctrl+Shift+S: Add Symbol
        else if (e.Key == Key.S && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            ShowAddSymbolDialog();
            e.Handled = true;
        }
        // Ctrl+Shift+F: Add File
        else if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            ShowAddFileDialog();
            e.Handled = true;
        }
        // Ctrl+Shift+D: Add Data
        else if (e.Key == Key.D && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            ShowAddDataDialog();
            e.Handled = true;
        }
        // Ctrl+Shift+G: Add Folder
        else if (e.Key == Key.G && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            ShowAddFolderDialog();
            e.Handled = true;
        }
        // F5: Refresh
        else if (e.Key == Key.F5)
        {
            RefreshView();
            e.Handled = true;
        }
    }

    #endregion

    #region Mouse Handling

    private void OnEntityTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Handle right-click
        if (e.GetCurrentPoint(_entityTree).Properties.IsRightButtonPressed)
        {
            var hitTest = _entityTree.InputHitTest(e.GetPosition(_entityTree));
            if (hitTest != null)
            {
                // Select item under mouse for context menu operations
                var visual = hitTest as Visual;
                while (visual != null)
                {
                    if (visual is TreeViewItem treeItem && treeItem.DataContext is EnvironmentEntity entity)
                    {
                        _entityTree.SelectedItem = entity;
                        break;
                    }
                    visual = visual.GetVisualParent() as Visual;
                }
            }
        }
    }

    #endregion

    #region Entity Operations

    private void AddDefaultEntities()
    {
        _entities.Clear();

        var project = _workspace.CurrentSolution.Projects.FirstOrDefault();
        if (project != null)
        {
            var projectEntity = new EnvironmentEntity
            {
                Name = project.Name,
                Type = EntityType.Project,
                Description = $"Project: {project.Name}",
                Value = project
            };

            foreach (var reference in project.MetadataReferences.Take(5))
            {
                projectEntity.Children.Add(new EnvironmentEntity
                {
                    Name = reference.Display?.Split(',').First() ?? "Reference",
                    Type = EntityType.File,
                    Description = reference.Display ?? ""
                });
            }

            _entities.Add(projectEntity);
        }
        UpdateStatus("Environment ready");
    }

    private async void ShowAddSymbolDialog()
    {
        var dialog = new Window
        {
            Title = "Add Symbol",
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40))
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };

        panel.Children.Add(new TextBlock { Text = "Enter symbol name to add:", Foreground = Brushes.White });

        var input = new TextBox { Watermark = "Type symbol name..." };
        panel.Children.Add(input);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var okBtn = new Button { Content = "Add", Background = new SolidColorBrush(Color.FromRgb(0, 100, 0)), Foreground = Brushes.White };
        var cancelBtn = new Button { Content = "Cancel", Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)), Foreground = Brushes.White };

        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        okBtn.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text))
                await TryAddSymbol(input.Text);
            dialog.Close();
        };

        cancelBtn.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this.GetVisualRoot() as Window ?? new Window());
    }

    private async void ShowAddFileDialog()
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
            return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select File",
            AllowMultiple = true
        });

        foreach (var file in files)
            AddFileEntity(file.Path.LocalPath);
    }

    private void ShowAddDataDialog()
    {
        var dialog = new Window
        {
            Title = "Add Data",
            Width = 400,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40))
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };

        panel.Children.Add(new TextBlock { Text = "Data Name:", Foreground = Brushes.White });
        var nameInput = new TextBox();
        panel.Children.Add(nameInput);

        panel.Children.Add(new TextBlock { Text = "Data Value:", Foreground = Brushes.White });
        var valueInput = new TextBox();
        panel.Children.Add(valueInput);

        panel.Children.Add(new TextBlock { Text = "Data Type:", Foreground = Brushes.White });
        var typeCombo = new ComboBox
        {
            ItemsSource = new[] { "String", "Integer", "Double", "Boolean", "JSON" },
            SelectedIndex = 0
        };
        panel.Children.Add(typeCombo);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var okBtn = new Button { Content = "Add", Background = new SolidColorBrush(Color.FromRgb(0, 100, 0)), Foreground = Brushes.White };
        var cancelBtn = new Button { Content = "Cancel", Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)), Foreground = Brushes.White };

        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        okBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(nameInput.Text))
            {
                AddDataEntity(nameInput.Text, valueInput.Text ?? "", typeCombo.SelectedItem?.ToString() ?? "String");
            }
            dialog.Close();
        };

        cancelBtn.Click += (_, _) => dialog.Close();

        dialog.ShowDialog(this.GetVisualRoot() as Window ?? new Window());
    }

    private void ShowAddFolderDialog()
    {
        var dialog = new Window
        {
            Title = "Add Folder",
            Width = 350,
            Height = 120,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40))
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };

        panel.Children.Add(new TextBlock { Text = "Folder Name:", Foreground = Brushes.White });
        var nameInput = new TextBox { Text = "New Folder" };
        panel.Children.Add(nameInput);

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var okBtn = new Button { Content = "Add", Background = new SolidColorBrush(Color.FromRgb(0, 100, 0)), Foreground = Brushes.White };
        var cancelBtn = new Button { Content = "Cancel", Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)), Foreground = Brushes.White };

        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonPanel);

        dialog.Content = panel;

        okBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(nameInput.Text))
            {
                AddFolderEntity(nameInput.Text);
            }
            dialog.Close();
        };

        cancelBtn.Click += (_, _) => dialog.Close();

        dialog.ShowDialog(this.GetVisualRoot() as Window ?? new Window());
    }

    private async Task TryAddSymbol(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var project = _workspace.CurrentSolution.Projects.FirstOrDefault();
        if (project == null) return;

        var symbols = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
            project.Solution, name, SymbolFilter.All);
        var found = symbols.FirstOrDefault();

        if (found != null)
        {
            var entity = new EnvironmentEntity
            {
                Name = found.Name,
                Type = EntityType.CodeSymbol,
                RoslynSymbol = found,
                Description = $"{found.Kind} from {found.ContainingNamespace}"
            };

            _entities.Add(entity);
            UpdateStatus($"Added symbol: {found.Name}");
        }
        else
        {
            UpdateStatus($"Symbol '{name}' not found", true);
        }
    }

    private void AddFileEntity(string filePath)
    {
        var entity = new EnvironmentEntity
        {
            Name = Path.GetFileName(filePath),
            Type = EntityType.File,
            FilePath = filePath,
            Description = $"File: {filePath}",
            Value = File.Exists(filePath) ? File.ReadAllText(filePath) : null
        };

        _entities.Add(entity);
        UpdateStatus($"Added file: {Path.GetFileName(filePath)}");
    }

    private void AddDataEntity(string name, string value, string type)
    {
        object convertedValue = type switch
        {
            "Integer" => int.TryParse(value, out var intVal) ? intVal : value,
            "Double" => double.TryParse(value, out var dblVal) ? dblVal : value,
            "Boolean" => bool.TryParse(value, out var boolVal) ? boolVal : value,
            "JSON" => value,
            _ => value
        };

        var entity = new EnvironmentEntity
        {
            Name = name,
            Type = EntityType.DataValue,
            Value = convertedValue,
            Description = $"Data: {type} = {GetValuePreview(convertedValue)}"
        };

        _entities.Add(entity);
        UpdateStatus($"Added data: {name}");
    }

    private void AddFolderEntity(string folderName)
    {
        var entity = new EnvironmentEntity
        {
            Name = folderName,
            Type = EntityType.Folder,
            Description = $"Folder: {folderName}",
            Children = new ObservableCollection<EnvironmentEntity>()
        };

        _entities.Add(entity);
        UpdateStatus($"Added folder: {folderName}");
    }

    private void RemoveSelectedEntity()
    {
        var selected = _entityTree.SelectedItem as EnvironmentEntity;
        if (selected != null)
        {
            _entities.Remove(selected);
            UpdateStatus($"Removed: {selected.Name}");
        }
    }

    private void ClearAll()
    {
        _entities.Clear();
        AddDefaultEntities();
        UpdateStatus("Environment cleared");
    }

    private void ExportEnvironment()
    {
        var exportData = _entities.Select(e => new
        {
            e.Name,
            Type = e.Type.ToString(),
            e.Description,
            Value = e.Value?.ToString(),
            e.FilePath,
            Children = e.Children.Select(c => new { c.Name, c.Description })
        }).ToList();

        var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions { WriteIndented = true });
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"environment_export_{timestamp}.json");

        File.WriteAllText(filePath, json);
        UpdateStatus($"Exported to: {Path.GetFileName(filePath)}");
    }

    private async void ImportEnvironment()
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider is null)
            return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Environment",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("JSON Files") { Patterns = new[] { "*.json" } }
            }
        });
        if (files.Count == 0)
            return;

        var file = files[0].Path.LocalPath;
        try
        {
            var json = await File.ReadAllTextAsync(file);
            UpdateStatus($"Imported from: {Path.GetFileName(file)}");
        }
        catch (Exception ex)
        {
            UpdateStatus($"Import error: {ex.Message}", true);
        }
    }

    #endregion

    #region Event Handlers

    private void OnEntitySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_entityTree.SelectedItem is EnvironmentEntity entity)
        {
            UpdateDetailsPanel(entity);
            EntitySelected?.Invoke(this, entity);
        }
    }

    private void OnEntityDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_entityTree.SelectedItem is EnvironmentEntity entity)
        {
            EntityDoubleClicked?.Invoke(this, entity);

            if (entity.Type == EntityType.File && File.Exists(entity.FilePath))
            {
                System.Diagnostics.Process.Start("open", entity.FilePath);
            }
        }
    }

    private void UpdateDetailsPanel(EnvironmentEntity entity)
    {
        var content = new StackPanel { Spacing = 8 };

        content.Children.Add(new TextBlock
        {
            Text = entity.Name,
            FontWeight = FontWeight.Bold,
            FontSize = 14,
            Foreground = GetEntityColor(entity)
        });

        content.Children.Add(new TextBlock
        {
            Text = $"Type: {entity.Type}",
            FontSize = 11,
            Foreground = Brushes.Gray
        });

        if (!string.IsNullOrEmpty(entity.Description))
        {
            content.Children.Add(new TextBlock
            {
                Text = entity.Description,
                FontSize = 11,
                Foreground = Brushes.LightGray,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (entity.Value != null)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"Value: {GetValuePreview(entity.Value)}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 100)),
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (!string.IsNullOrEmpty(entity.FilePath))
        {
            content.Children.Add(new TextBlock
            {
                Text = $"Path: {entity.FilePath}",
                FontSize = 10,
                Foreground = Brushes.Gray
            });
        }

        if (entity.RoslynSymbol != null)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"Kind: {entity.RoslynSymbol.Kind}",
                FontSize = 11,
                Foreground = Brushes.Gray
            });

            content.Children.Add(new TextBlock
            {
                Text = $"Namespace: {entity.RoslynSymbol.ContainingNamespace}",
                FontSize = 11,
                Foreground = Brushes.Gray
            });
        }

        if (entity.Children.Any())
        {
            content.Children.Add(new TextBlock
            {
                Text = $"Children: {entity.Children.Count}",
                FontSize = 11,
                Foreground = Brushes.Gray
            });
        }

        _detailsPanel.Child = new ScrollViewer
        {
            Content = content,
            Padding = new Thickness(5)
        };
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var searchTerm = _searchBox.Text?.ToLower() ?? "";
        var filterType = _typeFilter.SelectedItem?.ToString() ?? "All";

        IEnumerable<EnvironmentEntity> filtered = _entities;

        if (!string.IsNullOrEmpty(searchTerm))
        {
            filtered = filtered.Where(e =>
                e.Name.ToLower().Contains(searchTerm) ||
                (e.Description?.ToLower().Contains(searchTerm) ?? false));
        }

        if (filterType != "All")
        {
            var entityType = filterType.Replace(" ", "") switch
            {
                "CodeSymbol" => EntityType.CodeSymbol,
                "File" => EntityType.File,
                "DataValue" => EntityType.DataValue,
                "Folder" => EntityType.Folder,
                "Project" => EntityType.Project,
                _ => EntityType.CodeSymbol
            };
            filtered = filtered.Where(e => e.Type == entityType);
        }

        var itemsSource = filtered.ToList();
        _entityTree.ItemsSource = itemsSource;
        UpdateStatus($"Showing {itemsSource.Count} of {_entities.Count} entities");
    }

    #endregion

    #region Status Updates

    private void UpdateStatus(string message, bool isError = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _statusText.Text = message;
            _statusText.Foreground = isError ? Brushes.Red : Brushes.Gray;

            Task.Delay(5000).ContinueWith(_ =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_statusText.Text == message)
                        _statusText.Foreground = Brushes.Gray;
                });
            });
        });
    }

    #endregion

    #region Public API

    public void AddEntity(EnvironmentEntity entity)
    {
        _entities.Add(entity);
        UpdateStatus($"Added: {entity.Name}");
    }

    public void RemoveEntity(EnvironmentEntity entity)
    {
        _entities.Remove(entity);
        UpdateStatus($"Removed: {entity.Name}");
    }

    public IEnumerable<EnvironmentEntity> GetEntitiesByType(EntityType type) =>
        _entities.Where(e => e.Type == type);

    public EnvironmentEntity? FindEntity(string name) =>
        _entities.FirstOrDefault(e => e.Name == name);

    #endregion
}