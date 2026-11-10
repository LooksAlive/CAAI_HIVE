using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;

namespace CAAI;

public class SettingsData
{
    private static readonly SettingsData _instance = new();
    public static SettingsData Instance => _instance;


    // Editor settings
    public string FontFamily { get; set; } = "Consolas";
    public double FontSize { get; set; } = 14;
    public bool ShowLineNumbers { get; set; } = true;
    public bool WordWrap { get; set; } = false;
    public int IndentationSize { get; set; } = 4;
    public bool ConvertTabsToSpaces { get; set; } = false;
    
    // Theme settings
    public string Theme { get; set; } = "Dark";
    public string EditorBackground { get; set; } = "#1E1E1E";
    public string EditorForeground { get; set; } = "#D4D4D4";
    
    // Build settings
    public string BuildOutputPath { get; set; } = "./bin/Debug/";
    public bool ShowBuildErrors { get; set; } = true;
    public bool AutoBuildOnSave { get; set; } = false;
    
    // Notebook settings
    public string NotebookDefaultPath { get; set; } = "./notebooks/";
    public bool AutoRunCells { get; set; } = false;
    
    // General settings
    public bool CheckForUpdates { get; set; } = true;
    public bool SendTelemetry { get; set; } = false;
    public string Language { get; set; } = "en-US";
    public string? RecentProjects { get; set; }

    public event EventHandler? SettingsChanged;


    public void ApplySettings()
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }


    // -------------------------------------------------------------------------
    public const string ExportPath = "./settings.json";
    // default if not loaded
    
    public void Save(string path = ExportPath)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(path, json);
    }
    
    public SettingsData Load(string path)
    {
        if (!File.Exists(path))
            return Instance;
        
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SettingsData>(json) ?? Instance;
        }
        catch
        {
            return Instance;
        }
    }

    public void CopyFrom(SettingsData other)
    {
        var properties = typeof(SettingsData).GetProperties();

        foreach (var property in properties)
        {
            if (property.CanRead && property.CanWrite)
            {
                property.SetValue(this, property.GetValue(other));
            }
        }
    }

    public void RestoreDefaults()
    {
        CopyFrom(new SettingsData());
        ApplySettings();
    }
}

public class SettingsDialog : Window
{
    private SettingsData _settings;
    private TabControl _tabControl = null!;
    private readonly string _settingsPath;

