using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaloniaEdit;

public class FindReplaceDialog : Window
{
    private readonly TextEditor _editor;
    private readonly bool _showReplace;
    private TextBox? _findTextBox;
    private TextBox? _replaceTextBox;
    
    public FindReplaceDialog(TextEditor editor, bool showReplace)
    {
        _editor = editor;
        _showReplace = showReplace;
        
        Title = showReplace ? "Find and Replace" : "Find";
        Width = 450;
        Height = showReplace ? 180 : 130;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        
        SetupUI();
    }
    
    private void SetupUI()
    {
        var panel = new StackPanel { Margin = new Thickness(10), Spacing = 10 };
        
        var findPanel = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        findPanel.Children.Add(new TextBlock { Text = "Find:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        Grid.SetColumn(findPanel.Children[0], 0);
        _findTextBox = new TextBox();
        findPanel.Children.Add(_findTextBox);
        Grid.SetColumn(_findTextBox, 1);
        panel.Children.Add(findPanel);
        
        if (_showReplace)
        {
            var replacePanel = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            replacePanel.Children.Add(new TextBlock { Text = "Replace:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            Grid.SetColumn(replacePanel.Children[0], 0);
            _replaceTextBox = new TextBox();
            replacePanel.Children.Add(_replaceTextBox);
            Grid.SetColumn(_replaceTextBox, 1);
            panel.Children.Add(replacePanel);
        }
        
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        
        var findNextBtn = new Button { Content = "Find Next" };
        findNextBtn.Click += FindNext_Click;
        buttonPanel.Children.Add(findNextBtn);
        
        if (_showReplace)
        {
            var replaceBtn = new Button { Content = "Replace" };
            replaceBtn.Click += Replace_Click;
            buttonPanel.Children.Add(replaceBtn);
            
            var replaceAllBtn = new Button { Content = "Replace All" };
            replaceAllBtn.Click += ReplaceAll_Click;
            buttonPanel.Children.Add(replaceAllBtn);
        }
        
        var closeBtn = new Button { Content = "Close" };
        closeBtn.Click += (_, _) => Close();
        buttonPanel.Children.Add(closeBtn);
        
        panel.Children.Add(buttonPanel);
        Content = panel;
    }
    
    private void FindNext_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_findTextBox?.Text))
        {
            var index = _editor.Text.IndexOf(_findTextBox.Text, _editor.CaretOffset + 1, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                _editor.CaretOffset = index;
                _editor.SelectionStart = index;
                _editor.SelectionLength = _findTextBox.Text.Length;
            }
        }
    }
    
    private void Replace_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_findTextBox?.Text) && _replaceTextBox != null)
        {
            var selected = _editor.SelectedText;
            if (selected.Equals(_findTextBox.Text, StringComparison.OrdinalIgnoreCase))
            {
                _editor.SelectedText = _replaceTextBox.Text;
            }
            FindNext_Click(sender, e);
        }
    }
    
    private void ReplaceAll_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_findTextBox?.Text) && _replaceTextBox != null)
        {
            _editor.Text = _editor.Text.Replace(_findTextBox.Text, _replaceTextBox.Text);
        }
    }
}
