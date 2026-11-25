using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using System.Text;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Avalonia.Styling;

namespace CAAI;

public class FileSystemNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _hasLoaded = false;
    private string _name;

    public string Name 
    { 
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }
    
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public ObservableCollection<FileSystemNode> Children { get; } = new();
    
    public bool IsExpanded
    {
        get => _isExpanded;
        set 
        { 
            _isExpanded = value;
            OnPropertyChanged();
            if (value && IsDirectory && !_hasLoaded)
                LoadChildren();
        }
    }
    
    public FileSystemNode(string path, bool isDirectory)
    {
        FullPath = path;
        IsDirectory = isDirectory;
        _name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(_name)) _name = path;

        if (IsDirectory)
        {
            Children.Add(null!); // Dummy child to show expander
        }
    }

    public void LoadChildren()
    {
        if (!IsDirectory || _hasLoaded) return;

        try
        {
            Children.Clear();
            foreach (var dir in Directory.GetDirectories(FullPath))
                Children.Add(new FileSystemNode(dir, true));
            foreach (var file in Directory.GetFiles(FullPath))
                Children.Add(new FileSystemNode(file, false));
        }
        catch { /* Handle permissions errors */ }
        finally { _hasLoaded = true; }
    }

    public void Refresh()
    {
        _hasLoaded = false;
        Children.Clear();
        if (IsDirectory)
            Children.Add(null!);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class FileExplorerWidget : UserControl
{
    private readonly TreeView _treeView;
    private FileSystemNode _rootNode;
    
    public event Action<string>? OnFileOpened;
    public event Action<string>? OnFileCreated;
    public event Action<string>? OnFileDeleted;
    public event Action<string, string>? OnFileRenamed;

    public string BaseDir;

    public FileExplorerWidget(string rootPath)
    {
        BaseDir = rootPath;
        _rootNode = new FileSystemNode(rootPath, true);
    
        _treeView = new TreeView
        {
            Margin = new Thickness(0),
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40)),
            Foreground = Brushes.White,
            ItemsSource = new[] { _rootNode },
            ItemTemplate = CreateTemplate()
        };
        _treeView.Styles.Add(new Style(selector => selector.OfType<TreeViewItem>().Class(":selected"))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, new SolidColorBrush(Color.FromRgb(42, 68, 64))),
                new Setter(TemplatedControl.ForegroundProperty, Brushes.White)
            }
        });
    
        // Handle expansion to load children
        _treeView.AddHandler(TreeViewItem.ExpandedEvent, OnTreeViewItemExpanded);
    
        _treeView.KeyDown += OnTreeKeyDown;
    
        Content = _treeView;
    }

    private void OnTreeViewItemExpanded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem treeItem && treeItem.DataContext is FileSystemNode node)
        {
            if (node.IsDirectory && !node.Children.Any(c => c != null))
            {
                node.LoadChildren();
                // Force refresh of the tree item
                treeItem.ItemsSource = node.Children;
            }
        }
    }

    private IDataTemplate CreateTemplate()
    {
        return new FuncTreeDataTemplate<FileSystemNode>(
            match: _ => true,
            itemsSelector: n => n.Children,
            build: (node, _) => CreateNodeUI(node)
        );
    }

    private Control CreateNodeUI(FileSystemNode node)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(2),
            Background = Brushes.Transparent
        };

        // Icon
        var icon = new TextBlock
        {
            Text = GetFileIcon(node),
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Name
        var name = new TextBlock
        {
            Text = node.Name,
            Foreground = node.IsDirectory ? Brushes.LightSkyBlue : Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12
        };

        // Size info (for files)
        if (!node.IsDirectory && File.Exists(node.FullPath))
        {
            var fileInfo = new FileInfo(node.FullPath);
            var sizeText = FormatFileSize(fileInfo.Length);
            var sizeLabel = new TextBlock
            {
                Text = sizeText,
                FontSize = 9,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            grid.Children.Add(sizeLabel);
            Grid.SetColumn(sizeLabel, 2);
        }

        grid.Children.Add(icon);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(name);
        Grid.SetColumn(name, 1);

        // Hover effect
        grid.PointerEntered += (_, _) =>
        {
            grid.Background = new SolidColorBrush(Color.FromArgb(50, 70, 130, 120));
        };
        grid.PointerExited += (_, _) => grid.Background = Brushes.Transparent;

        // Only handle double-click for files (not directories)
        grid.DoubleTapped += (_, e) =>
        {
            if (!node.IsDirectory)
            {
                OnFileOpened?.Invoke(node.FullPath);
                e.Handled = true;
            }
            // For directories, let the TreeView handle expansion via the built-in expander
        };

        // Context menu
        grid.ContextMenu = CreateContextMenu(node);
        grid.PointerPressed += (_, e) =>
        {
            if (e.Pointer.Type == PointerType.Mouse && e.GetCurrentPoint(grid).Properties.IsRightButtonPressed)
                _treeView.SelectedItem = node;
        };

        return grid;
    }

    private string GetFileIcon(FileSystemNode node)
    {
        if (node.IsDirectory) return "📁";
        
        var ext = Path.GetExtension(node.Name).ToLower();
        return ext switch
        {
            ".cs" => "📘",
            ".csproj" => "🎯",
            ".sln" => "🏗️",
            ".json" => "📋",
            ".xml" => "📰",
            ".txt" => "📄",
            ".md" => "📝",
            ".png" or ".jpg" or ".jpeg" or ".gif" => "🖼️",
            ".exe" or ".dll" => "⚙️",
            _ => "📄"
        };
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private ContextMenu CreateContextMenu(FileSystemNode node)
    {
        var menu = new ContextMenu();

        // New submenu
        var newMenu = new MenuItem { Header = "🆕 New" };
        
        var newFile = new MenuItem { Header = "📄 C# Class" };
        newFile.Click += async (_, _) => await CreateNewClass(node);
        
        var newFolder = new MenuItem { Header = "📁 Folder" };
        newFolder.Click += async (_, _) => await CreateNewFolder(node);
        
        var newInterface = new MenuItem { Header = "🔷 Interface" };
        newInterface.Click += async (_, _) => await CreateNewInterface(node);
        
        var newStruct = new MenuItem { Header = "📗 Struct" };
        newStruct.Click += async (_, _) => await CreateNewStruct(node);
        
        var newEnum = new MenuItem { Header = "🔢 Enum" };
        newEnum.Click += async (_, _) => await CreateNewEnum(node);
        
        newMenu.Items.Add(newFile);
        newMenu.Items.Add(newFolder);
        newMenu.Items.Add(newInterface);
        newMenu.Items.Add(newStruct);
        newMenu.Items.Add(newEnum);
        
        menu.Items.Add(newMenu);
        menu.Items.Add(new Separator());

        // Open
        var openItem = new MenuItem { Header = "📂 Open" };
        openItem.Click += (_, _) =>
        {
            if (!node.IsDirectory)
                OnFileOpened?.Invoke(node.FullPath);
            else
                node.IsExpanded = !node.IsExpanded;
        };
        menu.Items.Add(openItem);

        // Open containing folder (for files)
        if (!node.IsDirectory)
        {
            var openFolderItem = new MenuItem { Header = "📁 Open Containing Folder" };
            openFolderItem.Click += (_, _) => OpenContainingFolder(node.FullPath);
            menu.Items.Add(openFolderItem);
        }

        // Rename
        var renameItem = new MenuItem { Header = "✏️ Rename" };
        renameItem.Click += async (_, _) => await RenameNode(node);
        menu.Items.Add(renameItem);

        // Delete
        var deleteItem = new MenuItem { Header = "🗑 Delete", Foreground = Brushes.Red };
        deleteItem.Click += async (_, _) => await DeleteNode(node);
        menu.Items.Add(deleteItem);
        menu.Items.Add(new Separator());

        // Copy path
        var copyPathItem = new MenuItem { Header = "📋 Copy Path" };
        copyPathItem.Click += async (_, _) => await CopyPath(node.FullPath);
        menu.Items.Add(copyPathItem);

        // Properties
        var propertiesItem = new MenuItem { Header = "ℹ️ Properties" };
        propertiesItem.Click += async (_, _) => await ShowProperties(node);
        menu.Items.Add(propertiesItem);

        // Refresh (for directories)
        if (node.IsDirectory)
        {
            menu.Items.Add(new Separator());
            var refreshItem = new MenuItem { Header = "🔄 Refresh" };
            refreshItem.Click += (_, _) => RefreshNode(node);
            menu.Items.Add(refreshItem);
        }

        return menu;
    }

    private async Task CreateNewClass(FileSystemNode parentNode)
    {
        var targetDir = parentNode.IsDirectory ? parentNode.FullPath : Path.GetDirectoryName(parentNode.FullPath);
        if (string.IsNullOrEmpty(targetDir)) return;
        if (GetOwnerWindow() is not { } owner) return;

        var dialog = new CreateFileDialog(targetDir, "Class", GetClassTemplate());
        var result = await dialog.ShowDialog(owner);
    
        if (!string.IsNullOrEmpty(result))
        {
            RefreshNode(parentNode);
            OnFileCreated?.Invoke(result);
            OnFileOpened?.Invoke(result);
        }
    }

// Similarly for other Create methods

    private async Task CreateNewFolder(FileSystemNode parentNode)
    {
        var targetDir = parentNode.IsDirectory ? parentNode.FullPath : Path.GetDirectoryName(parentNode.FullPath);
        if (string.IsNullOrEmpty(targetDir)) return;
        if (GetOwnerWindow() is not { } owner) return;

        var dialog = new InputDialog("New Folder", "Enter folder name:", "NewFolder");
        var result = await dialog.ShowDialog(owner);
    
        if (!string.IsNullOrEmpty(result))
        {
            var newPath = Path.Combine(targetDir, result);
            Directory.CreateDirectory(newPath);
            RefreshNode(parentNode);
        }
    }

    private async Task CreateNewInterface(FileSystemNode parentNode)
    {
        var targetDir = parentNode.IsDirectory ? parentNode.FullPath : Path.GetDirectoryName(parentNode.FullPath);
        if (string.IsNullOrEmpty(targetDir)) return;
        if (GetOwnerWindow() is not { } owner) return;

        var dialog = new CreateFileDialog(targetDir, "Interface", GetInterfaceTemplate());
        var result = await dialog.ShowDialog(owner);
        
        if (!string.IsNullOrEmpty(result))
        {
            RefreshNode(parentNode);
            OnFileCreated?.Invoke(result);
            OnFileOpened?.Invoke(result);
        }
    }

    private async Task CreateNewStruct(FileSystemNode parentNode)
    {
        var targetDir = parentNode.IsDirectory ? parentNode.FullPath : Path.GetDirectoryName(parentNode.FullPath);
        if (string.IsNullOrEmpty(targetDir)) return;
        if (GetOwnerWindow() is not { } owner) return;

        var dialog = new CreateFileDialog(targetDir, "Struct", GetStructTemplate());
        var result = await dialog.ShowDialog(owner);
        
        if (!string.IsNullOrEmpty(result))
        {
            RefreshNode(parentNode);
            OnFileCreated?.Invoke(result);
            OnFileOpened?.Invoke(result);
        }
    }

    private async Task CreateNewEnum(FileSystemNode parentNode)
    {
        var targetDir = parentNode.IsDirectory ? parentNode.FullPath : Path.GetDirectoryName(parentNode.FullPath);
        if (string.IsNullOrEmpty(targetDir)) return;
        if (GetOwnerWindow() is not { } owner) return;

        var dialog = new CreateFileDialog(targetDir, "Enum", GetEnumTemplate());
        var result = await dialog.ShowDialog(owner);
        
        if (!string.IsNullOrEmpty(result))
        {
            RefreshNode(parentNode);
            OnFileCreated?.Invoke(result);
            OnFileOpened?.Invoke(result);
        }
    }

    private async Task RenameNode(FileSystemNode node)
    {
        if (GetOwnerWindow() is not { } owner) return;
        var dialog = new InputDialog("Rename", "Enter new name:", node.Name);
        var newName = await dialog.ShowDialog(owner);
        
        if (!string.IsNullOrEmpty(newName) && newName != node.Name)
        {
            try
            {
                var newPath = Path.Combine(Path.GetDirectoryName(node.FullPath)!, newName);
                if (node.IsDirectory)
                    Directory.Move(node.FullPath, newPath);
                else
                    File.Move(node.FullPath, newPath);
                
                OnFileRenamed?.Invoke(node.FullPath, newPath);
                if (FindParentNode(node) is { } parent)
                    RefreshNode(parent);
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"Cannot rename: {ex.Message}");
            }
        }
    }

    private async Task DeleteNode(FileSystemNode node)
    {
        var result = await ShowConfirmDialog("Delete", 
            $"Are you sure you want to delete '{node.Name}'?\nThis action cannot be undone.");
        
        if (result)
        {
            try
            {
                if (node.IsDirectory)
                    Directory.Delete(node.FullPath, true);
                else
                    File.Delete(node.FullPath);
                
                OnFileDeleted?.Invoke(node.FullPath);
                if (FindParentNode(node) is { } parent)
                    RefreshNode(parent);
            }
            catch (Exception ex)
            {
                await ShowErrorDialog($"Cannot delete: {ex.Message}");
            }
        }
    }

    private void OpenContainingFolder(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
        {
            System.Diagnostics.Process.Start("explorer.exe", folder);
        }
    }

    private async Task CopyPathNoConfirmation(string path)
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window?.Clipboard != null)
        {
            await window.Clipboard.SetTextAsync(path);
        }
    }
    
    private async Task CopyPath(string path)
    {
        // Get the top-level window and access clipboard
        var window = TopLevel.GetTopLevel(this) as Window;
        if (window != null && window.Clipboard != null)
        {
            await window.Clipboard.SetTextAsync(path);
        
            // Optional: Show a brief confirmation
            var status = new TextBlock
            {
                Text = "✓ Path copied!",
                Foreground = Brushes.LightGreen,
                FontSize = 11,
                Margin = new Thickness(5),
                Opacity = 1
            };
        
            // Add temporary status message
            var parent = this.Parent as Panel;
            if (parent != null)
            {
                parent.Children.Add(status);
                await Task.Delay(1500);
                parent.Children.Remove(status);
            }
        }
    }

    private async Task ShowProperties(FileSystemNode node)
    {
        var properties = new StringBuilder();
        properties.AppendLine($"Name: {node.Name}");
        properties.AppendLine($"Path: {node.FullPath}");
        properties.AppendLine($"Type: {(node.IsDirectory ? "Folder" : "File")}");
        
        if (!node.IsDirectory && File.Exists(node.FullPath))
        {
            var info = new FileInfo(node.FullPath);
            properties.AppendLine($"Size: {FormatFileSize(info.Length)}");
            properties.AppendLine($"Created: {info.CreationTime}");
            properties.AppendLine($"Modified: {info.LastWriteTime}");
        }
        else if (node.IsDirectory)
        {
            var info = new DirectoryInfo(node.FullPath);
            properties.AppendLine($"Created: {info.CreationTime}");
            properties.AppendLine($"Contains: {info.GetFiles().Length} files, {info.GetDirectories().Length} folders");
        }

        await ShowInfoDialog("Properties", properties.ToString());
    }

    private void RefreshNode(FileSystemNode node)
    {
        if (node != null)
        {
            node.Refresh();
            node.IsExpanded = true;
        }
    }

    private FileSystemNode? FindParentNode(FileSystemNode child)
    {
        return FindParentNodeRecursive(_rootNode, child);
    }

    private FileSystemNode? FindParentNodeRecursive(FileSystemNode current, FileSystemNode target)
    {
        if (current.Children.Contains(target))
            return current;
        
        foreach (var child in current.Children.Where(c => c != null && c.IsDirectory))
        {
            var result = FindParentNodeRecursive(child, target);
            if (result != null)
                return result;
        }
        return null;
    }

    private async void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (_treeView.SelectedItem is FileSystemNode node)
        {
            if (e.Key == Key.F2)
                await RenameNode(node);
            else if (e.Key == Key.Delete)
                await DeleteNode(node);
            else if (e.Key == Key.Enter && !node.IsDirectory)
                OnFileOpened?.Invoke(node.FullPath);
        }
    }

    private string GetClassTemplate() => 
        @"using System;

