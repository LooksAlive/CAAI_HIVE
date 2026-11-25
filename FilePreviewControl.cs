using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using System.Diagnostics;
using System.Text;

namespace CAAI;

internal sealed class FilePreviewControl : UserControl, IDisposable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".gif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".config", ".csproj", ".editorconfig", ".gitignore", ".ini", ".json", ".log",
        ".props", ".targets", ".toml", ".txt", ".xml", ".yaml", ".yml"
    };

    private readonly string _filePath;
    private readonly bool _isTextFile;
    private Bitmap? _bitmap;
    private TextBox? _textEditor;
    private Markdown.Avalonia.MarkdownScrollViewer? _markdownPreview;
    private Grid? _markdownLayout;
    private string _savedText = string.Empty;
    private readonly bool _isMarkdown;

    public event EventHandler? SaveRequested;
    public event Action<bool>? DirtyStateChanged;
    public string FilePath => _filePath;
    public string? SaveError { get; private set; }
    public bool IsDirty => _textEditor != null && !string.Equals(_textEditor.Text, _savedText, StringComparison.Ordinal);

    public FilePreviewControl(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        var extension = Path.GetExtension(_filePath);
        _isMarkdown = extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                      extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        _isTextFile = TextExtensions.Contains(extension) || IsLikelyTextFile(_filePath);

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 35))
        };
        if (_isMarkdown)
        {
            var toolbar = CreateToolbar();
            Grid.SetRow(toolbar, 0);
            layout.Children.Add(toolbar);
        }
        var body = CreateBody(extension);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        Content = layout;
        AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        UpdateFileStatus();
    }

    private Control CreateToolbar()
    {
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(8, 5)
        };
        var editButton = new Button { Content = "Edit" };
        var previewButton = new Button { Content = "Preview" };
        var splitButton = new Button { Content = "Split", IsEnabled = true };
        editButton.Click += (_, _) => SetMarkdownMode("Edit");
        previewButton.Click += (_, _) => SetMarkdownMode("Preview");
        splitButton.Click += (_, _) => SetMarkdownMode("Split");
        toolbar.Children.Add(editButton);
        toolbar.Children.Add(previewButton);
        toolbar.Children.Add(splitButton);
        return toolbar;
    }

    private Control CreateBody(string extension)
    {
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase))
        {
            _savedText = File.ReadAllText(_filePath);
            _textEditor = CreateTextEditor(_savedText);
            _markdownPreview = new Markdown.Avalonia.MarkdownScrollViewer { Markdown = _savedText };
            _markdownLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
            _markdownLayout.Children.Add(_textEditor);
            var divider = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
                Margin = new Thickness(1, 0)
            };
            Grid.SetColumn(divider, 1);
            _markdownLayout.Children.Add(divider);
            var previewBorder = new Border { Padding = new Thickness(16), Child = _markdownPreview };
            Grid.SetColumn(previewBorder, 2);
            _markdownLayout.Children.Add(previewBorder);
            return _markdownLayout;
        }

        if (ImageExtensions.Contains(extension))
        {
            _bitmap = new Bitmap(_filePath);
            return new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new Image
                {
                    Source = _bitmap,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(16)
                }
            };
        }

        if (_isTextFile)
        {
            if (new FileInfo(_filePath).Length > 2 * 1024 * 1024)
                return CreateUnsupportedBody("File is larger than the 2 MB preview limit.");

            _savedText = File.ReadAllText(_filePath);
            _textEditor = CreateTextEditor(_savedText);
            return _textEditor;
        }

        return CreateUnsupportedBody($"No in-app preview is available for '{extension}'.");
    }

    private TextBox CreateTextEditor(string text)
    {
        var editor = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Code, Consolas, Monospace"),
            FontSize = 13,
            Padding = new Thickness(16),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(215, 215, 215)),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        editor.TextChanged += (_, _) =>
        {
            if (_isMarkdown && _markdownPreview != null)
                _markdownPreview.Markdown = editor.Text ?? string.Empty;
            UpdateFileStatus();
        };
        return editor;
    }

    private void SetMarkdownMode(string mode)
    {
        if (_markdownLayout is null) return;
        _markdownLayout.ColumnDefinitions[0].Width = new GridLength(mode == "Preview" ? 0 : 1, GridUnitType.Star);
        _markdownLayout.ColumnDefinitions[2].Width = new GridLength(mode == "Edit" ? 0 : 1, GridUnitType.Star);
        _markdownLayout.Children[0].IsVisible = mode != "Preview";
        _markdownLayout.Children[1].IsVisible = mode == "Split";
        _markdownLayout.Children[2].IsVisible = mode != "Edit";
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SaveRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    public async Task<bool> SaveAsync()
    {
        if (_textEditor is null || !IsDirty) return true;
        try
        {
            await File.WriteAllTextAsync(_filePath, _textEditor.Text ?? string.Empty);
            _savedText = _textEditor.Text ?? string.Empty;
            SaveError = null;
            UpdateFileStatus();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SaveError = exception.Message;
            return false;
        }
    }

    private void UpdateFileStatus()
    {
        DirtyStateChanged?.Invoke(IsDirty);
    }

    private Control CreateUnsupportedBody(string messageText)
    {
        var message = new TextBlock
        {
            Text = messageText,
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var openExternal = new Button
        {
            Content = "Open with system app",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        openExternal.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(_filePath) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                message.Text = $"Could not open this file: {exception.Message}";
            }
        };

        var unsupported = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        unsupported.Children.Add(message);
        unsupported.Children.Add(openExternal);
        return unsupported;
    }

    private static bool IsLikelyTextFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var sample = new byte[Math.Min(4096, (int)Math.Min(stream.Length, 4096))];
            var bytesRead = stream.Read(sample, 0, sample.Length);
            if (bytesRead == 0) return true;

            var textSample = sample.AsSpan(0, bytesRead);
            if (textSample.Contains((byte)0)) return false;
            _ = new UTF8Encoding(false, true).GetString(textSample);
            return textSample.ToArray().All(value => value is 9 or 10 or 13 || value >= 32);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
