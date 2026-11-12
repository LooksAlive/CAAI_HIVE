using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;

public class FindReplaceDialog : Window
{
    private readonly TextEditor _editor;
    private readonly TextBox _findTextBox;
    private readonly TextBox _replaceTextBox;
    private readonly CheckBox _matchCase;
    private readonly CheckBox _wholeWord;
    private readonly TextBlock _status;
    private bool _isReplacingAll;

    public FindReplaceDialog(TextEditor editor, bool showReplace)
    {
        _editor = editor;
        Title = showReplace ? "Find and Replace" : "Find";
        Width = 520;
        Height = showReplace ? 230 : 190;
        MinWidth = 420;
        MinHeight = showReplace ? 220 : 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 35));

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto, Auto, Auto, *"),
            Margin = new Thickness(14)
        };
        var fields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto, *"),
            RowDefinitions = new RowDefinitions(showReplace ? "Auto, Auto" : "Auto"),
            RowSpacing = 8,
            ColumnSpacing = 10
        };
        var findLabel = new TextBlock { Text = "Find", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Width = 55 };
        _findTextBox = new TextBox { Watermark = "Search in current file", HorizontalAlignment = HorizontalAlignment.Stretch };
        if (!string.IsNullOrEmpty(editor.SelectedText) && !editor.SelectedText.Contains('\n') && !editor.SelectedText.Contains('\r'))
            _findTextBox.Text = editor.SelectedText;
        Grid.SetColumn(_findTextBox, 1);
        fields.Children.Add(findLabel);
        fields.Children.Add(_findTextBox);

        _replaceTextBox = new TextBox { Watermark = "Replace with", HorizontalAlignment = HorizontalAlignment.Stretch };
        if (showReplace)
        {
            var replaceLabel = new TextBlock { Text = "Replace", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(replaceLabel, 1);
            Grid.SetRow(_replaceTextBox, 1);
            Grid.SetColumn(_replaceTextBox, 1);
            fields.Children.Add(replaceLabel);
            fields.Children.Add(_replaceTextBox);
        }
        root.Children.Add(fields);

        var options = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 18,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _matchCase = new CheckBox { Content = "Match case", Foreground = Brushes.White };
        _wholeWord = new CheckBox { Content = "Whole word", Foreground = Brushes.White };
        options.Children.Add(_matchCase);
        options.Children.Add(_wholeWord);
        Grid.SetRow(options, 1);
        root.Children.Add(options);

        var commands = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0)
        };
        var previous = new Button { Content = "Previous", Padding = new Thickness(10, 5) };
        var next = new Button { Content = "Next", Padding = new Thickness(10, 5) };
        previous.Click += (_, _) => FindMatch(false);
        next.Click += (_, _) => FindMatch(true);
        commands.Children.Add(previous);
        commands.Children.Add(next);
        if (showReplace)
        {
            var replace = new Button { Content = "Replace", Padding = new Thickness(10, 5) };
            var replaceAll = new Button { Content = "Replace All", Padding = new Thickness(10, 5) };
            replace.Click += (_, _) => ReplaceCurrent();
            replaceAll.Click += (_, _) => ReplaceAll();
            commands.Children.Add(replace);
            commands.Children.Add(replaceAll);
        }
        Grid.SetRow(commands, 2);
        root.Children.Add(commands);

        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(170, 175, 182)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 6, 0, 0)
        };
        Grid.SetRow(_status, 3);
        root.Children.Add(_status);
        Content = root;

        _findTextBox.TextChanged += (_, _) => UpdateStatus();
        _matchCase.IsCheckedChanged += (_, _) => UpdateStatus();
        _wholeWord.IsCheckedChanged += (_, _) => UpdateStatus();
        _editor.TextChanged += EditorTextChanged;
        Closed += (_, _) => _editor.TextChanged -= EditorTextChanged;
        Opened += (_, _) => { _findTextBox.Focus(); _findTextBox.SelectAll(); };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                FindMatch(!e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                e.Handled = true;
            }
        };
        UpdateStatus();
    }

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        if (!_isReplacingAll)
            UpdateStatus();
    }

    private List<int> GetMatches()
    {
        var matches = new List<int>();
        var query = _findTextBox.Text;
        var text = _editor.Text ?? "";
        if (string.IsNullOrEmpty(query))
            return matches;

        var comparison = _matchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var offset = 0;
        while (offset <= text.Length - query.Length)
        {
            var index = text.IndexOf(query, offset, comparison);
            if (index < 0)
                break;

            var end = index + query.Length;
            if (_wholeWord.IsChecked != true ||
                ((index == 0 || !IsWordCharacter(text[index - 1])) &&
                 (end == text.Length || !IsWordCharacter(text[end]))))
                matches.Add(index);
            offset = index + query.Length;
        }
        return matches;
    }

    private static bool IsWordCharacter(char character) => char.IsLetterOrDigit(character) || character == '_';

    private void UpdateStatus()
    {
        if (string.IsNullOrEmpty(_findTextBox.Text))
        {
            _status.Text = "";
            return;
        }

        var count = GetMatches().Count;
        _status.Text = count == 0 ? "No matches" : $"{count} match{(count == 1 ? "" : "es")}";
    }

    private void FindMatch(bool forward)
    {
        var matches = GetMatches();
        if (matches.Count == 0)
        {
            UpdateStatus();
            return;
        }

        var selectedMatch = _editor.SelectionLength == _findTextBox.Text!.Length && matches.Contains(_editor.SelectionStart);
        var position = selectedMatch ? _editor.SelectionStart : _editor.CaretOffset;
        var matchIndex = forward
            ? matches.FindIndex(index => selectedMatch ? index > position : index >= position)
            : matches.FindLastIndex(index => index < position);
        var wrapped = matchIndex < 0;
        if (wrapped)
            matchIndex = forward ? 0 : matches.Count - 1;

        _editor.Select(matches[matchIndex], _findTextBox.Text.Length);
        _editor.CaretOffset = matches[matchIndex] + _findTextBox.Text.Length;
        _editor.ScrollToLine(_editor.Document.GetLineByOffset(matches[matchIndex]).LineNumber);
        _status.Text = $"{matchIndex + 1} of {matches.Count}" + (wrapped ? " (wrapped)" : "");
    }

    private void ReplaceCurrent()
    {
        var query = _findTextBox.Text;
        if (string.IsNullOrEmpty(query))
            return;

        var matches = GetMatches();
        if (_editor.SelectionLength == query.Length && matches.Contains(_editor.SelectionStart))
        {
            var start = _editor.SelectionStart;
            var replacement = _replaceTextBox.Text ?? "";
            _editor.Document.Replace(start, query.Length, replacement);
            _editor.CaretOffset = start + replacement.Length;
            _editor.Select(_editor.CaretOffset, 0);
        }
        FindMatch(true);
    }

    private void ReplaceAll()
    {
        var query = _findTextBox.Text;
        if (string.IsNullOrEmpty(query))
            return;

        var matches = GetMatches();
        if (matches.Count == 0)
        {
            UpdateStatus();
            return;
        }

        var replacement = _replaceTextBox.Text ?? "";
        _isReplacingAll = true;
        _editor.Document.BeginUpdate();
        try
        {
            for (var index = matches.Count - 1; index >= 0; index--)
                _editor.Document.Replace(matches[index], query.Length, replacement);
        }
        finally
        {
            _editor.Document.EndUpdate();
            _isReplacingAll = false;
        }
        _status.Text = $"Replaced {matches.Count} match{(matches.Count == 1 ? "" : "es")}";
    }
}