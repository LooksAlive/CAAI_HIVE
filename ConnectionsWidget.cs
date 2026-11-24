using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace CAAI;

public sealed class ConnectionsWidget : UserControl
{
    private readonly EnvironmentStore _store;
    private readonly TextBox _search = new() { Watermark = "Filter connections...", Margin = new Thickness(8) };
    private readonly ListBox _list = new() { Name = "ConnectionList", Background = Brushes.Transparent };
    private readonly StackPanel _details = new() { Margin = new Thickness(10), Spacing = 6 };
    private readonly TextBlock _status = new() { Margin = new Thickness(8), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
    private readonly Button _edit = IconButton(Symbol.Edit, "Edit connection");
    private readonly Button _delete = IconButton(Symbol.Delete, "Delete connection");
    private bool _refreshing;

    public event EventHandler<EnvironmentEntity>? RevealEntityRequested;

    public ConnectionsWidget(EnvironmentStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var add = IconButton(Symbol.Add, "New connection");
        add.Name = "NewConnection";
        add.Click += async (_, _) => await EditConnectionAsync();
        _edit.Click += async (_, _) => { if (_list.SelectedItem is EntityConnection connection) await EditConnectionAsync(connection); };
        _delete.Click += (_, _) =>
        {
            if (_list.SelectedItem is EntityConnection connection) _store.RemoveConnection(connection.Id);
        };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(8, 0) };
        toolbar.Children.Add(add);
        toolbar.Children.Add(_edit);
        toolbar.Children.Add(_delete);
        _list.ItemTemplate = new FuncDataTemplate<EntityConnection>((connection, _) =>
        {
            if (connection == null) return new TextBlock();
            var row = new StackPanel { Spacing = 3, Margin = new Thickness(2, 5) };
            row.Children.Add(new TextBlock { Text = connection.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = $"{connection.Kind} | {connection.Participants.Count} entities", Foreground = Brushes.Gray, FontSize = 11 });
            row.Children.Add(new TextBlock { Text = string.Join(", ", connection.Participants.Select(entity => _store.GetPath(entity))),
                FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            return row;
        });
        _list.SelectionChanged += (_, _) => { if (!_refreshing) ShowDetails(); };
        _list.DoubleTapped += async (_, _) => { if (_list.SelectedItem is EntityConnection connection) await EditConnectionAsync(connection); };
        _search.TextChanged += (_, _) => Refresh();
        var detailScroll = new ScrollViewer { Content = _details, MaxHeight = 250, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
        layout.Children.Add(_search);
        Grid.SetRow(toolbar, 1);
        layout.Children.Add(toolbar);
        Grid.SetRow(_list, 2);
        layout.Children.Add(_list);
        Grid.SetRow(detailScroll, 3);
        layout.Children.Add(detailScroll);
        Grid.SetRow(_status, 4);
        layout.Children.Add(_status);
        Content = layout;
        AttachedToVisualTree += (_, _) => { _store.Changed += OnStoreChanged; Refresh(); };
        DetachedFromVisualTree += (_, _) => _store.Changed -= OnStoreChanged;
        Refresh();
    }

    private void OnStoreChanged(object? sender, EventArgs args) => Refresh();

    private void Refresh()
    {
        var selectedId = (_list.SelectedItem as EntityConnection)?.Id;
        var query = _search.Text?.Trim() ?? "";
        var matches = _store.Connections.Where(connection =>
            $"{connection.Name} {connection.Kind} {connection.Notes}".Contains(query, StringComparison.OrdinalIgnoreCase) ||
            connection.Participants.Any(entity => _store.GetPath(entity).Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        _refreshing = true;
        try
        {
            _list.ItemsSource = matches;
            _list.SelectedItem = matches.FirstOrDefault(connection => connection.Id == selectedId);
        }
        finally { _refreshing = false; }
        _status.Text = _store.Connections.Count == 0 ? "No connections" : $"{matches.Length} of {_store.Connections.Count} connections";
        ShowDetails();
    }

    private void ShowDetails()
    {
        _details.Children.Clear();
        var connection = _list.SelectedItem as EntityConnection;
        _edit.IsEnabled = _delete.IsEnabled = connection != null;
        if (connection == null) return;
        _details.Children.Add(new TextBlock { Text = connection.Kind, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        foreach (var entity in connection.Participants)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = new TextBlock { Text = _store.GetPath(entity), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(name, $"{entity.Type}: {entity.Description}");
            row.Children.Add(name);
            var reveal = IconButton(Symbol.Go, "Reveal in Environment");
            reveal.Click += (_, _) => RevealEntityRequested?.Invoke(this, entity);
            Grid.SetColumn(reveal, 1);
            row.Children.Add(reveal);
            _details.Children.Add(row);
        }
        if (!string.IsNullOrWhiteSpace(connection.Notes))
            _details.Children.Add(new TextBlock { Text = connection.Notes, TextWrapping = TextWrapping.Wrap });
    }

    public Task CreateConnectionAsync(EnvironmentEntity? firstParticipant = null) => EditConnectionAsync(null, firstParticipant);

    private async Task EditConnectionAsync(EntityConnection? existing = null, EnvironmentEntity? firstParticipant = null)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var selected = (existing?.Participants ?? []).ToHashSet();
        if (firstParticipant != null) selected.Add(firstParticipant);
        var dialog = new Window { Title = existing == null ? "New Connection" : "Edit Connection", Width = 620, Height = 620,
            MinWidth = 420, MinHeight = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var name = new TextBox { Name = "ConnectionName", Text = existing?.Name ?? "", Watermark = "Connection name" };
        var kind = new TextBox { Name = "ConnectionKind", Text = existing?.Kind ?? "Related", Watermark = "Relation" };
        var notes = new TextBox { Name = "ConnectionNotes", Text = existing?.Notes ?? "", Watermark = "Notes", AcceptsReturn = true, Height = 64 };
        var search = new TextBox { Name = "ConnectionEntitySearch", Watermark = "Filter entities..." };
        var count = new TextBlock { Foreground = Brushes.Gray };
        var error = new TextBlock { Foreground = Brushes.LightCoral, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var available = new ListBox { Name = "ConnectionEntities", Background = Brushes.Transparent };
        var save = new Button { Name = "SaveConnection", Content = existing == null ? "Connect" : "Save", IsDefault = true };
        void UpdateCount() { count.Text = $"{selected.Count} selected"; save.IsEnabled = selected.Count >= 2 && !string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(kind.Text); }
        available.ItemTemplate = new FuncDataTemplate<EnvironmentEntity>((entity, _) =>
        {
            if (entity == null) return new TextBlock();
            var label = new StackPanel { Spacing = 2 };
            label.Children.Add(new TextBlock { Text = _store.GetPath(entity), TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = $"{entity.Type} | {entity.Description}", FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis });
            var checkbox = new CheckBox { Content = label, IsChecked = selected.Contains(entity), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(checkbox, $"{_store.GetPath(entity)}\n{entity.Description}");
            checkbox.IsCheckedChanged += (_, _) =>
            {
                available.SelectedItem = entity;
                if (checkbox.IsChecked == true) selected.Add(entity); else selected.Remove(entity);
                UpdateCount();
            };
            return checkbox;
        });
        void Filter()
        {
            var entities = _store.GetAllEntities();
            selected.IntersectWith(entities);
            var query = search.Text?.Trim() ?? "";
            available.ItemsSource = entities.Where(entity => $"{_store.GetPath(entity)} {entity.Description}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            UpdateCount();
        }
        search.TextChanged += (_, _) => Filter();
        name.TextChanged += (_, _) => UpdateCount();
        kind.TextChanged += (_, _) => UpdateCount();
        var expand = IconButton(Symbol.List, "Expose selected data entity's members (up to 100; property getters are not evaluated)");
        expand.IsEnabled = false;
        available.SelectionChanged += (_, _) => expand.IsEnabled = available.SelectedItem is EnvironmentEntity { Type: EntityType.DataValue, Value: not null };
        expand.Click += (_, _) =>
        {
            if (available.SelectedItem is not EnvironmentEntity { Type: EntityType.DataValue } entity) return;
            try { _store.AddDataMembers(entity); Filter(); }
            catch (Exception exception) { error.Text = exception.Message; error.IsVisible = true; }
        };
        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        searchRow.Children.Add(search);
        Grid.SetColumn(expand, 1);
        searchRow.Children.Add(expand);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) =>
        {
            try
            {
                var connection = existing == null ? _store.Connect(name.Text ?? "", selected, kind.Text ?? "", notes.Text ?? "")
                    : _store.Update(existing.Id, name.Text ?? "", selected, kind.Text ?? "", notes.Text ?? "");
                _search.Text = "";
                Refresh();
                _list.SelectedItem = connection;
                dialog.Close();
            }
            catch (Exception exception) { error.Text = exception.Message; error.IsVisible = true; }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var form = new StackPanel { Spacing = 8 };
        form.Children.Add(new TextBlock { Text = "Name" });
        form.Children.Add(name);
        form.Children.Add(new TextBlock { Text = "Relation" });
        form.Children.Add(kind);
        form.Children.Add(notes);
        form.Children.Add(searchRow);
        form.Children.Add(count);
        var footer = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        footer.Children.Add(error);
        footer.Children.Add(buttons);
        var layout = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        layout.Children.Add(form);
        Grid.SetRow(available, 1);
        layout.Children.Add(available);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        dialog.Content = layout;
        void OnChanged(object? sender, EventArgs args) => Filter();
        _store.Changed += OnChanged;
        try
        {
            Filter();
            dialog.Opened += (_, _) => name.Focus();
            await dialog.ShowDialog(owner);
        }
        finally { _store.Changed -= OnChanged; }
    }

    private static Button IconButton(Symbol symbol, string tooltip)
    {
        var button = new Button { Content = new SymbolIcon { Symbol = symbol, FontSize = 16 }, Width = 30, Height = 30, Padding = new Thickness(4) };
        ToolTip.SetTip(button, tooltip);
        Avalonia.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }
}