namespace {{Namespace}}
{
    public class {{ClassName}}
    {
        public {{ClassName}}()
        {
        }
    }
}";

    private string GetInterfaceTemplate() =>
        @"using System;

namespace {{Namespace}}
{
    public interface I{{InterfaceName}}
    {
    }
}";

    private string GetStructTemplate() =>
        @"using System;

namespace {{Namespace}}
{
    public struct {{StructName}}
    {
        public {{StructName}}()
        {
        }
    }
}";

    private string GetEnumTemplate() =>
        @"using System;

namespace {{Namespace}}
{
    public enum {{EnumName}}
    {
        None,
        First,
        Second,
        Third
    }
}";

    private async Task<bool> ShowConfirmDialog(string title, string message)
    {
        if (GetOwnerWindow() is not { } owner) return false;
        var dialog = new ConfirmDialog(title, message);
        var result = await dialog.ShowDialog(owner);
        return result ?? false;
    }

    private async Task ShowErrorDialog(string message)
    {
        if (GetOwnerWindow() is not { } owner) return;
        var dialog = new MessageDialog("Error", message, true);
        await dialog.ShowDialog(owner);
    }

    private async Task ShowInfoDialog(string title, string message)
    {
        if (GetOwnerWindow() is not { } owner) return;
        var dialog = new MessageDialog(title, message, false);
        await dialog.ShowDialog(owner);
    }

    private Window? GetOwnerWindow() => TopLevel.GetTopLevel(this) as Window;

    public void RefreshAll()
    {
        _rootNode.Refresh();
    }

    public void SetRootPath(string rootPath)
    {
        BaseDir = Path.GetFullPath(rootPath);
        _rootNode = new FileSystemNode(BaseDir, true);
        _treeView.ItemsSource = new[] { _rootNode };
    }
}

