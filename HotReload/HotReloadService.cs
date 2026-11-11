using System.Reflection.Metadata;
using System.Threading;
using Avalonia.Threading;

[assembly: MetadataUpdateHandler(
    typeof(CAAI.HotReloadService))]

namespace CAAI;

public static class HotReloadService
{
    private static WeakReference<HotReloadRoot>? _root;

    private static int _rebuildPending;

    public static void Register(HotReloadRoot root)
    {
        _root = new WeakReference<HotReloadRoot>(root);
    }

    // Called by the .NET Hot Reload infrastructure.
    public static void ClearCache(Type[]? updatedTypes)
    {
        // Nothing to invalidate in our implementation.
    }

    // Called after Hot Reload applies changes.
    public static void UpdateApplication(Type[]? updatedTypes)
    {
        if (Interlocked.Exchange(ref _rebuildPending, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_root?.TryGetTarget(out var root) == true)
                {
                    root.Rebuild();
                }
            }
            catch (Exception ex)
            {
                // Keep the existing UI if rebuilding fails.
                // Replace with your application logger.
                System.Diagnostics.Debug.WriteLine(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _rebuildPending, 0);
            }
        });
    }
}