using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.CodeAnalysis;

namespace CAAI;

public class NuGetPackage : INotifyPropertyChanged
    {
        private bool _isInstalled;
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string Authors { get; set; } = "";
        public long Downloads { get; set; }

        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                _isInstalled = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    
public class NuGetPackageManager : UserControl
{
    private readonly AdhocWorkspace? _workspace;
    private readonly ObservableCollection<NuGetPackage> _packages = new();
    private TextBox _searchBox = null!;
    private ListBox _packageList = null!;
    private ProgressBar _progressBar = null!;
    private TextBlock _statusText = null!;
    private TextBox _versionBox = null!;
    private bool _isLoading;

    public event EventHandler<NuGetPackage>? PackageInstallRequested;
    public event EventHandler<NuGetPackage>? PackageUninstallRequested;
    public event EventHandler<string>? OutputAppended;

    public NuGetPackageManager(AdhocWorkspace? workspace = null)
    {
        _workspace = workspace;
        BuildUI();
        LoadInstalledPackages();
    }

    private void BuildUI()
    {
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto, Auto, Auto, *, Auto"),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
            Margin = new Thickness(10)
        };

        // Search panel
        var searchPanel = CreateSearchPanel();
        mainGrid.Children.Add(searchPanel);
        Grid.SetRow(searchPanel, 0);

        // Action panel
        var actionPanel = CreateActionPanel();
        mainGrid.Children.Add(actionPanel);
        Grid.SetRow(actionPanel, 1);

        // Progress bar
        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            IsVisible = false,
            Height = 20,
            Margin = new Thickness(0, 5)
        };
        mainGrid.Children.Add(_progressBar);
        Grid.SetRow(_progressBar, 2);

        // Package list
        _packageList = CreatePackageList();
        mainGrid.Children.Add(_packageList);
        Grid.SetRow(_packageList, 3);

        // Status bar
        var statusBar = CreateStatusBar();
        mainGrid.Children.Add(statusBar);
        Grid.SetRow(statusBar, 4);

        Content = mainGrid;
    }

    private StackPanel CreateSearchPanel()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Margin = new Thickness(0, 0, 0, 10)
        };

        _searchBox = new TextBox
        {
            Watermark = "Search packages (e.g., Newtonsoft.Json)...",
            Width = 350,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White
        };
        _searchBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
                SearchPackages();
        };

        var searchBtn = new Button
        {
            Content = "🔍 Search",
            Background = new SolidColorBrush(Color.FromRgb(0, 100, 150)),
            Foreground = Brushes.White,
            Padding = new Thickness(15, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        searchBtn.Click += (_, _) => SearchPackages();

        var refreshBtn = new Button
        {
            Content = "🔄 Refresh Installed",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(10, 0, 0, 0)
        };
        refreshBtn.Click += (_, _) => LoadInstalledPackages();

        panel.Children.Add(_searchBox);
        panel.Children.Add(searchBtn);
        panel.Children.Add(refreshBtn);

        return panel;
    }

    private StackPanel CreateActionPanel()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 0, 0, 10)
        };

        _versionBox = new TextBox
        {
            Watermark = "Version (e.g., 13.0.3)",
            Width = 150,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White
        };

        var installBtn = new Button
        {
            Content = "📦 Install",
            Background = new SolidColorBrush(Color.FromRgb(0, 120, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(15, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        installBtn.Click += InstallSelectedPackage;

        var uninstallBtn = new Button
        {
            Content = "🗑️ Uninstall",
            Background = new SolidColorBrush(Color.FromRgb(120, 0, 0)),
            Foreground = Brushes.White,
            Padding = new Thickness(15, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        uninstallBtn.Click += UninstallSelectedPackage;

        // Version dropdown with common versions
        var versionCombo = new ComboBox
        {
            Width = 100,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White,
            ItemsSource = new[] { "Latest", "6.0.0", "7.0.0", "8.0.0", "12.0.0", "13.0.3" }
        };
        versionCombo.SelectionChanged += (s, e) =>
        {
            if (versionCombo.SelectedItem is string version && version != "Latest")
                _versionBox.Text = version;
        };

        panel.Children.Add(new TextBlock 
        { 
            Text = "Version:", 
            Foreground = Brushes.Gray, 
            VerticalAlignment = VerticalAlignment.Center 
        });
        panel.Children.Add(versionCombo);
        panel.Children.Add(_versionBox);
        panel.Children.Add(installBtn);
        panel.Children.Add(uninstallBtn);

        return panel;
    }

    private ListBox CreatePackageList()
    {
        var list = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 25, 30)),
            Foreground = Brushes.White,
            ItemsSource = _packages,
            SelectionMode = SelectionMode.Single,
            Margin = new Thickness(0, 5)
        };

        list.ItemTemplate = new FuncDataTemplate<NuGetPackage>((pkg, _) =>
        {
            if (pkg == null)
                return null;

            var border = new Border
            {
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 1, 0, 1),
                Background = pkg.IsInstalled ? 
                    new SolidColorBrush(Color.FromArgb(40, 0, 200, 0)) : 
                    Brushes.Transparent
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto, Auto")
            };

            // Icon
            var icon = new TextBlock
            {
                Text = pkg.IsInstalled ? "✅" : "📦",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Package info
            var infoStack = new StackPanel { Margin = new Thickness(10, 0) };
            
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            headerPanel.Children.Add(new TextBlock
            {
                Text = pkg.Id,
                FontWeight = FontWeight.Bold,
                FontSize = 13,
                Foreground = Brushes.White
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = pkg.Version,
                FontSize = 11,
                Foreground = Brushes.Gray
            });
            headerPanel.Children.Add(new TextBlock
            {
                Text = $"📥 {pkg.Downloads:N0}",
                FontSize = 10,
                Foreground = Brushes.Gray
            });
            infoStack.Children.Add(headerPanel);

            infoStack.Children.Add(new TextBlock
            {
                Text = pkg.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 500
            });

            infoStack.Children.Add(new TextBlock
            {
                Text = $"by {pkg.Authors}",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(128, 128, 128)),
                FontStyle = FontStyle.Italic
            });

            // Badge
            if (pkg.IsInstalled)
            {
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0, 150, 0)),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(8, 2),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock
                {
                    Text = "INSTALLED",
                    FontSize = 9,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                };
                grid.Children.Add(badge);
                Grid.SetColumn(badge, 2);
            }

            grid.Children.Add(icon);
            Grid.SetColumn(icon, 0);
            grid.Children.Add(infoStack);
            Grid.SetColumn(infoStack, 1);

            // Hover effect
            border.PointerEntered += (_, _) =>
            {
                if (!pkg.IsInstalled)
                    border.Background = new SolidColorBrush(Color.FromArgb(50, 100, 100, 100));
            };
            border.PointerExited += (_, _) =>
            {
                if (!pkg.IsInstalled)
                    border.Background = Brushes.Transparent;
            };

            border.Child = grid;
            return border;
        });

        return list;
    }

    private Border CreateStatusBar()
    {
        var statusBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
            Padding = new Thickness(10, 5),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 5, 0, 0)
        };

        _statusText = new TextBlock
        {
            Text = "Ready",
            FontSize = 11,
            Foreground = Brushes.Gray
        };

        statusBar.Child = _statusText;
        return statusBar;
    }

    private async void SearchPackages()
    {
        var searchTerm = _searchBox.Text;
        if (string.IsNullOrWhiteSpace(searchTerm)) return;

        await SearchNuGetPackages(searchTerm);
    }

    private async Task SearchNuGetPackages(string searchTerm)
    {
        _isLoading = true;
        _progressBar.IsVisible = true;
        _statusText.Text = $"Searching for '{searchTerm}'...";
        _statusText.Foreground = Brushes.Yellow;
        _packages.Clear();

        try
        {
            // Simulate async search - replace with actual NuGet API
            var results = await Task.Run(() => SearchNuGetPackagesAsync(searchTerm));
            
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var pkg in results)
                    _packages.Add(pkg);
                
                _statusText.Text = $"Found {_packages.Count} packages matching '{searchTerm}'";
                _statusText.Foreground = Brushes.LightGreen;
                _progressBar.IsVisible = false;
                _isLoading = false;
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _statusText.Text = $"Error: {ex.Message}";
                _statusText.Foreground = Brushes.Red;
                _progressBar.IsVisible = false;
                _isLoading = false;
            });
        }
    }

    private void LoadInstalledPackages()
    {
        // Load packages from project (simplified - would read from .csproj)
        // For demo, we'll add some sample installed packages
        _packages.Clear();
        
        var installed = new[]
        {
            new NuGetPackage 
            { 
                Id = "Avalonia", 
                Version = "11.0.0", 
                Description = "Cross-platform UI framework for .NET",
                Authors = "Avalonia Team",
                Downloads = 5000000,
                IsInstalled = true
            },
            new NuGetPackage 
            { 
                Id = "Microsoft.CodeAnalysis.CSharp", 
                Version = "4.8.0", 
                Description = "C# compiler and code analysis APIs",
                Authors = "Microsoft",
                Downloads = 15000000,
                IsInstalled = true
            }
        };
        
        foreach (var pkg in installed)
            _packages.Add(pkg);

        _statusText.Text = $"Loaded {_packages.Count} installed packages";
        _statusText.Foreground = Brushes.Gray;
    }

    private async void InstallSelectedPackage(object? sender, RoutedEventArgs e)
    {
        if (_packageList.SelectedItem is not NuGetPackage package) return;
        if (package.IsInstalled)
        {
            _statusText.Text = $"{package.Id} is already installed";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        var version = _versionBox.Text;
        if (string.IsNullOrWhiteSpace(version))
            version = "latest";

        _isLoading = true;
        _progressBar.IsVisible = true;
        _statusText.Text = $"Installing {package.Id} version {version}...";
        _statusText.Foreground = Brushes.Yellow;

        try
        {
            // Simulate install - replace with actual NuGet install
            await Task.Delay(2000);
            
            package.IsInstalled = true;
            package.Version = version == "latest" ? "1.0.0" : version;
            
            _statusText.Text = $"✅ Successfully installed {package.Id}";
            _statusText.Foreground = Brushes.LightGreen;
            
            PackageInstallRequested?.Invoke(this, package);
            OutputAppended?.Invoke(this, $"Installed {package.Id} version {version}");
        }
        catch (Exception ex)
        {
            _statusText.Text = $"❌ Failed to install {package.Id}: {ex.Message}";
            _statusText.Foreground = Brushes.Red;
        }
        finally
        {
            _progressBar.IsVisible = false;
            _isLoading = false;
        }
    }

    private async void UninstallSelectedPackage(object? sender, RoutedEventArgs e)
    {
        if (_packageList.SelectedItem is not NuGetPackage package) return;
        if (!package.IsInstalled)
        {
            _statusText.Text = $"{package.Id} is not installed";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        // Confirm uninstall
        var confirm = await ShowConfirmDialog("Confirm Uninstall", 
            $"Are you sure you want to uninstall {package.Id}?");
        
        if (!confirm) return;

        _isLoading = true;
        _progressBar.IsVisible = true;
        _statusText.Text = $"Uninstalling {package.Id}...";
        _statusText.Foreground = Brushes.Yellow;

        try
        {
            // Simulate uninstall
            await Task.Delay(1500);
            
            package.IsInstalled = false;
            
            _statusText.Text = $"🗑️ Successfully uninstalled {package.Id}";
            _statusText.Foreground = Brushes.LightGreen;
            
            PackageUninstallRequested?.Invoke(this, package);
            OutputAppended?.Invoke(this, $"Uninstalled {package.Id}");
        }
        catch (Exception ex)
        {
            _statusText.Text = $"❌ Failed to uninstall {package.Id}: {ex.Message}";
            _statusText.Foreground = Brushes.Red;
        }
        finally
        {
            _progressBar.IsVisible = false;
            _isLoading = false;
        }
    }

    private async Task<bool> ShowConfirmDialog(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(35, 35, 40))
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = message, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap });
        
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        
        var result = false;
        
        var yesBtn = new Button { Content = "Yes", Background = new SolidColorBrush(Color.FromRgb(0, 100, 0)), Foreground = Brushes.White };
        yesBtn.Click += (_, _) => { result = true; dialog.Close(); };
        
        var noBtn = new Button { Content = "No", Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)), Foreground = Brushes.White };
        noBtn.Click += (_, _) => { result = false; dialog.Close(); };
        
        buttonPanel.Children.Add(yesBtn);
        buttonPanel.Children.Add(noBtn);
        panel.Children.Add(buttonPanel);
        dialog.Content = panel;
        
        await dialog.ShowDialog(this.GetVisualRoot() as Window ?? new Window());
        return result;
    }

    // Simulated NuGet search - replace with actual NuGet.Protocol API
    private Task<List<NuGetPackage>> SearchNuGetPackagesAsync(string term)
    {
        return Task.FromResult(new List<NuGetPackage>
        {
            new NuGetPackage { Id = "Newtonsoft.Json", Version = "13.0.3", Description = "Popular high-performance JSON framework for .NET", Authors = "James Newton-King", Downloads = 800000000, IsInstalled = false },
            new NuGetPackage { Id = "Avalonia", Version = "11.0.0", Description = "Cross-platform UI framework for .NET", Authors = "Avalonia Team", Downloads = 5000000, IsInstalled = false },
            new NuGetPackage { Id = "Microsoft.CodeAnalysis.CSharp", Version = "4.8.0", Description = "C# compiler and code analysis APIs", Authors = "Microsoft", Downloads = 15000000, IsInstalled = false },
            new NuGetPackage { Id = "Serilog", Version = "3.1.1", Description = "Simple .NET logging with fully-structured events", Authors = "Serilog Contributors", Downloads = 120000000, IsInstalled = false },
            new NuGetPackage { Id = "AutoMapper", Version = "12.0.0", Description = "Convention-based object-object mapper", Authors = "Jimmy Bogard", Downloads = 250000000, IsInstalled = false },
            new NuGetPackage { Id = "FluentValidation", Version = "11.8.0", Description = "Validation library for .NET", Authors = "Jeremy Skinner", Downloads = 80000000, IsInstalled = false },
            new NuGetPackage { Id = "Dapper", Version = "2.1.24", Description = "Simple object mapper for .NET", Authors = "Stack Overflow", Downloads = 150000000, IsInstalled = false },
            new NuGetPackage { Id = "MediatR", Version = "12.1.1", Description = "Mediator pattern implementation", Authors = "Jimmy Bogard", Downloads = 40000000, IsInstalled = false },
            new NuGetPackage { Id = "Polly", Version = "8.2.0", Description = "Resilience and transient-fault-handling library", Authors = "App vNext", Downloads = 60000000, IsInstalled = false }
        }.Where(p => p.Id.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                     p.Description.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList());
    }
}