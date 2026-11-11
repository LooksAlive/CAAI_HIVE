using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace CAAI;

public enum DebugSessionState { Idle, Building, Starting, Running, Paused, Stopping, Faulted }
public sealed record DebugStackFrame(int Id, string Name, string? FilePath, int Line, int Column)
{
    public override string ToString() => $"{Name} ({(FilePath == null ? "external code" : Path.GetFileName(FilePath))}:{Line})";
}
public sealed record DebugThread(int Id, string Name)
{
    public override string ToString() => $"{Id}: {Name}";
}

public sealed class DebugSession : IAsyncDisposable
{
    private readonly BreakpointStore _breakpoints;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _breakpointGate = new(1, 1);
    private readonly Dictionary<int, (string FilePath, int Line)> _adapterBreakpoints = new();
    private DapClient? _client;
    private CancellationTokenSource? _sessionCancellation;
    private TaskCompletionSource _initialized = NewCompletion();
    private volatile DebugSessionState _state;
    private int _stopVersion;
    private int _frameVersion;
    private int _disposed;
    private int? _threadId;
    private DebugStackFrame? _frame;

    public DebugSessionState State => _state;
    public bool IsActive => State is DebugSessionState.Building or DebugSessionState.Starting or DebugSessionState.Running or DebugSessionState.Paused or DebugSessionState.Stopping;
    public DebugLaunchConfiguration? Configuration { get; private set; }
    public int? ThreadId => _threadId;
    public DebugStackFrame? SelectedFrame => _frame;
    public IReadOnlyList<DebugStackFrame> Frames { get; private set; } = [];
    public IReadOnlyList<DebugThread> Threads { get; private set; } = [];
    public event Action<DebugSessionState, string>? StateChanged;
    public event Action<string, bool>? Output;
    public event Action? StackChanged;
    public event Action<DebugStackFrame?>? FrameChanged;
    public event Action<IReadOnlyList<ObjectNode>, string>? VariablesChanged;

