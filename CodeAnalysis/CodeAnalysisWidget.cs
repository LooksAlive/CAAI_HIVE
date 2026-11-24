using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using Microsoft.CodeAnalysis;

namespace CAAI;

public sealed class CodeAnalysisWidget : UserControl, IDisposable
{
    private readonly Workspace _workspace;
    private readonly TextBlock _status = new() { Text = "Analysis has not run yet", Foreground = Brushes.Gray, FontSize = 11 };
    private readonly StackPanel _overview = new() { Spacing = 8, Margin = new Thickness(12) };
    private readonly StackPanel _types = new() { Spacing = 5, Margin = new Thickness(12) };
    private readonly StackPanel _naming = new() { Spacing = 5, Margin = new Thickness(12) };
    private readonly StackPanel _similarity = new() { Spacing = 5, Margin = new Thickness(12) };
    private readonly TextBox _typeFilter = new() { Watermark = "Filter classes...", Margin = new Thickness(12, 10, 12, 0) };
    private readonly ComboBox _similarityCategory = new() { Name = "SimilarityCategory", Width = 190,
        ItemsSource = new[] { "All similarities", "Type names", "Member names", "Member types", "Type structure", "Code usage", "Repeated method structures" }, SelectedIndex = 0 };
    private readonly ComboBox _similaritySymbolKind = new() { Name = "SimilaritySymbolKind", Width = 145,
        ItemsSource = new[] { "All symbols", "Classes", "Structs", "Interfaces", "Records", "Methods", "Fields", "Properties", "Events", "Enums", "Delegates" }, SelectedIndex = 0 };
    private readonly NumericUpDown _similarityMinimum = new() { Name = "SimilarityMinimum", Width = 130, Minimum = 65, Maximum = 100, Increment = 5, Value = 75, FormatString = "0" };
    private readonly TextBox _similarityFilter = new() { Name = "SimilarityFilter", Watermark = "Filter names or files...", Margin = new Thickness(12, 0, 12, 4) };
    private CancellationTokenSource? _refreshRequest;
    private ProjectMetrics? _metrics;
    private bool _disposed;
    public event Action<string, int, int>? SourceRequested;

    public CodeAnalysisWidget(Workspace workspace)
    {
        _workspace = workspace;
        _typeFilter.TextChanged += (_, _) => RenderTypes();
        _similarityCategory.SelectionChanged += (_, _) => { if (_metrics != null) RenderSimilarity(_metrics); };
        _similaritySymbolKind.SelectionChanged += (_, _) => { if (_metrics != null) RenderSimilarity(_metrics); };
        _similarityMinimum.ValueChanged += (_, _) => { if (_metrics != null) RenderSimilarity(_metrics); };
        _similarityFilter.TextChanged += (_, _) => { if (_metrics != null) RenderSimilarity(_metrics); };
        var refresh = new Button { Content = "Refresh analysis", Margin = new Thickness(0, 0, 8, 0) };
        refresh.Click += async (_, _) => await RefreshAsync();

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(12, 10)
        };
        header.Children.Add(new TextBlock
        {
            Text = "Project Code Analysis",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(_status, 1);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(0, 0, 12, 0);
        header.Children.Add(_status);
        Grid.SetColumn(refresh, 2);
        header.Children.Add(refresh);

        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Overview", Content = CreateScrollPage(_overview) },
                new TabItem { Header = "Types", Content = CreateTypesPage() },
                new TabItem { Header = "Naming", Content = CreateScrollPage(_naming) },
                new TabItem { Header = "Similarity", Content = CreateSimilarityPage() }
            }
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(header);
        Grid.SetRow(tabs, 1);
        layout.Children.Add(tabs);
        Content = layout;

        var weakWidget = new WeakReference<CodeAnalysisWidget>(this);
        _workspace.RegisterWorkspaceChangedHandler(_ =>
        {
            if (weakWidget.TryGetTarget(out var widget))
                Dispatcher.UIThread.Post(widget.ScheduleRefresh);
        });
        AttachedToVisualTree += (_, _) => ScheduleRefresh();
    }

    private static ScrollViewer CreateScrollPage(Control content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
    };