// Helper Dialogs
// Fixed InputDialog
public class InputDialog : Window
{
    private TextBox _inputBox;
    private string _result = "";

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        Title = title;
        Width = 400;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(35, 35, 40));

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        
        panel.Children.Add(new TextBlock { Text = prompt, Foreground = Brushes.White });
        
        _inputBox = new TextBox 
        { 
            Text = defaultValue,
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
            Foreground = Brushes.White
        };
        panel.Children.Add(_inputBox);
        
        var buttonPanel = new StackPanel 
        { 
            Orientation = Orientation.Horizontal, 
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        
        var okBtn = new Button { Content = "OK", Background = new SolidColorBrush(Color.FromRgb(0, 100, 0)), Foreground = Brushes.White };
        okBtn.Click += (_, _) => { _result = _inputBox.Text; Close(); };
        
        var cancelBtn = new Button { Content = "Cancel", Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)), Foreground = Brushes.White };
        cancelBtn.Click += (_, _) => { _result = ""; Close(); };
        
        buttonPanel.Children.Add(okBtn);
        buttonPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonPanel);
        
        Content = panel;
        
        _inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _result = _inputBox.Text; Close(); }
            else if (e.Key == Key.Escape) Close();
        };
    }

    public new async Task<string?> ShowDialog(Window parent)
    {
        await base.ShowDialog(parent);
        return string.IsNullOrEmpty(_result) ? null : _result;
    }
}

