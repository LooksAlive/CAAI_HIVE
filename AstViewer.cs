using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Layout;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CAAI
{
    public class AstViewer : UserControl
    {
        private TreeView _treeView = null!;
        private Grid? _mainGrid;
        private TextBlock? _statusText;
        private ProgressBar? _loadingIndicator;
        private SyntaxNode? _currentSyntaxTree;

        public static readonly StyledProperty<SyntaxNode?> SyntaxTreeProperty =
            AvaloniaProperty.Register<AstViewer, SyntaxNode?>(nameof(SyntaxTree));

        public SyntaxNode? SyntaxTree
        {
            get => GetValue(SyntaxTreeProperty);
            set => SetValue(SyntaxTreeProperty, value);
        }

        static AstViewer()
        {
            SyntaxTreeProperty.Changed.AddClassHandler<AstViewer>((x, e) => x.OnSyntaxTreeChanged(e));
        }

        public AstViewer()
        {
            BuildControlHierarchy();
        }

        private void BuildControlHierarchy()
        {
            _mainGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,*"),
                Margin = new Thickness(0)
            };

            // Toolbar
            var toolbar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Padding = new Thickness(5)
            };

            var toolbarPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10
            };

            var expandAllBtn = new Button
            {
                Content = "📂 Expand All",
                Margin = new Thickness(2),
                Padding = new Thickness(8, 4),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
                Foreground = Brushes.White
            };

            var collapseAllBtn = new Button
            {
                Content = "📁 Collapse All",
                Margin = new Thickness(2),
                Padding = new Thickness(8, 4),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
                Foreground = Brushes.White
            };

            var refreshBtn = new Button
            {
                Content = "🔄 Refresh",
                Margin = new Thickness(2),
                Padding = new Thickness(8, 4),
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 65)),
                Foreground = Brushes.White
            };

            expandAllBtn.Click += (s, e) => ExpandAll();
            collapseAllBtn.Click += (s, e) => CollapseAll();
            refreshBtn.Click += (s, e) => Refresh();

            toolbarPanel.Children.Add(expandAllBtn);
            toolbarPanel.Children.Add(collapseAllBtn);
            toolbarPanel.Children.Add(refreshBtn);

            toolbar.Child = toolbarPanel;
            _mainGrid.Children.Add(toolbar);
            Grid.SetRow(toolbar, 0);

            // Status bar
            var statusBar = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 45, 48)),
                Padding = new Thickness(5)
            };

            _statusText = new TextBlock
            {
                Foreground = Brushes.LightGray,
                FontSize = 11
            };
            statusBar.Child = _statusText;
            _mainGrid.Children.Add(statusBar);
            Grid.SetRow(statusBar, 1);

            // Loading indicator
            _loadingIndicator = new ProgressBar
            {
                IsIndeterminate = true,
                IsVisible = false,
                Height = 20,
                Margin = new Thickness(10)
            };

            // TreeView
            _treeView = new TreeView
            {
                Background = new SolidColorBrush(Color.FromRgb(35, 35, 35)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };

            // Create container for loading indicator and tree
            var container = new Grid
            {
                RowDefinitions = new RowDefinitions("*")
            };
            container.Children.Add(_treeView);
            container.Children.Add(_loadingIndicator);

            _mainGrid.Children.Add(container);
            Grid.SetRow(container, 2);

            Content = _mainGrid;
        }

        private void OnSyntaxTreeChanged(AvaloniaPropertyChangedEventArgs e)
        {
            if (e.NewValue is SyntaxNode syntaxNode)
            {
                _currentSyntaxTree = syntaxNode;
                BuildTree(syntaxNode);
                UpdateStatus();
            }
            else if (e.NewValue == null)
            {
                _treeView.ItemsSource = null;
            }
        }

        private void UpdateStatus()
        {
            if (_statusText != null && _currentSyntaxTree != null)
            {
                var nodeCount = _currentSyntaxTree.DescendantNodesAndTokens().Count();
                _statusText.Text = $"Total nodes: {nodeCount} | Root: {_currentSyntaxTree.Kind()}";
            }
        }

        private async void BuildTree(SyntaxNode rootNode)
        {
            if (_loadingIndicator != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => _loadingIndicator.IsVisible = true);
            }

            // Create the tree on UI thread
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    var rootItem = ConvertToTreeViewItem(rootNode);
                    _treeView.ItemsSource = new[] { rootItem };
                }
                catch (Exception ex)
                {
                    _treeView.ItemsSource = new[] 
                    { 
                        new TextBlock 
                        { 
                            Text = $"Error: {ex.Message}", 
                            Foreground = Brushes.Red,
                            Margin = new Thickness(10)
                        } 
                    };
                }
                finally
                {
                    if (_loadingIndicator != null)
                    {
                        _loadingIndicator.IsVisible = false;
                    }
                }
            });
        }

        private TreeViewItem ConvertToTreeViewItem(SyntaxNodeOrToken nodeOrToken)
        {
            var item = new TreeViewItem
            {
                Header = CreateHeader(nodeOrToken),
                Tag = nodeOrToken
            };

            if (nodeOrToken.IsNode)
            {
                var node = nodeOrToken.AsNode();
                if (node != null)
                {
                    var children = node.ChildNodesAndTokens().ToList();
                    if (children.Any())
                    {
                        foreach (var child in children)
                        {
                            item.Items.Add(ConvertToTreeViewItem(child));
                        }
                    }
                }
            }

            return item;
        }

        private object CreateHeader(SyntaxNodeOrToken nodeOrToken)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Icon
            var icon = GetIcon(nodeOrToken);
            panel.Children.Add(icon);

            // Node kind with color
            string kindText = nodeOrToken.IsNode ? 
                nodeOrToken.AsNode()?.Kind().ToString() ?? nodeOrToken.Kind().ToString() : 
                nodeOrToken.Kind().ToString();
            
            var kindColor = GetKindColor(kindText, nodeOrToken);
            var kindLabel = new Border
            {
                Background = new SolidColorBrush(kindColor),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 2),
                Margin = new Thickness(0, 0, 4, 0)
            };

            kindLabel.Child = new TextBlock
            {
                Text = kindText,
                FontWeight = FontWeight.Bold,
                FontSize = 11,
                Foreground = kindColor.A > 0.5 ? Brushes.Black : Brushes.White
            };
            panel.Children.Add(kindLabel);

            // Span information
            if (nodeOrToken.FullSpan.Length > 0)
            {
                var spanText = new TextBlock
                {
                    Text = $"📍 {nodeOrToken.FullSpan.Start}-{nodeOrToken.FullSpan.End}",
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    FontFamily = new FontFamily("Consolas, monospace")
                };
                panel.Children.Add(spanText);
            }

            // Token value
            if (nodeOrToken.IsToken && !string.IsNullOrWhiteSpace(nodeOrToken.AsToken().Text))
            {
                var tokenText = nodeOrToken.AsToken().Text;
                var isKeyword = nodeOrToken.AsToken().IsKeyword();

                var tokenValue = new TextBlock
                {
                    Text = $"= \"{tokenText.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
                    FontSize = 11,
                    Foreground = isKeyword ? Brushes.Cyan : Brushes.LightGreen,
                    FontFamily = new FontFamily("Consolas, monospace"),
                    FontWeight = isKeyword ? FontWeight.Bold : FontWeight.Normal
                };
                panel.Children.Add(tokenValue);
            }

            // Literal value
            if (nodeOrToken.IsNode && nodeOrToken.AsNode() is LiteralExpressionSyntax literal)
            {
                var valueText = new TextBlock
                {
                    Text = $"= {literal.Token.Value}",
                    FontSize = 11,
                    Foreground = Brushes.LightSalmon,
                    FontFamily = new FontFamily("Consolas, monospace")
                };
                panel.Children.Add(valueText);
            }

            // Node name
            if (nodeOrToken.IsNode && TryGetNodeName(nodeOrToken.AsNode()!, out string? name))
            {
                var nameText = new TextBlock
                {
                    Text = $"\"{name}\"",
                    FontSize = 11,
                    Foreground = Brushes.White,
                    FontFamily = new FontFamily("Consolas, monospace"),
                    FontStyle = FontStyle.Italic
                };
                panel.Children.Add(nameText);
            }

            // Add click handler for selection
            panel.PointerPressed += (s, e) =>
            {
                OnNodeSelected(nodeOrToken);
            };

            return panel;
        }

        private Control GetIcon(SyntaxNodeOrToken nodeOrToken)
        {
            var icon = new TextBlock
            {
                FontSize = 12,
                Width = 20,
                TextAlignment = Avalonia.Media.TextAlignment.Center
            };

            if (nodeOrToken.IsNode)
            {
                var node = nodeOrToken.AsNode();
                icon.Text = node switch
                {
                    CompilationUnitSyntax => "📦",
                    NamespaceDeclarationSyntax => "📁",
                    FileScopedNamespaceDeclarationSyntax => "📁",
                    ClassDeclarationSyntax => "📘",
                    StructDeclarationSyntax => "📗",
                    InterfaceDeclarationSyntax => "📙",
                    EnumDeclarationSyntax => "🔢",
                    MethodDeclarationSyntax => "⚡",
                    PropertyDeclarationSyntax => "🔧",
                    ConstructorDeclarationSyntax => "🏗️",
                    VariableDeclarationSyntax => "📝",
                    IfStatementSyntax => "❓",
                    ForStatementSyntax => "🔄",
                    ForEachStatementSyntax => "🔄",
                    WhileStatementSyntax => "🔁",
                    ReturnStatementSyntax => "⬅️",
                    _ => "📄"
                };
                icon.Foreground = Brushes.LightGray;
            }
            else
            {
                var token = nodeOrToken.AsToken();
                if (token.IsKeyword())
                {
                    icon.Text = "🔑";
                    icon.Foreground = Brushes.LightBlue;
                }
                else
                {
                    icon.Text = "🔸";
                    icon.Foreground = Brushes.DarkGray;
                }
            }

            return icon;
        }

        private Color GetKindColor(string kind, SyntaxNodeOrToken nodeOrToken)
        {
            if (nodeOrToken.IsToken)
            {
                if (nodeOrToken.AsToken().IsKeyword())
                    return Color.FromRgb(86, 156, 214);
                return Color.FromRgb(106, 153, 85);
            }

            return kind switch
            {
                var k when k.Contains("Class") => Color.FromRgb(78, 201, 176),
                var k when k.Contains("Method") => Color.FromRgb(220, 140, 86),
                var k when k.Contains("Property") => Color.FromRgb(156, 220, 254),
                var k when k.Contains("Namespace") => Color.FromRgb(78, 154, 114),
                var k when k.Contains("Interface") => Color.FromRgb(185, 140, 214),
                var k when k.Contains("Enum") => Color.FromRgb(214, 185, 140),
                var k when k.Contains("Statement") => Color.FromRgb(140, 185, 214),
                var k when k.Contains("Expression") => Color.FromRgb(214, 140, 185),
                _ => Color.FromRgb(100, 100, 100)
            };
        }

        private bool TryGetNodeName(SyntaxNode node, out string? name)
        {
            name = null;
            
            switch (node)
            {
                case ClassDeclarationSyntax c:
                    name = c.Identifier.Text;
                    return true;
                case MethodDeclarationSyntax m:
                    name = m.Identifier.Text;
                    return true;
                case PropertyDeclarationSyntax p:
                    name = p.Identifier.Text;
                    return true;
                case VariableDeclaratorSyntax v:
                    name = v.Identifier.Text;
                    return true;
                case NamespaceDeclarationSyntax n:
                    name = n.Name.ToString();
                    return true;
                case FileScopedNamespaceDeclarationSyntax f:
                    name = f.Name.ToString();
                    return true;
                case ParameterSyntax p:
                    name = p.Identifier.Text;
                    return true;
                case ConstructorDeclarationSyntax c:
                    name = c.Identifier.Text;
                    return true;
                default:
                    return false;
            }
        }

        public void ExpandAll()
        {
            ExpandAllItems(_treeView.ItemsSource);
        }

        private void ExpandAllItems(IEnumerable? items)
        {
            if (items == null) return;
            
            foreach (var item in items)
            {
                if (item is TreeViewItem treeItem)
                {
                    treeItem.IsExpanded = true;
                    ExpandAllItems(treeItem.Items);
                }
            }
        }

        public void CollapseAll()
        {
            CollapseAllItems(_treeView.ItemsSource);
        }

        private void CollapseAllItems(IEnumerable? items)
        {
            if (items == null) return;
            
            foreach (var item in items)
            {
                if (item is TreeViewItem treeItem)
                {
                    treeItem.IsExpanded = false;
                    CollapseAllItems(treeItem.Items);
                }
            }
        }

        public void Refresh()
        {
            if (_currentSyntaxTree != null)
            {
                BuildTree(_currentSyntaxTree);
            }
        }

        public async void ParseAndDisplayCode(string code)
        {
            if (_loadingIndicator != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => _loadingIndicator.IsVisible = true);
            }

            await Task.Run(() =>
            {
                try
                {
                    var syntaxTree = CSharpSyntaxTree.ParseText(code);
                    var root = syntaxTree.GetRoot();
                    
                    Dispatcher.UIThread.Post(() =>
                    {
                        SyntaxTree = root;
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var errorMsg = new TextBlock
                        {
                            Text = $"Error: {ex.Message}",
                            Foreground = Brushes.Red,
                            Margin = new Thickness(10)
                        };
                        _treeView.ItemsSource = new[] { errorMsg };
                    });
                }
                finally
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_loadingIndicator != null)
                        {
                            _loadingIndicator.IsVisible = false;
                        }
                    });
                }
            });
        }

        private void OnNodeSelected(SyntaxNodeOrToken nodeOrToken)
        {
            var args = new AstNodeSelectedEventArgs
            {
                Node = nodeOrToken.IsNode ? nodeOrToken.AsNode() : null,
                Token = nodeOrToken.IsToken ? nodeOrToken.AsToken() : null,
                Location = nodeOrToken.GetLocation()
            };
            
            NodeSelected?.Invoke(this, args);
        }

        public event EventHandler<AstNodeSelectedEventArgs>? NodeSelected;
    }

    public class AstNodeSelectedEventArgs : EventArgs
    {
        public SyntaxNode? Node { get; set; }
        public SyntaxToken? Token { get; set; }
        public Microsoft.CodeAnalysis.Location? Location { get; set; }
    }
}