using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;


namespace CAAI;

public class NuGetPackage : INotifyPropertyChanged
    {
        private bool _isInstalled;
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string Authors { get; set; } = "";
        public long? Downloads { get; set; }
        public string InstalledVersion { get; set; } = "";

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
    private readonly ObservableCollection<NuGetPackage> _packages = new();
    private readonly ObservableCollection<string> _projectFiles = new();
    private readonly Dictionary<string, string> _installedVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SourceRepository _repository;
    private TextBox _searchBox = null!;
    private ListBox _packageList = null!;
    private Border _packageDetailsPanel = null!;
    private TextBlock _packageDetailsTitle = null!;
    private TextBlock _packageDetailsText = null!;
    private TextBlock _packageDetailsMeta = null!;
    private ProgressBar _progressBar = null!;
    private TextBlock _statusText = null!;
    private TextBox _versionBox = null!;
    private ComboBox _projectBox = null!;
    private Button _searchButton = null!;
    private Button _refreshButton = null!;
    private Button _installButton = null!;
    private Button _uninstallButton = null!;
    private string? _projectFilePath;
    private bool _isLoading;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _detailsCancellation;

    public event EventHandler<NuGetPackage>? PackageInstallRequested;
    public event EventHandler<NuGetPackage>? PackageUninstallRequested;
    public event EventHandler<string>? OutputAppended;
    private readonly WindowNotificationManager _notificationManager;

    public NuGetPackageManager(WindowNotificationManager notificationManager, string? projectFolder = null)
    {
        _notificationManager = notificationManager;
        _repository = new SourceRepository(
            new PackageSource("https://api.nuget.org/v3/index.json"),
            Repository.Provider.GetCoreV3());
        BuildUI();
        SetProjectFolder(projectFolder);
    }

    private void BuildUI()
    {
        var mainGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto, Auto, Auto, Auto, *, Auto, Auto"),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35)),
            Margin = new Thickness(10)
        };

        var projectPanel = CreateProjectPanel();
        mainGrid.Children.Add(projectPanel);
        Grid.SetRow(projectPanel, 0);

        var searchPanel = CreateSearchPanel();
        mainGrid.Children.Add(searchPanel);
        Grid.SetRow(searchPanel, 1);

        // Action panel
        var actionPanel = CreateActionPanel();
        mainGrid.Children.Add(actionPanel);
        Grid.SetRow(actionPanel, 2);

        // Progress bar
        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            IsVisible = false,
            Height = 3,
            Margin = new Thickness(0, 2)
        };
        mainGrid.Children.Add(_progressBar);
        Grid.SetRow(_progressBar, 3);

        // Package list
        _packageList = CreatePackageList();
        mainGrid.Children.Add(_packageList);
        Grid.SetRow(_packageList, 4);

        _packageDetailsPanel = CreatePackageDetailsPanel();
        mainGrid.Children.Add(_packageDetailsPanel);
        Grid.SetRow(_packageDetailsPanel, 5);

        // Status bar
        var statusBar = CreateStatusBar();
        mainGrid.Children.Add(statusBar);
        Grid.SetRow(statusBar, 6);

        Content = mainGrid;
    }

    private StackPanel CreateProjectPanel()
    {
        _projectBox = new ComboBox
        {
            ItemsSource = _projectFiles,
            MinWidth = 250,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _projectBox.SelectionChanged += async (_, _) =>
        {
            _projectFilePath = _projectBox.SelectedItem as string;
            await LoadInstalledPackages();
        };

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 0, 0, 8)
        };
        panel.Children.Add(new TextBlock
        {
            Text = "Project:",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(_projectBox);
        return panel;
    }

    private Grid CreateSearchPanel()
    {
        var panel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto"),
            Margin = new Thickness(0, 0, 0, 10)
        };

        _searchBox = new TextBox
        {
            Watermark = "Search packages (e.g., Newtonsoft.Json)...",
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White,
            CaretBrush = Brushes.White
        };
        _searchBox.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Enter)
                SearchPackages();
        };

        _searchButton = new Button
        {
            Content = "🔍 Search",
            Background = new SolidColorBrush(Color.FromRgb(0, 100, 150)),
            Foreground = Brushes.White,
            Padding = new Thickness(15, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _searchButton.Click += (_, _) => SearchPackages();
        Grid.SetColumn(_searchButton, 1);

        _refreshButton = new Button
        {
            Content = "🔄 Refresh Installed",
            Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            Foreground = Brushes.White,
            Padding = new Thickness(10, 8),
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(6, 0, 0, 0)
        };
        _refreshButton.Click += async (_, _) => await LoadInstalledPackages();
        Grid.SetColumn(_refreshButton, 2);

        panel.Children.Add(_searchBox);
        panel.Children.Add(_searchButton);
        panel.Children.Add(_refreshButton);

        return panel;
    }

    private StackPanel CreateActionPanel()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 10)
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Version",
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center
        });

        _versionBox = new TextBox
        {
            Watermark = "Latest",
            Width = 125,
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 50)),
            Foreground = Brushes.White
        };

        _installButton = new Button
        {
            Content = "Install",
            Background = new SolidColorBrush(Color.FromRgb(28, 112, 93)),
            Foreground = Brushes.White,
            Padding = new Thickness(14, 6),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _installButton.Click += InstallSelectedPackage;

        _uninstallButton = new Button
        {
            Content = "Uninstall",
            Background = new SolidColorBrush(Color.FromRgb(126, 57, 57)),
            Foreground = Brushes.White,
            Padding = new Thickness(14, 6),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        _uninstallButton.Click += UninstallSelectedPackage;

        panel.Children.Add(_versionBox);
        panel.Children.Add(_installButton);
        panel.Children.Add(_uninstallButton);

        return panel;
    }

    private ListBox CreatePackageList()
    {
        var list = new ListBox
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 25, 30)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemsSource = _packages,
            SelectionMode = SelectionMode.Single
        };
        list.SelectionChanged += (_, _) =>
        {
            UpdateActionButtons();
            _ = LoadSelectedPackageDetailsAsync();
        };

        list.ItemTemplate = new FuncDataTemplate<NuGetPackage>((pkg, _) =>
        {
            if (pkg == null)
                return null;

            var border = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(4, 3)
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto, Auto"),
                MinHeight = 28
            };

            var name = new TextBlock
            {
                Text = pkg.Id,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };

            var version = new TextBlock
            {
                Text = pkg.IsInstalled && !string.IsNullOrEmpty(pkg.InstalledVersion)
                    ? $"v{pkg.Version} / installed {pkg.InstalledVersion}"
                    : $"v{pkg.Version}",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(175, 180, 188)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0),
                MaxWidth = 240,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(version, 1);

            var downloads = new TextBlock
            {
                Text = pkg.Downloads.HasValue ? $"{pkg.Downloads:N0} downloads" : "",
                FontSize = 10,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0),
                MaxWidth = 130,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(downloads, 2);

            var status = new TextBlock
            {
                Text = pkg.IsInstalled ? "INSTALLED" : "AVAILABLE",
                FontSize = 9,
                FontWeight = FontWeight.Bold,
                Foreground = pkg.IsInstalled
                    ? new SolidColorBrush(Color.FromRgb(133, 214, 174))
                    : new SolidColorBrush(Color.FromRgb(159, 166, 177)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 2, 0)
            };
            Grid.SetColumn(status, 3);

            grid.Children.Add(name);
            grid.Children.Add(version);
            grid.Children.Add(downloads);
            grid.Children.Add(status);
            border.Child = grid;
            return border;
        });

        return list;
    }

    private Border CreatePackageDetailsPanel()
    {
        _packageDetailsTitle = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        };
        _packageDetailsText = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(190, 194, 201)),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 38
        };
        _packageDetailsMeta = new TextBlock
        {
            FontSize = 10,
            Foreground = Brushes.Gray
        };

        var content = new StackPanel { Spacing = 3 };
        content.Children.Add(_packageDetailsTitle);
        content.Children.Add(_packageDetailsText);
        content.Children.Add(_packageDetailsMeta);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(55, 55, 62)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(6, 8, 6, 4),
            IsVisible = false,
            Child = content
        };
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
        if (_isLoading)
            return;

        var searchTerm = _searchBox.Text;
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            _statusText.Text = "Enter a package name or keyword to search.";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        await SearchNuGetPackages(searchTerm);
    }

    public string? ProjectFilePath => _projectFilePath;

    public void SetProjectFolder(string? folderPath)
    {
        _projectFiles.Clear();
        _projectFilePath = null;

        if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath))
        {
            try
            {
                foreach (var projectFile in Directory.EnumerateFiles(
                             folderPath, "*.csproj", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    _projectFiles.Add(Path.GetFullPath(projectFile));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _statusText.Text = $"Could not inspect project folder: {ex.Message}";
                _statusText.Foreground = Brushes.Red;
                return;
            }
        }

        if (_projectFiles.Count == 1)
        {
            _projectBox.SelectedItem = _projectFiles[0];
            return;
        }

        _projectBox.SelectedItem = null;
        _installButton.IsEnabled = false;
        _uninstallButton.IsEnabled = false;
        _refreshButton.IsEnabled = false;
        _packages.Clear();
        _installedVersions.Clear();

        _statusText.Text = _projectFiles.Count == 0
            ? "No .csproj found. Select a project folder to manage packages."
            : "Select a .csproj to manage its packages.";
        _statusText.Foreground = Brushes.Gray;
    }

    public async Task InstallPackage(string project,
                                 string id,
                                 string version)
    {
        var arguments = new List<string> { "add", Path.GetFullPath(project), "package", id };
        if (!string.IsNullOrWhiteSpace(version) && !string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase))
        {
            if (!NuGetVersion.TryParse(version, out _))
                throw new ArgumentException($"'{version}' is not a valid NuGet version.", nameof(version));

            arguments.Add("--version");
            arguments.Add(version);
        }

        await RunDotnetAsync(Path.GetDirectoryName(Path.GetFullPath(project))!, arguments);
    }

    public async Task<List<NuGetPackage>> GetInstalledPackages(string projectFile)
    {
        return await Task.Run(() => ReadInstalledPackages(projectFile));
    }

    private static List<NuGetPackage> ReadInstalledPackages(string projectFile)
    {
        var projectPath = Path.GetFullPath(projectFile);
        var projectDocument = XDocument.Load(projectPath);
        var centralVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);

        while (directory is not null)
        {
            var centralFile = Path.Combine(directory.FullName, "Directory.Packages.props");
            if (File.Exists(centralFile))
            {
                foreach (var packageVersion in XDocument.Load(centralFile).Descendants()
                             .Where(element => element.Name.LocalName == "PackageVersion"))
                {
                    var id = packageVersion.Attribute("Include")?.Value;
                    var version = packageVersion.Attribute("Version")?.Value
                                  ?? packageVersion.Elements().FirstOrDefault(element => element.Name.LocalName == "Version")?.Value;
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(version))
                        centralVersions[id] = version;
                }

                break;
            }

            directory = directory.Parent;
        }

        return projectDocument.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element =>
            {
                var id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
                if (string.IsNullOrWhiteSpace(id))
                    return null;

                var version = element.Attribute("Version")?.Value
                              ?? element.Attribute("VersionOverride")?.Value
                              ?? element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value;
                if (string.IsNullOrWhiteSpace(version))
                    centralVersions.TryGetValue(id, out version);

                return new NuGetPackage
                {
                    Id = id,
                    Version = version ?? "Version managed by project",
                    InstalledVersion = version ?? "Version managed by project",
                    IsInstalled = true
                };
            })
            .Where(package => package is not null)
            .Cast<NuGetPackage>()
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    public async Task<List<NuGetPackage>> SearchAsync(string search)
    {
        return await SearchAsync(search, CancellationToken.None);
    }

    private async Task<List<NuGetPackage>> SearchAsync(string search, CancellationToken cancellationToken)
    {
        var resource = await _repository.GetResourceAsync<PackageSearchResource>();
        var results = await resource.SearchAsync(
            search,
            new SearchFilter(includePrerelease: false),
            skip: 0,
            take: 50,
            NullLogger.Instance,
            cancellationToken);

        return results.Select(result => new NuGetPackage
        {
            Id = result.Identity.Id,
            Version = result.Identity.Version.ToNormalizedString(),
            Description = result.Description ?? "",
            Authors = string.Join(", ", result.Authors),
            Downloads = result.DownloadCount,
            IsInstalled = _installedVersions.TryGetValue(result.Identity.Id, out var installedVersion),
            InstalledVersion = installedVersion ?? ""
        }).ToList();
    }

    private async Task SearchNuGetPackages(string searchTerm)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _isLoading = true;
        SetBusy(true);
        _statusText.Text = $"Searching for '{searchTerm}'...";
        _statusText.Foreground = Brushes.Yellow;
        _packages.Clear();

        try
        {
            var results = await SearchAsync(searchTerm, _searchCancellation.Token);
            foreach (var package in results)
                _packages.Add(package);

            _statusText.Text = $"Found {_packages.Count} packages matching '{searchTerm}'.";
            _statusText.Foreground = Brushes.LightGreen;
        }
        catch (OperationCanceledException)
        {
            _statusText.Text = "Search canceled.";
            _statusText.Foreground = Brushes.Gray;
        }
        catch (Exception ex)
        {
            _statusText.Text = $"NuGet search failed: {ex.Message}";
            _statusText.Foreground = Brushes.Red;
        }
        finally
        {
            _isLoading = false;
            SetBusy(false);
        }
    }

    private async Task LoadInstalledPackages()
    {
        _packages.Clear();
        _installedVersions.Clear();
        var hasProject = !string.IsNullOrWhiteSpace(_projectFilePath) && File.Exists(_projectFilePath);
        _installButton.IsEnabled = hasProject;
        _uninstallButton.IsEnabled = hasProject;
        _refreshButton.IsEnabled = hasProject;

        if (!hasProject)
        {
            _statusText.Text = "Select a .csproj to manage its packages.";
            _statusText.Foreground = Brushes.Gray;
            return;
        }

        _isLoading = true;
        SetBusy(true);
        _statusText.Text = $"Reading {_projectFilePath}...";
        _statusText.Foreground = Brushes.Yellow;

        try
        {
            var installed = await GetInstalledPackages(_projectFilePath!);
            foreach (var package in installed)
            {
                _installedVersions[package.Id] = package.InstalledVersion;
                _packages.Add(package);
            }

            _statusText.Text = $"Loaded {_packages.Count} direct package references.";
            _statusText.Foreground = Brushes.Gray;
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Could not read project packages: {ex.Message}";
            _statusText.Foreground = Brushes.Red;
        }
        finally
        {
            _isLoading = false;
            SetBusy(false);
        }
    }

    private async void InstallSelectedPackage(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;

        if (_packageList.SelectedItem is not NuGetPackage package) return;
        if (package.IsInstalled)
        {
            _statusText.Text = $"{package.Id} is already installed";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        if (string.IsNullOrWhiteSpace(_projectFilePath) || !File.Exists(_projectFilePath))
        {
            _statusText.Text = "Select a valid .csproj before installing packages.";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        var version = string.IsNullOrWhiteSpace(_versionBox.Text) ? "latest" : _versionBox.Text.Trim();
        if (version != "latest" && !NuGetVersion.TryParse(version, out _))
        {
            _statusText.Text = $"'{version}' is not a valid NuGet version.";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        _isLoading = true;
        SetBusy(true);
        _statusText.Text = $"Installing {package.Id} version {version}...";
        _statusText.Foreground = Brushes.Yellow;

        try
        {
            await InstallPackage(_projectFilePath, package.Id, version);
            package.IsInstalled = true;
            package.InstalledVersion = version == "latest" ? package.Version : version;
            _statusText.Text = $"Installed {package.Id} {package.InstalledVersion}.";
            _statusText.Foreground = Brushes.LightGreen;
            PackageInstallRequested?.Invoke(this, package);
            OutputAppended?.Invoke(this, $"Installed {package.Id} version {package.InstalledVersion} into {_projectFilePath}");
            await LoadInstalledPackages();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Failed to install {package.Id}: {ex.Message}";
            _statusText.Foreground = Brushes.Red;
            OutputAppended?.Invoke(this, _statusText.Text);
        }
        finally
        {
            _isLoading = false;
            SetBusy(false);
        }
    }

    private async void UninstallSelectedPackage(object? sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;

        if (_packageList.SelectedItem is not NuGetPackage package) return;
        if (!package.IsInstalled)
        {
            _statusText.Text = $"{package.Id} is not installed";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        if (string.IsNullOrWhiteSpace(_projectFilePath) || !File.Exists(_projectFilePath))
        {
            _statusText.Text = "Select a valid .csproj before uninstalling packages.";
            _statusText.Foreground = Brushes.Orange;
            return;
        }

        var confirm = await ShowConfirmDialog("Confirm Uninstall",
            $"Are you sure you want to uninstall {package.Id}?");

        if (!confirm || _isLoading) return;

        _isLoading = true;
        SetBusy(true);
        _statusText.Text = $"Uninstalling {package.Id}...";
        _statusText.Foreground = Brushes.Yellow;

        try
        {
            await RunDotnetAsync(
                Path.GetDirectoryName(_projectFilePath)!,
                new[] { "remove", _projectFilePath, "package", package.Id });
            package.IsInstalled = false;

            _statusText.Text = $"Uninstalled {package.Id}.";
            _statusText.Foreground = Brushes.LightGreen;
            PackageUninstallRequested?.Invoke(this, package);
            OutputAppended?.Invoke(this, $"Uninstalled {package.Id} from {_projectFilePath}");
            await LoadInstalledPackages();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Failed to uninstall {package.Id}: {ex.Message}";
            _statusText.Foreground = Brushes.Red;
            OutputAppended?.Invoke(this, _statusText.Text);
        }
        finally
        {
            _isLoading = false;
            SetBusy(false);
        }
    }

    private async Task RunDotnetAsync(string workingDirectory, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the dotnet CLI. Ensure the .NET SDK is installed and on PATH.");

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(standardError) ? standardOutput : standardError;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(details)
                    ? $"dotnet exited with code {process.ExitCode}."
                    : details.Trim());
        }
    }

    private void SetBusy(bool isBusy)
    {
        _progressBar.IsVisible = isBusy;
        _searchButton.IsEnabled = !isBusy;
        _projectBox.IsEnabled = !isBusy;

        var hasProject = !string.IsNullOrWhiteSpace(_projectFilePath) && File.Exists(_projectFilePath);
        _refreshButton.IsEnabled = !isBusy && hasProject;
        UpdateActionButtons();
    }

    private void UpdateActionButtons()
    {
        var hasProject = !string.IsNullOrWhiteSpace(_projectFilePath) && File.Exists(_projectFilePath);
        var selectedPackage = _packageList.SelectedItem as NuGetPackage;
        var canAct = !_isLoading && hasProject && selectedPackage is not null;
        _installButton.IsEnabled = canAct && !selectedPackage!.IsInstalled;
        _uninstallButton.IsEnabled = canAct && selectedPackage!.IsInstalled;

        UpdatePackageDetails(selectedPackage);
    }

    private void UpdatePackageDetails(NuGetPackage? selectedPackage)
    {
        _packageDetailsPanel.IsVisible = selectedPackage is not null;
        if (selectedPackage is not null)
        {
            _packageDetailsTitle.Text = $"{selectedPackage.Id}  v{selectedPackage.Version}";
            _packageDetailsText.Text = string.IsNullOrWhiteSpace(selectedPackage.Description)
                ? "Package information is loading..."
                : selectedPackage.Description;
            _packageDetailsMeta.Text = string.Join("  |  ", new[]
            {
                string.IsNullOrWhiteSpace(selectedPackage.Authors) ? null : selectedPackage.Authors,
                selectedPackage.Downloads.HasValue ? $"{selectedPackage.Downloads:N0} downloads" : null,
                selectedPackage.IsInstalled ? $"Installed: {selectedPackage.InstalledVersion}" : "Not installed"
            }.Where(value => value is not null));
        }
    }

    private async Task LoadSelectedPackageDetailsAsync()
    {
        _detailsCancellation?.Cancel();
        _detailsCancellation?.Dispose();
        _detailsCancellation = null;

        if (_packageList.SelectedItem is not NuGetPackage package ||
            !string.IsNullOrWhiteSpace(package.Description))
            return;

        var cancellation = new CancellationTokenSource();
        _detailsCancellation = cancellation;
        UpdatePackageDetails(package);

        try
        {
            var resource = await _repository.GetResourceAsync<PackageMetadataResource>();
            using var cacheContext = new SourceCacheContext();
            var metadata = await resource.GetMetadataAsync(
                package.Id,
                includePrerelease: false,
                includeUnlisted: false,
                cacheContext,
                NullLogger.Instance,
                cancellation.Token);
            var packageMetadata = metadata.FirstOrDefault(item =>
                                      string.Equals(item.Identity.Version.ToNormalizedString(), package.Version, StringComparison.OrdinalIgnoreCase))
                                  ?? metadata.FirstOrDefault();

            if (cancellation.IsCancellationRequested || _packageList.SelectedItem != package)
                return;

            if (packageMetadata is not null)
            {
                package.Description = packageMetadata.Description ?? "";
                package.Authors = packageMetadata.Authors ?? "";
                package.Downloads = packageMetadata.DownloadCount;
            }

            UpdatePackageDetails(package);
            if (string.IsNullOrWhiteSpace(package.Description))
                _packageDetailsText.Text = "No description is available for this package.";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!cancellation.IsCancellationRequested && _packageList.SelectedItem == package)
                _packageDetailsText.Text = "Package information could not be loaded.";
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
        
        if (this.GetVisualRoot() is not Window owner)
            return false;

        await dialog.ShowDialog(owner);
        return result;
    }

    private void ShowNotification(String title, String message, NotificationType notif_type = NotificationType.Success)
    {
	    // Build a built-in Notification object
        var toast = new Notification(
            title: title,
            message: message,
            type: notif_type,
            expiration: TimeSpan.FromSeconds(5),
            onClick: () => Console.WriteLine(title),
            onClose: () => Console.WriteLine(message)
        );

        // Send to the manager screen container
        _notificationManager.Show(toast);
    }
}