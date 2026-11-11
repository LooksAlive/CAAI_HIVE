using System.Text.Json;

namespace CAAI;

public sealed record SourceBreakpoint(string FilePath, int Line, bool Enabled = true, string? Condition = null,
    bool Verified = false, string? Message = null);

public sealed class BreakpointStore
{
    private readonly List<SourceBreakpoint> _items = [];
    private readonly string _storagePath;
    private readonly object _gate = new();
    public event Action<string, bool>? Changed;
    public event Action<string>? PersistenceFailed;
    public string? LoadError { get; }
    public IReadOnlyList<SourceBreakpoint> Items { get { lock (_gate) return _items.ToArray(); } }

    public BreakpointStore(string? storagePath = null)
    {
        _storagePath = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CAAI", "debugging", "breakpoints.json");
        try
        {
            if (!File.Exists(_storagePath)) return;
            foreach (var item in JsonSerializer.Deserialize<SourceBreakpoint[]>(File.ReadAllText(_storagePath)) ?? [])
            {
                if (item.Line < 1 || string.IsNullOrWhiteSpace(item.FilePath)) continue;
                var normalized = item with { FilePath = Path.GetFullPath(item.FilePath), Verified = false, Message = null };
                if (!_items.Any(existing => SameFile(existing.FilePath, normalized.FilePath) && existing.Line == normalized.Line))
                    _items.Add(normalized);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LoadError = $"Could not load breakpoints: {exception.Message}";
        }
    }

    public static bool SameFile(string first, string second) => string.Equals(first, second,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void Toggle(string filePath, int line)
    {
        if (line < 1) return;
        filePath = Path.GetFullPath(filePath);
        lock (_gate)
        {
            var index = _items.FindIndex(item => SameFile(item.FilePath, filePath) && item.Line == line);
            if (index >= 0) _items.RemoveAt(index);
            else _items.Add(new SourceBreakpoint(filePath, line));
        }
        SaveAndNotify(filePath);
    }

    public void Update(SourceBreakpoint breakpoint, bool enabled, string? condition)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(item => SameFile(item.FilePath, breakpoint.FilePath) && item.Line == breakpoint.Line);
            if (index < 0) return;
            _items[index] = breakpoint with { Enabled = enabled, Condition = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim(), Verified = false, Message = null };
        }
        SaveAndNotify(breakpoint.FilePath);
    }

    public void Relocate(string filePath, IReadOnlyDictionary<int, int> lines)
    {
        var changed = false;
        lock (_gate)
        {
            for (var index = _items.Count - 1; index >= 0; index--)
            {
                var item = _items[index];
                if (!SameFile(item.FilePath, filePath)) continue;
                var newLine = lines.GetValueOrDefault(item.Line);
                if (newLine == item.Line) continue;
                changed = true;
                if (newLine <= 0) _items.RemoveAt(index);
                else _items[index] = item with { Line = newLine, Verified = false, Message = null };
            }
            if (changed)
            {
                var unique = _items.DistinctBy(item => (OperatingSystem.IsWindows() ? item.FilePath.ToUpperInvariant() : item.FilePath, item.Line)).ToArray();
                _items.Clear();
                _items.AddRange(unique);
            }
        }
        if (changed) SaveAndNotify(filePath);
    }

    public void SetVerification(string filePath, int line, bool verified, string? message)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(item => SameFile(item.FilePath, filePath) && item.Line == line);
            if (index < 0) return;
            _items[index] = _items[index] with { Verified = verified, Message = message };
        }
        Changed?.Invoke(filePath, false);
    }

    public void ResetVerification()
    {
        string[] files;
        lock (_gate)
        {
            files = _items.Select(item => item.FilePath).Distinct().ToArray();
            for (var index = 0; index < _items.Count; index++)
                _items[index] = _items[index] with { Verified = false, Message = null };
        }
        foreach (var file in files) Changed?.Invoke(file, false);
    }

    private void SaveAndNotify(string filePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_storagePath))!);
            var json = JsonSerializer.Serialize(Items.Select(item => new { item.FilePath, item.Line, item.Enabled, item.Condition }),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storagePath + ".tmp", json);
            File.Move(_storagePath + ".tmp", _storagePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            PersistenceFailed?.Invoke($"Could not save breakpoints: {exception.Message}");
        }
        Changed?.Invoke(filePath, true);
    }
}