// Fixed ConfirmDialog
public class ConfirmDialog : Window
{
    private bool _result = false;

    public ConfirmDialog(string title, string message)
    {
        Title = title;
        Width = 400;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(35, 35, 40));

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        
        panel.Children.Add(new TextBlock { Text = message, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        
        var buttonPanel = new StackPanel 
        { 
            Orientation = Orientation.Horizontal, 
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        
        var yesBtn = new Button { Content = "Yes", Background = new SolidColorBrush(Color.FromRgb(0, 100, 0)), Foreground = Brushes.White };
        yesBtn.Click += (_, _) => { _result = true; Close(); };
        
        var noBtn = new Button { Content = "No", Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)), Foreground = Brushes.White };
        noBtn.Click += (_, _) => { _result = false; Close(); };
        
        buttonPanel.Children.Add(yesBtn);
        buttonPanel.Children.Add(noBtn);
        panel.Children.Add(buttonPanel);
        
        Content = panel;
    }

    public new async Task<bool?> ShowDialog(Window parent)
    {
        await base.ShowDialog(parent);
        return _result;
    }
}

// Fixed MessageDialog
public class MessageDialog : Window
{
    public MessageDialog(string title, string message, bool isError)
    {
        Title = title;
        Width = 400;
        Height = 150;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(35, 35, 40));

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        
        panel.Children.Add(new TextBlock 
        { 
            Text = message, 
            Foreground = isError ? Brushes.Red : Brushes.White,
            TextWrapping = TextWrapping.Wrap 
        });
        