    public DebugSession(BreakpointStore breakpoints)
    {
        _breakpoints = breakpoints;
        _breakpoints.Changed += OnBreakpointsChanged;
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void OnUI(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
    private void SetState(DebugSessionState state, string message)
    {
        _state = state;
        OnUI(() => StateChanged?.Invoke(state, message));
    }
    private void Write(string text, bool error = false) => OnUI(() => Output?.Invoke(text, error));

    public async Task StartAsync(DebugLaunchConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _lifecycle.WaitAsync(cancellationToken);
        var started = false;
        try
        {
            if (IsActive) throw new InvalidOperationException("A debug session is already active.");
            started = true;
            Configuration = configuration;
            var adapter = configuration.ResolveAdapter();
            _sessionCancellation?.Dispose();
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _sessionCancellation.Token;
            SetState(DebugSessionState.Building, "Building Debug configuration...");
            var project = Path.GetFullPath(configuration.ProjectPath);
            if (!File.Exists(project) || !project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select an executable .csproj project to debug.");
            var directory = Path.GetDirectoryName(project)!;
            var properties = new List<string> { "-p:Configuration=Debug", "-p:DebugType=portable", "-p:DebugSymbols=true", "-p:Optimize=false" };
            if (!string.IsNullOrWhiteSpace(configuration.TargetFramework)) properties.Add($"-p:TargetFramework={configuration.TargetFramework}");
            await RunDotnetAsync(["build", project, "--nologo", .. properties], directory, token, true);
            var targetJson = await RunDotnetAsync(["msbuild", project, "-nologo", "-getProperty:TargetPath,OutputType,TargetFramework,TargetFrameworks", .. properties], directory, token, false);
            using var target = JsonDocument.Parse(targetJson);
            var values = target.RootElement.GetProperty("Properties");
            if (String(values, "OutputType") is not ("Exe" or "WinExe"))
                throw new InvalidOperationException("The selected project is a library, not an executable.");
            var program = String(values, "TargetPath");
            if (string.IsNullOrWhiteSpace(program) || !File.Exists(program))
                throw new InvalidOperationException("No executable assembly found. For multi-targeted projects, set TargetFramework in .caai/launch.json.");
            if (!File.Exists(Path.ChangeExtension(program, ".pdb")))
                throw new InvalidOperationException("The target has no PDB debug symbols.");

            SetState(DebugSessionState.Starting, $"Starting {Path.GetFileName(program)}...");
            _initialized = NewCompletion();
            _adapterBreakpoints.Clear();
            var client = new DapClient(adapter);
            _client = client;
            client.EventReceived += (name, body) => OnEvent(client, name, body);
            client.ErrorOutput += text => Write(text, true);
            client.Closed += failure =>
            {
                if (ReferenceEquals(_client, client) && State is not (DebugSessionState.Idle or DebugSessionState.Stopping or DebugSessionState.Faulted))
                    _ = EndUnexpectedAsync(client, failure);
            };
            var capabilities = await client.RequestAsync("initialize", new
            {
                clientID = "caai", clientName = "CAAI IDE", adapterID = "coreclr",
                pathFormat = "path", linesStartAt1 = true, columnsStartAt1 = true,
                supportsVariableType = true, supportsRunInTerminalRequest = false, supportsVariablePaging = true
            }, token);
            var supportedFilters = capabilities.TryGetProperty("exceptionBreakpointFilters", out var exceptionFilters)
                ? exceptionFilters.EnumerateArray().Select(filter => String(filter, "filter")).ToArray() : [];
            if (configuration.ExceptionFilters.Any(filter => !supportedFilters.Contains(filter)))
                throw new InvalidOperationException($"Unsupported exception filter. Adapter supports: {string.Join(", ", supportedFilters)}");
            var cwd = string.IsNullOrWhiteSpace(configuration.WorkingDirectory) ? directory :
                Path.GetFullPath(configuration.WorkingDirectory, directory);
            var launch = client.RequestAsync("launch", new
            {
                name = Path.GetFileNameWithoutExtension(project), type = "coreclr", request = "launch",
                program, cwd, args = configuration.Arguments, env = configuration.Environment,
                stopAtEntry = configuration.StopAtEntry, justMyCode = configuration.JustMyCode,
                console = "internalConsole"
            }, token);
            try
            {
                await _initialized.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                foreach (var file in _breakpoints.Items.Select(item => item.FilePath).Distinct())
                    await SendBreakpointsAsync(client, file, token);
                await client.RequestAsync("setExceptionBreakpoints", new { filters = configuration.ExceptionFilters }, token);
                await client.RequestAsync("configurationDone", new { }, token);
                await launch;
            }
            catch
            {
                try { await launch; } catch { }
                throw;
            }
            if (ReferenceEquals(_client, client) && State == DebugSessionState.Starting)
                SetState(DebugSessionState.Running, "Running");
        }
        catch (Exception exception)
        {
            if (!started) throw;
            await CleanupAsync();
            if (exception is OperationCanceledException)
                SetState(DebugSessionState.Idle, "Debug launch cancelled");
            else
            {
                SetState(DebugSessionState.Faulted, exception.Message);
                Write(exception.Message + Environment.NewLine, true);
            }
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    private async Task<string> RunDotnetAsync(string[] arguments, string directory, CancellationToken token, bool log)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.Start();
        var output = new StringBuilder();
        async Task ReadAsync(StreamReader reader, bool error)
        {
            while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (!error) output.AppendLine(line);
                if (log || error) Write(line + Environment.NewLine, error);
            }
        }
        var readers = Task.WhenAll(ReadAsync(process.StandardOutput, false), ReadAsync(process.StandardError, true));
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await readers.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"dotnet {arguments[0]} failed (exit code {process.ExitCode}). See Terminal output.");
            return output.ToString();
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await readers.ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private void OnEvent(DapClient client, string name, JsonElement body)
    {
        if (!ReferenceEquals(_client, client)) return;
        switch (name)
        {
            case "initialized": _initialized.TrySetResult(); break;
            case "output": Write(String(body, "output"), String(body, "category") == "stderr"); break;
            case "stopped": _ = HandleStoppedAsync(client, body); break;
            case "continued": InvalidatePause(); SetState(DebugSessionState.Running, "Running"); break;
            case "exited": Write($"Debuggee exited with code {Int(body, "exitCode")}.{Environment.NewLine}"); break;
            case "terminated": _ = FinishAsync(client); break;
            case "breakpoint":
                if (body.TryGetProperty("breakpoint", out var breakpoint))
                {
                    OnUI(() =>
                    {
                        if (_adapterBreakpoints.TryGetValue(Int(breakpoint, "id"), out var source))
                            _breakpoints.SetVerification(source.FilePath, source.Line, Bool(breakpoint, "verified"), String(breakpoint, "message"));
                    });
                }
                break;
        }
    }

    private async Task HandleStoppedAsync(DapClient client, JsonElement body)
    {
        try
        {
            var version = Interlocked.Increment(ref _stopVersion);
            SetState(DebugSessionState.Paused, $"Paused: {String(body, "reason")} {String(body, "description")}".Trim());
            var response = await client.RequestAsync("threads");
            if (!IsCurrentPause(client, version)) return;
            Threads = response.GetProperty("threads").EnumerateArray().Select(thread => new DebugThread(Int(thread, "id"), String(thread, "name"))).ToArray();
            var threadId = Int(body, "threadId");
            if (threadId == 0) threadId = Threads.FirstOrDefault()?.Id ?? 0;
            if (threadId != 0) await SelectThreadAsync(threadId);
            else OnUI(() => StackChanged?.Invoke());
        }
        catch (Exception exception) { Write($"Could not inspect stopped process: {exception.Message}{Environment.NewLine}", true); }
    }

    public async Task SelectThreadAsync(int threadId)
    {
        var client = RequirePaused();
        var version = Volatile.Read(ref _stopVersion);
        var response = await client.RequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 100 });
        if (!IsCurrentPause(client, version)) return;
        _threadId = threadId;
        Frames = response.GetProperty("stackFrames").EnumerateArray().Select(frame => new DebugStackFrame(
            Int(frame, "id"), String(frame, "name"), frame.TryGetProperty("source", out var source) ? String(source, "path") : null,
            Int(frame, "line"), Int(frame, "column"))).ToArray();
        OnUI(() => StackChanged?.Invoke());
        if (Frames.FirstOrDefault() is { } first) await SelectFrameAsync(first);
    }

    public async Task SelectFrameAsync(DebugStackFrame frame)
    {
        var client = RequirePaused();
        var stopVersion = Volatile.Read(ref _stopVersion);
        var frameVersion = Interlocked.Increment(ref _frameVersion);
        _frame = frame;
        OnUI(() => FrameChanged?.Invoke(frame));
        var scopes = await client.RequestAsync("scopes", new { frameId = frame.Id });
        var nodes = new List<ObjectNode>();
        foreach (var scope in scopes.GetProperty("scopes").EnumerateArray())
        {
            var reference = Int(scope, "variablesReference");
            if (reference == 0) continue;
            nodes.Add(new ObjectNode(String(scope, "name"), "Scope", "", token => LoadVariablesAsync(client, reference, stopVersion, token)));
        }
        if (IsCurrentPause(client, stopVersion) && frameVersion == Volatile.Read(ref _frameVersion))
            OnUI(() => VariablesChanged?.Invoke(nodes, frame.Name));
    }

    private async Task<IReadOnlyList<ObjectNode>> LoadVariablesAsync(DapClient client, int reference, int version, CancellationToken token)
    {
        if (!IsCurrentPause(client, version)) throw new InvalidOperationException("This snapshot is no longer paused.");
        var response = await client.RequestAsync("variables", new { variablesReference = reference }, token);
        if (!IsCurrentPause(client, version)) throw new InvalidOperationException("This snapshot is no longer paused.");
        var variables = response.GetProperty("variables").EnumerateArray().ToArray();
        var nodes = variables.Take(200).Select(variable => VariableNode(client, variable, version)).ToList();
        if (variables.Length > 200) nodes.Add(new ObjectNode("More variables", "", $"{variables.Length - 200} additional variables omitted"));
        return nodes;
    }

    private ObjectNode VariableNode(DapClient client, JsonElement variable, int version)
    {
        var reference = Int(variable, "variablesReference");
        return new ObjectNode(String(variable, "name"), String(variable, "type"), String(variable, "value"),
            reference == 0 ? null : token => LoadVariablesAsync(client, reference, version, token));
    }

    public async Task<ObjectNode> EvaluateAsync(string expression, CancellationToken token = default)
    {
        var client = RequirePaused();
        if (_frame == null) throw new InvalidOperationException("Select a stack frame first.");
        var version = Volatile.Read(ref _stopVersion);
        var result = await client.RequestAsync("evaluate", new { expression, frameId = _frame.Id, context = "watch" }, token);
        if (!IsCurrentPause(client, version)) throw new InvalidOperationException("Execution resumed during evaluation.");
        var reference = Int(result, "variablesReference");
        return new ObjectNode(expression, String(result, "type"), String(result, "result"),
            reference == 0 ? null : cancellation => LoadVariablesAsync(client, reference, version, cancellation));
    }

    private DapClient RequirePaused() => State == DebugSessionState.Paused && _client != null
        ? _client : throw new InvalidOperationException("The debuggee must be paused.");
    private bool IsCurrentPause(DapClient client, int version) => ReferenceEquals(client, _client) && State == DebugSessionState.Paused && version == Volatile.Read(ref _stopVersion);

    public async Task ContinueAsync(string command = "continue")
    {
        var client = RequirePaused();
        if (_threadId == null) throw new InvalidOperationException("Select a thread first.");
        var version = Volatile.Read(ref _stopVersion);
        await client.RequestAsync(command, new { threadId = _threadId.Value });
        if (IsCurrentPause(client, version))
        {
            InvalidatePause();
            SetState(DebugSessionState.Running, "Running");
        }
    }

    public async Task PauseAsync()
    {
        if (State != DebugSessionState.Running || _client == null) return;
        var client = _client;
        var threads = await client.RequestAsync("threads");
        var first = threads.GetProperty("threads").EnumerateArray().FirstOrDefault();
        var threadId = Int(first, "id");
        if (threadId != 0) await client.RequestAsync("pause", new { threadId });
    }

    private void InvalidatePause()
    {
        Interlocked.Increment(ref _stopVersion);
        Interlocked.Increment(ref _frameVersion);
        _frame = null;
        _threadId = null;
        Frames = [];
        Threads = [];
        OnUI(() => { FrameChanged?.Invoke(null); StackChanged?.Invoke(); VariablesChanged?.Invoke([], "Not paused"); });
    }

    private void OnBreakpointsChanged(string filePath, bool userChange)
    {
        if (userChange && _client is { } client && State is DebugSessionState.Running or DebugSessionState.Paused)
            _ = UpdateBreakpointsAsync(client, filePath);
    }
    private async Task UpdateBreakpointsAsync(DapClient client, string filePath)
    {
        try { await SendBreakpointsAsync(client, filePath, _sessionCancellation?.Token ?? default); }
        catch (Exception exception) { Write($"Breakpoint update failed: {exception.Message}{Environment.NewLine}", true); }
    }
    private async Task SendBreakpointsAsync(DapClient client, string filePath, CancellationToken token)
    {
        await _breakpointGate.WaitAsync(token);
        try
        {
            if (!ReferenceEquals(client, _client)) return;
            var requested = _breakpoints.Items.Where(item => BreakpointStore.SameFile(item.FilePath, filePath) && item.Enabled).OrderBy(item => item.Line).ToArray();
            var response = await client.RequestAsync("setBreakpoints", new
            {
                source = new { path = filePath, name = Path.GetFileName(filePath) },
                breakpoints = requested.Select(item => new { line = item.Line, condition = item.Condition }).ToArray(), sourceModified = false
            }, token);
            var actual = response.GetProperty("breakpoints").EnumerateArray().ToArray();
            OnUI(() =>
            {
                if (!ReferenceEquals(client, _client)) return;
                foreach (var key in _adapterBreakpoints.Where(pair => BreakpointStore.SameFile(pair.Value.FilePath, filePath)).Select(pair => pair.Key).ToArray())
                    _adapterBreakpoints.Remove(key);
                for (var index = 0; index < requested.Length; index++)
                {
                    var item = index < actual.Length ? actual[index] : default;
                    _breakpoints.SetVerification(filePath, requested[index].Line, Bool(item, "verified"), String(item, "message"));
                    if (Int(item, "id") is var id && id != 0) _adapterBreakpoints[id] = (filePath, requested[index].Line);
                }
            });
        }
        finally { _breakpointGate.Release(); }
    }

    public async Task StopAsync()
    {
        _sessionCancellation?.Cancel();
        await _lifecycle.WaitAsync();
        try
        {
            SetState(DebugSessionState.Stopping, "Stopping...");
            if (_client is { } client)
            {
                try { await client.RequestAsync("disconnect", new { terminateDebuggee = true }).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception exception) { Write($"Debugger disconnect: {exception.Message}{Environment.NewLine}", true); }
            }
            await CleanupAsync();
            SetState(DebugSessionState.Idle, "Debug session ended");
        }
        finally { _lifecycle.Release(); }
    }

    private async Task CleanupAsync()
    {
        var client = _client;
        _client = null;
        InvalidatePause();
        if (client != null) await client.DisposeAsync();
        OnUI(_breakpoints.ResetVerification);
    }
    private async Task FinishAsync(DapClient client)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!ReferenceEquals(client, _client)) return;
            await CleanupAsync();
            SetState(DebugSessionState.Idle, "Debug session ended");
        }
        catch (Exception exception) { Write(exception.Message + Environment.NewLine, true); }
        finally { _lifecycle.Release(); }
    }
    private async Task EndUnexpectedAsync(DapClient client, Exception? failure)
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!ReferenceEquals(client, _client)) return;
            await CleanupAsync();
            SetState(DebugSessionState.Faulted, failure?.Message ?? "Debug adapter disconnected.");
        }
        catch (Exception exception) { Write(exception.Message + Environment.NewLine, true); }
        finally { _lifecycle.Release(); }
    }

    private static int Int(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : 0;
    private static string String(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Bool(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _breakpoints.Changed -= OnBreakpointsChanged;
        await StopAsync();
        _sessionCancellation?.Dispose();
    }
}