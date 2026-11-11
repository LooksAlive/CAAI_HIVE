using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CAAI;

internal sealed class DapClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _reader;
    private readonly Task _errors;
    private int _sequence;
    private int _disposed;

    public event Action<string, JsonElement>? EventReceived;
    public event Action<string>? ErrorOutput;
    public event Action<Exception?>? Closed;

    public DapClient(string executable)
    {
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "--interpreter=vscode" }
            }
        };
        try
        {
            if (!_process.Start()) throw new IOException("Could not start the debug adapter.");
        }
        catch
        {
            _process.Dispose();
            throw;
        }
        _reader = ReadMessagesAsync();
        _errors = ReadErrorsAsync();
    }

    public async Task<JsonElement> RequestAsync(string command, object? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var sequence = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[sequence] = completion;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { seq = sequence, type = "request", command, arguments }, JsonOptions);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
            await _writer.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
                await _process.StandardInput.BaseStream.WriteAsync(payload, timeout.Token).ConfigureAwait(false);
                await _process.StandardInput.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally { _writer.Release(); }
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(sequence, out _); }
    }

    private async Task ReadMessagesAsync()
    {
        Exception? failure = null;
        try
        {
            var stream = _process.StandardOutput.BaseStream;
            var single = new byte[1];
            while (!_lifetime.IsCancellationRequested)
            {
                var header = new StringBuilder();
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    var count = await stream.ReadAsync(single, _lifetime.Token).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException("The debug adapter closed its output.");
                    header.Append((char)single[0]);
                    if (header.Length > 8192) throw new InvalidDataException("Invalid debug adapter header.");
                }
                var length = -1;
                foreach (var line in header.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0 && line[..separator].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                        length = int.Parse(line[(separator + 1)..].Trim(), CultureInfo.InvariantCulture);
                }
                if (length is < 0 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid debug adapter message size.");
                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, _lifetime.Token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(payload);
                var message = document.RootElement;
                var type = message.GetProperty("type").GetString();
                if (type == "response" && _pending.TryRemove(message.GetProperty("request_seq").GetInt32(), out var completion))
                {
                    if (message.GetProperty("success").GetBoolean())
                        completion.TrySetResult(message.TryGetProperty("body", out var body) ? body.Clone() : default);
                    else
                        completion.TrySetException(new InvalidOperationException(
                            message.TryGetProperty("message", out var error) ? error.GetString() : "Debugger request failed."));
                }
                else if (type == "event")
                {
                    EventReceived?.Invoke(message.GetProperty("event").GetString()!,
                        message.TryGetProperty("body", out var body) ? body.Clone() : default);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; }
        finally
        {
            foreach (var completion in _pending.Values)
                completion.TrySetException(failure ?? new IOException("The debug adapter disconnected."));
            Closed?.Invoke(failure);
        }
    }

    private async Task ReadErrorsAsync()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
                ErrorOutput?.Invoke(line + Environment.NewLine);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(_reader, _errors).ConfigureAwait(false);
        }
        finally
        {
            _process.Dispose();
            _lifetime.Dispose();
        }
    }
}