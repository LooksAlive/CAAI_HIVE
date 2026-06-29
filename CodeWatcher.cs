using System;
using System.IO;
using System.Threading;

namespace CAAI;

public class CodeWatcher
{
    private readonly FileSystemWatcher _watcher;
    private readonly Action<string> _onChanged;
    private Timer? _debounceTimer;
    private string? _lastFile;

    public CodeWatcher(string path, Action<string> onChanged)
    {
        _onChanged = onChanged;

        _watcher = new FileSystemWatcher(path)
        {
            Filter = "*.cs",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite
        };

        _watcher.Changed += OnChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        _lastFile = e.FullPath;

        // debounce (avoid multiple triggers)
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(_ =>
        {
            _onChanged(_lastFile);
        }, null, 300, Timeout.Infinite);
    }
}