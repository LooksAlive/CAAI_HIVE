using Avalonia.Controls;
using Avalonia.Markup.Xaml;

public abstract class CAAIUserControl : UserControl
{
	public virtual void ReloadLayout()
	{
		try
		{
			// Remove current logical content
			Content = null;

			// Reload AXAML into this instance
			AvaloniaXamlLoader.Load(this);

			// Refresh layout
			InvalidateMeasure();
			InvalidateArrange();
			InvalidateVisual();

			OnLayoutReloaded();
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine(
				$"ReloadLayout failed: {ex}");
		}
	}

	protected virtual void OnLayoutReloaded()
	{
	}
}