        var buttonPanel = new StackPanel 
        { 
            Orientation = Orientation.Horizontal, 
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        
        var okBtn = new Button { Content = "OK", Background = new SolidColorBrush(Color.FromRgb(0, 100, 0)), Foreground = Brushes.White };
        okBtn.Click += (_, _) => Close();
        
        buttonPanel.Children.Add(okBtn);
        panel.Children.Add(buttonPanel);
        
        Content = panel;
    }

    public new async Task ShowDialog(Window parent)
    {
        await base.ShowDialog(parent);
    }
}

// Fixed CreateFileDialog
public class CreateFileDialog : Window
{
    private TextBox _nameBox;
    private TextBox _namespaceBox;
    private string _result = "";

    public CreateFileDialog(string directory, string type, string template)
    {
        Title = $"Create New {type}";
        Width = 450;
        Height = 250;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(35, 35, 40));

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        
        panel.Children.Add(new TextBlock { Text = $"{type} Name:", Foreground = Brushes.White });
        _nameBox = new TextBox 
        { 
            Watermark = $"Enter {type.ToLower()} name...",
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
            Foreground = Brushes.White
        };
        panel.Children.Add(_nameBox);
        
        panel.Children.Add(new TextBlock { Text = "Namespace:", Foreground = Brushes.White });
        _namespaceBox = new TextBox 
        { 
            Text = GetDefaultNamespace(directory),
            Background = new SolidColorBrush(Color.FromRgb(50, 50, 55)),
            Foreground = Brushes.White
        };
        panel.Children.Add(_namespaceBox);
        
