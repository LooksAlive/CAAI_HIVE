using System;
using System.IO;
using System.Threading;

namespace CAAI;

public class CodeWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;
    private readonly Action<string> _onChanged;
    private Timer? _debounceTimer;
    private int _watcherGeneration;

    public CodeWatcher(string path, Action<string> onChanged)
    {
        _onChanged = onChanged;
        ChangePath(path);
    }

    public void ChangePath(string path)
    {
        Interlocked.Increment(ref _watcherGeneration);
        _debounceTimer?.Dispose();
        _debounceTimer = null;
        _watcher?.Dispose();
        _watcher = null;
        if (!Directory.Exists(path)) return;

        _watcher = new FileSystemWatcher(path)
        {
            Filter = "*.cs",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite
        };

        _watcher.Changed += OnChanged;
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _watcherGeneration);
        _debounceTimer?.Dispose();
        _watcher?.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        var file = e.FullPath;
        var generation = Volatile.Read(ref _watcherGeneration);

        // debounce (avoid multiple triggers)
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ =>
        {
            if (generation == Volatile.Read(ref _watcherGeneration))
                _onChanged(file);
        }, null, 300, Timeout.Infinite);
    }
}