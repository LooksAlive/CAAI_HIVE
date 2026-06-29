using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AvaloniaEdit;

public class GoToLineDialog : Window
{
    private readonly TextEditor _editor;
    private TextBox? _lineTextBox;
    
    public GoToLineDialog(TextEditor editor)
    {
        _editor = editor;
        Title = "Go to Line";
        Width = 300;
        Height = 120;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        
        SetupUI();
    }
    
    private void SetupUI()
    {
        var panel = new StackPanel { Margin = new Thickness(10), Spacing = 10 };
        
        panel.Children.Add(new TextBlock { Text = $"Line number (1-{_editor.Document.LineCount}):" });
        
        _lineTextBox = new TextBox();
        panel.Children.Add(_lineTextBox);
        
        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        
        var goBtn = new Button { Content = "Go" };
        goBtn.Click += Go_Click;
        buttonPanel.Children.Add(goBtn);
        
        var cancelBtn = new Button { Content = "Cancel" };
        cancelBtn.Click += (_, _) => Close();
        buttonPanel.Children.Add(cancelBtn);
        
        panel.Children.Add(buttonPanel);
        Content = panel;
    }
    
    private void Go_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (int.TryParse(_lineTextBox?.Text, out int line) && line >= 1 && line <= _editor.Document.LineCount)
        {
            _editor.CaretOffset = _editor.Document.GetOffset(line, 1);
            _editor.ScrollToLine(line);
            Close();
        }
    }
}