    private Control CreateTypesPage()
    {
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        layout.Children.Add(_typeFilter);
        var typeScroll = CreateScrollPage(_types);
        Grid.SetRow(typeScroll, 1);
        layout.Children.Add(typeScroll);
        return layout;
    }

    private Control CreateSimilarityPage()
    {
        var filters = new WrapPanel { Margin = new Thickness(12, 8) };
        foreach (var (label, control) in new (string Label, Control Control)[]
            { ("Category", _similarityCategory), ("Symbols", _similaritySymbolKind), ("Minimum %", _similarityMinimum) })
        {
            var group = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 10, 6) };
            group.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brushes.Gray });
            group.Children.Add(control);
            filters.Children.Add(group);
        }
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        layout.Children.Add(filters);
        Grid.SetRow(_similarityFilter, 1);
        layout.Children.Add(_similarityFilter);
        var scroll = CreateScrollPage(_similarity);
        Grid.SetRow(scroll, 2);
        layout.Children.Add(scroll);
        return layout;
    }

    private void ScheduleRefresh()
    {
        if (_disposed) return;
        _refreshRequest?.Cancel();
        _refreshRequest?.Dispose();
        var request = new CancellationTokenSource();
        _refreshRequest = request;
        _ = RefreshAfterDelayAsync(request.Token);
    }

    private async Task RefreshAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken);
            await RefreshCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAsync()
    {
        _refreshRequest?.Cancel();
        _refreshRequest?.Dispose();
        var request = new CancellationTokenSource();
        _refreshRequest = request;
        await RefreshCoreAsync(request.Token);
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        _status.Text = "Analyzing...";
        try
        {
            var project = _workspace.CurrentSolution.Projects.FirstOrDefault();
            if (project is null)
            {
                _metrics = null;
                RenderEmpty("No project is loaded.");
                return;
            }

            var metrics = await ProjectMetricsAnalyzer.AnalyzeAsync(project, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) return;
            _metrics = metrics;
            RenderMetrics(metrics);
            _status.Text = $"{metrics.ProjectName}  |  {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_disposed) return;
            _metrics = null;
            RenderEmpty($"Analysis failed: {exception.Message}");
            _status.Text = "Analysis failed";
        }
    }

    private void RenderMetrics(ProjectMetrics metrics)
    {
        _overview.Children.Clear();
        _overview.Children.Add(new TextBlock
        {
            Text = metrics.ProjectName,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White
        });
        var cards = new WrapPanel { Orientation = Orientation.Horizontal };
        cards.Children.Add(CreateMetricCard("Source files", metrics.SourceFileCount));
        cards.Children.Add(CreateMetricCard("Lines of code", metrics.LinesOfCode));
        cards.Children.Add(CreateMetricCard("Classes", metrics.ClassCount));
        cards.Children.Add(CreateMetricCard("Methods", metrics.MethodCount));
        cards.Children.Add(CreateMetricCard("Properties", metrics.PropertyCount));
        cards.Children.Add(CreateMetricCard("Fields", metrics.FieldCount));
        cards.Children.Add(CreateMetricCard("Complexity", metrics.TotalComplexity));
        _overview.Children.Add(cards);
        _overview.Children.Add(SectionTitle("Type inventory"));
        _overview.Children.Add(new TextBlock
        {
            Text = $"{metrics.ClassCount} classes  |  {metrics.RecordCount} records  |  {metrics.StructCount} structs  |  {metrics.InterfaceCount} interfaces  |  {metrics.EnumCount} enums",
            Foreground = Brushes.LightGray
        });

        _overview.Children.Add(SectionTitle("Highest estimated complexity"));
        AddTypeSummaryList(_overview, metrics.Types.Take(5));
        _overview.Children.Add(SectionTitle("Simplest types"));
        AddTypeSummaryList(_overview, metrics.Types.OrderBy(type => type.Complexity).ThenBy(type => type.MethodCount).Take(5));
        _overview.Children.Add(SectionTitle("Types by namespace"));
        foreach (var group in metrics.Types.GroupBy(type => string.IsNullOrEmpty(type.Namespace) ? "(global namespace)" : type.Namespace)
                     .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            _overview.Children.Add(new TextBlock
            {
                Text = $"{group.Key}  |  {group.Count()} types",
                Foreground = Brushes.LightGray
            });

        RenderTypes();
        RenderNaming(metrics);
        RenderSimilarity(metrics);
    }

    private void RenderTypes()
    {
        _types.Children.Clear();
        if (_metrics is null) return;
        var query = _typeFilter.Text?.Trim();
        var filtered = _metrics.Types.Where(type => string.IsNullOrEmpty(query) ||
            type.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            type.Namespace.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        _types.Children.Add(new TextBlock
        {
            Text = $"{filtered.Length} of {_metrics.Types.Count} types",
            Foreground = Brushes.Gray,
            FontSize = 11
        });
        foreach (var type in filtered)
        {
            var members = new StackPanel { Spacing = 3, Margin = new Thickness(8, 4) };
            members.Children.Add(new TextBlock
            {
                Text = $"{type.MethodCount} methods  |  {type.PropertyCount} properties  |  {type.FieldCount} fields  |  {type.Lines} lines  |  complexity {type.Complexity}",
                Foreground = Brushes.LightGray,
                FontSize = 11
            });
            foreach (var method in type.Methods.OrderByDescending(method => method.Complexity))
                members.Children.Add(new TextBlock
                {
                    Text = $"{method.Signature}  |  complexity {method.Complexity}  |  {method.Lines} lines  |  {Path.GetFileName(method.FilePath)}:{method.StartLine}",
                    Foreground = Brushes.Gray,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                });

            var expander = new Expander
            {
                Header = $"{type.Kind} {type.Name}  |  {type.Namespace}  |  {type.MethodCount} methods  |  complexity {type.Complexity}",
                Content = members,
                IsExpanded = false,
                Margin = new Thickness(0, 1)
            };
            _types.Children.Add(expander);
        }
    }

    private void RenderNaming(ProjectMetrics metrics)
    {
        _naming.Children.Clear();
        _naming.Children.Add(SectionTitle("Naming consistency"));
        _naming.Children.Add(new TextBlock
        {
            Text = $"{metrics.NamingIssues.Count} names differ from the basic conventions (types and methods: PascalCase; parameters and fields: camelCase). Review these as suggestions, not errors.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var issue in metrics.NamingIssues.Take(300))
            _naming.Children.Add(new TextBlock
            {
                Text = $"{issue.Kind}: {issue.Name}  |  expected {issue.ExpectedStyle}  |  {issue.Location}",
                Foreground = new SolidColorBrush(Color.FromRgb(218, 180, 120)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11
            });
    }

    private void RenderSimilarity(ProjectMetrics metrics)
    {
        _similarity.Children.Clear();
        var query = _similarityFilter.Text?.Trim() ?? "";
        var minimum = (int)(_similarityMinimum.Value ?? 75);
        var category = _similarityCategory.SelectedIndex;
        var symbolKind = _similaritySymbolKind.SelectedIndex switch
        {
            1 => "Class", 2 => "Struct", 3 => "Interface", 4 => "Record", 5 => "Method", 6 => "Field",
            7 => "Property", 8 => "Event", 9 => "Enum", 10 => "Delegate", _ => null
        };
        var report = metrics.Similarities;
        _similarity.Children.Add(new TextBlock
        {
            Name = "SimilaritySummary", Text = $"{report.SymbolCount:N0} symbols | {report.ComparedPairs:N0} compared pairs",
            Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap
        });
        foreach (var warning in report.Warnings)
            _similarity.Children.Add(new TextBlock { Text = warning, Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap });
        var matches = report.Matches.Where(match => match.Score >= minimum &&
            (category == 0 || category == (int)match.Kind + 1) && (symbolKind == null || match.Left.Kind == symbolKind) &&
            $"{match.Left.DisplayName} {match.Right.DisplayName} {match.Left.FilePath} {match.Right.FilePath}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(match => match.Score).ThenBy(match => match.Kind).ToArray();
        if (category != 6)
        {
            _similarity.Children.Add(new TextBlock { Name = "SimilarityMatchCount", Text = $"{matches.Length} matching pairs", Foreground = Brushes.Gray });
            foreach (var match in matches)
            {
                var details = new StackPanel { Spacing = 5, Margin = new Thickness(8, 4) };
                details.Children.Add(CreateSymbolLink(match.Left));
                details.Children.Add(CreateSymbolLink(match.Right));
                foreach (var evidence in match.Evidence)
                    details.Children.Add(new TextBlock { Text = evidence, FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
                _similarity.Children.Add(new Expander
                {
                    Header = new TextBlock { Text = $"{match.Score}% {SimilarityLabel(match.Kind)} | {match.Left.Name} / {match.Right.Name}", TextWrapping = TextWrapping.Wrap },
                    Content = details, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 1)
                });
            }
            if (matches.Length == 0) _similarity.Children.Add(new TextBlock { Text = "No similar pairs match these filters.", Foreground = Brushes.Gray });
        }
        if (category is not (0 or 6) || symbolKind is not (null or "Method")) return;
        _similarity.Children.Add(SectionTitle("Repeated method structures"));
        var groups = metrics.SimilarCode.Where(group => group.Methods.Any(method =>
            $"{method.Signature} {method.FilePath}".Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (groups.Length == 0) _similarity.Children.Add(new TextBlock { Text = "No repeated method structures match these filters.", Foreground = Brushes.Gray });
        foreach (var group in groups)
        {
            var members = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4) };
            foreach (var method in group.Methods)
                members.Children.Add(CreateSymbolLink(new SimilaritySymbol(method.Name, method.Signature, "Method", method.FilePath, method.StartLine, 1)));
            _similarity.Children.Add(new Expander
            {
                Header = new TextBlock { Text = $"100% normalized structure | {group.Methods.Count} methods", TextWrapping = TextWrapping.Wrap },
                Content = members
            });
        }
    }

    private Control CreateSymbolLink(SimilaritySymbol symbol)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock { Text = $"{symbol.Kind}: {symbol.DisplayName}\n{Path.GetFileName(symbol.FilePath)}:{symbol.Line}:{symbol.Column}",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center });
        var navigate = new Button { Content = new SymbolIcon { Symbol = Symbol.Go, FontSize = 14 }, Width = 28, Height = 28,
            Padding = new Thickness(3), IsEnabled = !string.IsNullOrEmpty(symbol.FilePath) };
        ToolTip.SetTip(navigate, "Go to source");
        Avalonia.Automation.AutomationProperties.SetName(navigate, "Go to source");
        navigate.Click += (_, _) => SourceRequested?.Invoke(symbol.FilePath, symbol.Line, symbol.Column);
        Grid.SetColumn(navigate, 1);
        row.Children.Add(navigate);
        return row;
    }

    private static string SimilarityLabel(ProjectSimilarityKind kind) => kind switch
    {
        ProjectSimilarityKind.TypeName => "type names", ProjectSimilarityKind.MemberName => "member names",
        ProjectSimilarityKind.MemberType => "member types", ProjectSimilarityKind.TypeShape => "type structure",
        _ => "code usage"
    };

    private void RenderEmpty(string message)
    {
        _overview.Children.Clear();
        _overview.Children.Add(new TextBlock { Text = message, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
        _types.Children.Clear();
        _naming.Children.Clear();
        _similarity.Children.Clear();
    }

    private static Border CreateMetricCard(string label, int value)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new TextBlock { Text = value.ToString("N0"), FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White });
        content.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Brushes.Gray });
        return new Border
        {
            Child = content,
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 0, 8, 8),
            Background = new SolidColorBrush(Color.FromRgb(40, 40, 45)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3)
        };
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeight.SemiBold,
        Foreground = Brushes.White,
        Margin = new Thickness(0, 8, 0, 2)
    };

    private static void AddTypeSummaryList(Panel panel, IEnumerable<TypeMetric> types)
    {
        var any = false;
        foreach (var type in types)
        {
            any = true;
            panel.Children.Add(new TextBlock
            {
                Text = $"{type.Name} ({type.Namespace})  |  {type.MethodCount} methods  |  complexity {type.Complexity}  |  {type.Lines} lines",
                Foreground = Brushes.LightGray,
                TextWrapping = TextWrapping.Wrap
            });
        }
        if (!any) panel.Children.Add(new TextBlock { Text = "No type data available.", Foreground = Brushes.Gray });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshRequest?.Cancel();
        _refreshRequest?.Dispose();
    }
}
