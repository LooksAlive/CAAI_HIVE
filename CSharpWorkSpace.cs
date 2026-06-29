using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace CAAI;

public class CSharpWorkSpace
{
	public ObservableCollection<WorkspaceElement> Elements { get; } = new();
	public ObservableCollection<WorkspaceRelation> Relations { get; } = new();

	public void AddElement(WorkspaceElement element)
		=> Elements.Add(element);

	public void AddRelation(
		WorkspaceElement from,
		WorkspaceElement to,
		string kind)
	{
		Relations.Add(new WorkspaceRelation(from, to, kind));
	}
}

public abstract class WorkspaceElement
{
	public string Id { get; } = Guid.NewGuid().ToString();
	public string Name { get; }

	protected WorkspaceElement(string name)
	{
		Name = name;
	}
}

public class AstElement : WorkspaceElement
{
	public StructureNode Node { get; }

	public AstElement(StructureNode node)
		: base(node.Name)
	{
		Node = node;
	}
}

public class DataElement : WorkspaceElement
{
	public object Data { get; }

	public DataElement(string name, object data)
		: base(name)
	{
		Data = data;
	}
}

public class WorkspaceRelation
{
	public WorkspaceElement From { get; }
	public WorkspaceElement To { get; }
	public string Kind { get; }

	public WorkspaceRelation(
		WorkspaceElement from,
		WorkspaceElement to,
		string kind)
	{
		From = from;
		To = to;
		Kind = kind;
	}
}

public class WorkspaceView : UserControl
{
	public CSharpWorkSpace Workspace { get; }

	public WorkspaceView(CSharpWorkSpace workspace)
	{
		Workspace = workspace;
		Content = BuildLayout();
	}

	Control BuildLayout()
	{
		var tabs = new TabControl
		{
			Items =
			{
				new TabItem
				{
					Header = "Relations",
					Content = new RelationsView(Workspace)
				},
				new TabItem
				{
					Header = "Data",
					Content = new DataView(Workspace)
				},
				new TabItem
				{
					Header = "Patterns",
					Content = new PatternView(Workspace)
				}
			}
		};

		return tabs;
	}
	
	
	
}

public class RelationsView : UserControl
{
	public RelationsView(CSharpWorkSpace workspace)
	{
		var grid = new Grid
		{
			ColumnDefinitions =
			{
				new ColumnDefinition(new GridLength(200)),
				new ColumnDefinition(new GridLength(1, GridUnitType.Star))
			}
		};

		// Elements list
		var elementsList = new ListBox
		{
			ItemsSource = workspace.Elements,
			//DisplayMemberPath = "Name"
		};

		// Relations list
		var relationsList = new ListBox
		{
			ItemsSource = workspace.Relations
		};

		relationsList.ItemTemplate = new FuncDataTemplate<WorkspaceRelation>((r, _) =>
			new TextBlock
			{
				Text = $"{r.From.Name} --[{r.Kind}]--> {r.To.Name}"
			});

		Grid.SetColumn(elementsList, 0);
		Grid.SetColumn(relationsList, 1);

		grid.Children.Add(elementsList);
		grid.Children.Add(relationsList);

		Content = grid;
	}
	
	public void AddRelationElement(
		CSharpWorkSpace workspace,
		StructureNode from,
		StructureNode to,
		string kind)
	{
		var fromEl = workspace.Elements
			.OfType<AstElement>()
			.First(e => e.Node == from);

		var toEl = workspace.Elements
			.OfType<AstElement>()
			.First(e => e.Node == to);

		workspace.AddRelation(fromEl, toEl, kind);
	}
}

public class DataView : UserControl
{
	public DataView(CSharpWorkSpace workspace)
	{
		var list = new ListBox
		{
			ItemsSource = workspace.Elements.OfType<DataElement>(),
			ItemTemplate = new FuncDataTemplate<DataElement>((d, _) =>
				new TextBlock
				{
					Text = $"{d.Name}: {d.Data}"
				})
		};

		Content = list;
	}
}

public class PatternView : UserControl
{
	public PatternView(CSharpWorkSpace workspace)
	{
		Content = new TextBlock
		{
			Text = "Pattern Engine (coming soon)",
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
	}
}