        var buttonPanel = new StackPanel 
        { 
            Orientation = Orientation.Horizontal, 
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        
        var createBtn = new Button { Content = "Create", Background = new SolidColorBrush(Color.FromRgb(0, 100, 0)), Foreground = Brushes.White };
        createBtn.Click += (_, _) =>
        {
            var fileName = _nameBox.Text;
            if (!string.IsNullOrEmpty(fileName))
            {
                var namespaceName = string.IsNullOrEmpty(_namespaceBox.Text) ? "MyProject" : _namespaceBox.Text;
                var content = template
                    .Replace("{{Namespace}}", namespaceName)
                    .Replace("{{ClassName}}", fileName)
                    .Replace("{{InterfaceName}}", fileName)
                    .Replace("{{StructName}}", fileName)
                    .Replace("{{EnumName}}", fileName);
                
                var filePath = Path.Combine(directory, $"{fileName}.cs");
                File.WriteAllText(filePath, content);
                _result = filePath;
                Close();
            }
        };
        
        var cancelBtn = new Button { Content = "Cancel", Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)), Foreground = Brushes.White };
        cancelBtn.Click += (_, _) => Close();
        
        buttonPanel.Children.Add(createBtn);
        buttonPanel.Children.Add(cancelBtn);
        panel.Children.Add(buttonPanel);
        
        Content = panel;
    }

    private string GetDefaultNamespace(string directory)
    {
        var csproj = Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (csproj != null)
            return Path.GetFileNameWithoutExtension(csproj);
        
        return Path.GetFileName(directory).Replace(" ", "");
    }

    public new async Task<string?> ShowDialog(Window parent)
    {
        await base.ShowDialog(parent);
        return string.IsNullOrEmpty(_result) ? null : _result;
    }
}