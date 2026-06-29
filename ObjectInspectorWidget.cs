using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.CodeAnalysis.Scripting;

namespace CAAI;

public class ObjectNode
{
    public string Name { get; set; }
    public string Type { get; set; }
    public string Value { get; set; }
    public object? RawValue { get; set; }
    public List<ObjectNode>? Children { get; private set; }

    public ObjectNode(string name, object? value)
    {
        Name = name;
        RawValue = value;
        Type = value?.GetType().Name ?? "null";
        Value = value?.ToString() ?? "null";

        // Only allow expansion if it's not a primitive/string
        if (value != null && !value.GetType().IsPrimitive && value is not string)
        {
            Children = new List<ObjectNode>(); // Acts as a flag for the TreeView
        }
    }

    public void LoadChildren()
    {
        if (RawValue == null || Children == null || Children.Count > 0) return;

        var type = RawValue.GetType();

        // Get all public properties
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            try { Children.Add(new ObjectNode(prop.Name, prop.GetValue(RawValue))); }
            catch { /* Handle getter exceptions */ }
        }
    }
}

public class ObjectInspectorWidget : UserControl
{
    private readonly TreeView _tree;
    private readonly ObservableCollection<ObjectNode> _rootNodes = [];

    public ObjectInspectorWidget()
    {
        _tree = new TreeView
        {
            ItemsSource = _rootNodes,
            ItemTemplate = new FuncTreeDataTemplate<ObjectNode>(
                _ => true,
                (node, _) => new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = {
                        new TextBlock { Text = node.Name, Foreground = Brushes.LightBlue, Margin = new Thickness(0,0,5,0) },
                        new TextBlock { Text = $"({node.Type})", Foreground = Brushes.Gray, FontSize = 10, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = $" = {node.Value}", Foreground = Brushes.White, Margin = new Thickness(5,0,0,0) }
                    }
                },
                node =>
                {
                    node.LoadChildren(); // Lazy load on expand
                    return node.Children ?? [];
                }
            )
        };
        Content = _tree;
    }

    public void Inspect(string name, object obj)
    {
        _rootNodes.Clear();
        _rootNodes.Add(new ObjectNode(name, obj));
    }

    public void UpdateVariables(IEnumerable<ScriptVariable> variables)
    {
        _rootNodes.Clear();
        foreach (var v in variables)
        {
            _rootNodes.Add(new ObjectNode(v.Name, v.Value));
        }
    }
}