    public SettingsDialog(SettingsData settings, string settingsPath)
    {
        _settings = settings ?? SettingsData.Instance;
        _settingsPath = settingsPath;
        
        Title = "Settings";
        Width = 600;
        Height = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 35));
        MinWidth = 500;
        MinHeight = 400;
        
        BuildUI();
        LoadSettings();
        
        // Keyboard shortcuts
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
            //if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                //SaveSettings();
        };
    }

    private void BuildUI()
    {
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("*, Auto"),
            Margin = new Thickness(0)
        };

        _tabControl = new TabControl
        {
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            TabStripPlacement = Dock.Top,
            Margin = new Thickness(10, 10, 10, 5)
        };

        // Add tabs
        _tabControl.Items.Add(CreateEditorTab());
        _tabControl.Items.Add(CreateAppearanceTab());
        _tabControl.Items.Add(CreateBuildTab());
        _tabControl.Items.Add(CreateNotebookTab());
        _tabControl.Items.Add(CreateGeneralTab());

        mainGrid.Children.Add(_tabControl);
        Grid.SetRow(_tabControl, 0);

        // Button panel
        var buttonPanel = CreateButtonPanel();
        mainGrid.Children.Add(buttonPanel);
        Grid.SetRow(buttonPanel, 1);

        Content = mainGrid;
    }

    private TabItem CreateEditorTab()
    {
        var panel = new StackPanel { Spacing = 15, Margin = new Thickness(10) };

        // Font Family
        var fontPanel = CreateSettingRow("Font Family:", 
            new ComboBox
            {
                Width = 200,
                ItemsSource = new[] { "Consolas", "Cascadia Code", "Fira Code", "JetBrains Mono", "Monospace" },
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                Foreground = Brushes.White
            }, out var fontCombo);

        // Font Size
        var sizePanel = CreateSettingRow("Font Size:", 
            new NumericUpDown
            {
                Width = 80,
                Minimum = 8,
                Maximum = 32,
                Increment = 1,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                Foreground = Brushes.White
            }, out var sizeUpDown);

        // Show Line Numbers
        var lineNumbersPanel = CreateSettingRow("Show Line Numbers:", 
            new ToggleSwitch
            {
                OffContent = "Off",
                OnContent = "On",
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50))
            }, out var lineNumbersToggle);

        // Word Wrap
        var wordWrapPanel = CreateSettingRow("Word Wrap:", 
            new ToggleSwitch
            {
                OffContent = "Off",
                OnContent = "On",
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50))
            }, out var wordWrapToggle);

        // Indentation Size
        var indentPanel = CreateSettingRow("Indentation Size:", 
            new NumericUpDown
            {
                Width = 80,
                Minimum = 1,
                Maximum = 8,
                Increment = 1,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                Foreground = Brushes.White
            }, out var indentUpDown);

        // Convert Tabs to Spaces
        var tabsPanel = CreateSettingRow("Convert Tabs to Spaces:", 
            new ToggleSwitch
            {
                OffContent = "Off",
                OnContent = "On",
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50))
            }, out var tabsToggle);

        panel.Children.Add(fontPanel);
        panel.Children.Add(sizePanel);
        panel.Children.Add(lineNumbersPanel);
        panel.Children.Add(wordWrapPanel);
        panel.Children.Add(indentPanel);
        panel.Children.Add(tabsPanel);

        // Store references for loading/saving
        panel.Tag = new Dictionary<string, object>
        {
            ["FontCombo"] = fontCombo,
            ["SizeUpDown"] = sizeUpDown,
            ["LineNumbersToggle"] = lineNumbersToggle,
            ["WordWrapToggle"] = wordWrapToggle,
            ["IndentUpDown"] = indentUpDown,
            ["TabsToggle"] = tabsToggle
        };

        return new TabItem 
        { 
            Header = "📝 Editor", 
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
    }

    private TabItem CreateAppearanceTab()
    {
        var panel = new StackPanel { Spacing = 15, Margin = new Thickness(10) };

        // Theme
        var themePanel = CreateSettingRow("Theme:", 
            new ComboBox
            {
                Width = 150,
                ItemsSource = new[] { "Dark", "Light", "High Contrast" },
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                Foreground = Brushes.White
            }, out var themeCombo);

        // Editor Background
        var bgPanel = CreateSettingRow("Editor Background:", 
            new ColorPicker
            {
                Width = 150,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50))
            }, out var bgPicker);

        // Editor Foreground
        var fgPanel = CreateSettingRow("Editor Foreground:", 
            new ColorPicker
            {
                Width = 150,
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50))
            }, out var fgPicker);

        // Preview area
        var previewBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(15),
            Margin = new Thickness(0, 10, 0, 0),
            Child = new TextBlock
            {
                Text = "Code Preview\n  public class Example\n  {\n      private string _name;\n  }",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Normal
            }
        };

        panel.Children.Add(themePanel);
        panel.Children.Add(bgPanel);
        panel.Children.Add(fgPanel);
        panel.Children.Add(previewBorder);

        panel.Tag = new Dictionary<string, object>
        {
            ["ThemeCombo"] = themeCombo,
            ["BgPicker"] = bgPicker,
            ["FgPicker"] = fgPicker,
            ["Preview"] = previewBorder
        };

        return new TabItem 
        { 
            Header = "🎨 Appearance", 
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
    }

    private TabItem CreateBuildTab()
    {
        var panel = new StackPanel { Spacing = 15, Margin = new Thickness(10) };

        // Build Output Path with Browse
        var browsePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5
        };

        // Create TextBox directly for browse panel
        var pathBox = new TextBox
        {
            Width = 300,
            Watermark = "Enter build output path...",
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White
        };

        var browseBtn = new Button
        {
            Content = "Browse...",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 4)
        };
        browseBtn.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Build Output Folder",
                AllowMultiple = false
            });
            if (folders.Count > 0)
                pathBox.Text = folders[0].Path.LocalPath;
        };

        browsePanel.Children.Add(pathBox);
        browsePanel.Children.Add(browseBtn);

        // Add the browse panel as a labeled row
        var pathRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0)
        };
        var pathLabel = new TextBlock
        {
            Text = "Build Output Path:",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 150
        };
        pathRow.Children.Add(pathLabel);
        pathRow.Children.Add(browsePanel);
        panel.Children.Add(pathRow);

        // Show Build Errors
        var errorsPanel = CreateSettingRow("Show Build Errors:", 
            new ToggleSwitch
            {
                OffContent = "Off",
                OnContent = "On",
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50))
            }, out var errorsToggle);

        // Auto Build on Save
        var autoBuildPanel = CreateSettingRow("Auto Build on Save:", 
            new ToggleSwitch
            {
                OffContent = "Off",
                OnContent = "On",
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50))
            }, out var autoBuildToggle);

        panel.Children.Add(errorsPanel);
        panel.Children.Add(autoBuildPanel);

        // Store references for loading/saving
        panel.Tag = new Dictionary<string, object>
        {
            ["PathBox"] = pathBox,
            ["ErrorsToggle"] = errorsToggle,
            ["AutoBuildToggle"] = autoBuildToggle
        };

        return new TabItem 
        { 
            Header = "🔨 Build", 
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
    }

    private TabItem CreateNotebookTab()
    {
        var panel = new StackPanel { Spacing = 15, Margin = new Thickness(10) };

        // Default Path with Browse
        var browsePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5
        };

        var pathBox = new TextBox
        {
            Width = 300,
            Watermark = "Enter default notebook path...",
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White
        };

        var browseBtn = new Button
        {
            Content = "Browse...",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 4)
        };
        browseBtn.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Notebook Folder",
                AllowMultiple = false
            });
            if (folders.Count > 0)
                pathBox.Text = folders[0].Path.LocalPath;
        };

        browsePanel.Children.Add(pathBox);
        browsePanel.Children.Add(browseBtn);

        // Add the browse panel as a labeled row
        var pathRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0)
        };
        var pathLabel = new TextBlock
        {
            Text = "Notebook Default Path:",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 150
        };
        pathRow.Children.Add(pathLabel);
        pathRow.Children.Add(browsePanel);
        panel.Children.Add(pathRow);

        // Auto Run Cells
        var autoRunPanel = CreateSettingRow("Auto Run Cells:", 
            new ToggleSwitch
            {
                OffContent = "Off",
                OnContent = "On",
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50))
            }, out var autoRunToggle);

        panel.Children.Add(autoRunPanel);

        panel.Tag = new Dictionary<string, object>
        {
            ["PathBox"] = pathBox,
            ["AutoRunToggle"] = autoRunToggle
        };

        return new TabItem 
        { 
            Header = "📓 Notebook", 
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
    }
    
    private TabItem CreateGeneralTab()
    {
        var panel = new StackPanel { Spacing = 15, Margin = new Thickness(10) };

        // Language
        var langPanel = CreateSettingRow("Language:", 
            new ComboBox
            {
                Width = 150,
                ItemsSource = new[] { "en-US", "cs-CZ", "sk-SK", "de-DE", "fr-FR" },
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
                Foreground = Brushes.White
            }, out var langCombo);

        // Check for Updates
        var updatesPanel = CreateSettingRow("Check for Updates:", 
            new ToggleSwitch
            {
                OffContent = "Off",
                OnContent = "On",
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50))
            }, out var updatesToggle);

        // Send Telemetry
        var telemetryPanel = CreateSettingRow("Send Telemetry:", 
            new ToggleSwitch
            {
                OffContent = "Off",
                OnContent = "On",
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 50))
            }, out var telemetryToggle);

        // Clear Recent Projects
        var clearRecentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var clearRecentBtn = new Button
        {
            Content = "Clear Recent Projects",
            Background = new SolidColorBrush(Color.FromRgb(139, 0, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(15, 8)
        };
        clearRecentBtn.Click += (_, _) =>
        {
            _settings.RecentProjects = null;
        };
        clearRecentPanel.Children.Add(clearRecentBtn);

        panel.Children.Add(langPanel);
        panel.Children.Add(updatesPanel);
        panel.Children.Add(telemetryPanel);
        panel.Children.Add(clearRecentPanel);

        panel.Tag = new Dictionary<string, object>
        {
            ["LangCombo"] = langCombo,
            ["UpdatesToggle"] = updatesToggle,
            ["TelemetryToggle"] = telemetryToggle
        };

        return new TabItem 
        { 
            Header = "⚙️ General", 
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
    }

    private StackPanel CreateSettingRow(string label, Control control, out Control controlOut)
    {
        controlOut = control;
        
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0)
        };

        var labelBlock = new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 150
        };

        panel.Children.Add(labelBlock);
        panel.Children.Add(control);

        return panel;
    }

    private StackPanel CreateButtonPanel()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(10, 5, 10, 10)
        };

        var saveBtn = new Button
        {
            Content = "💾 Save",
            Background = new SolidColorBrush(Color.FromRgb(0, 120, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(20, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        saveBtn.Click += (_, _) => SaveSettings();

        var resetBtn = new Button
        {
            Content = "🔄 Reset to Default",
            Background = new SolidColorBrush(Color.FromRgb(100, 100, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(20, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        resetBtn.Click += (_, _) => SettingsData.Instance.RestoreDefaults();

        var cancelBtn = new Button
        {
            Content = "Cancel",
            Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(20, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        cancelBtn.Click += (_, _) => Close();

        panel.Children.Add(resetBtn);
        panel.Children.Add(cancelBtn);
        panel.Children.Add(saveBtn);

        return panel;
    }

    private void LoadSettings()
    {
        SettingsData.Instance.Load(_settingsPath);
        SettingsData.Instance.ApplySettings();
    }

    private void SaveSettings()
    {
        SettingsData.Instance.ApplySettings();
        SettingsData.Instance.Save(_settingsPath);
    }
}

// Extension method to easily show the settings dialog
public static class SettingsExtensions
{
    public static async Task<SettingsData?> ShowSettingsDialog(this Window parent, SettingsData settings, string settingsPath)
    {
        var dialog = new SettingsDialog(settings, settingsPath);
        var result = await dialog.ShowDialog<bool?>(parent);
        //return dialog.SettingsChanged != null ? settings : null;
        // on close invoke all settings changed
        //dialog.RaiseSettingsChanged(settings);
        return settings;
    }
}
