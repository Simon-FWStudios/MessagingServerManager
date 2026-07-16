using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using MessagingServerManager.Core;

namespace MessagingServerManager.Infrastructure;

public sealed class PathResolver
{
    public string ConfigurationDirectory { get; }
    public PathResolver(string? root = null) => ConfigurationDirectory = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MessagingServerManager");
    public string Resolve(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(ConfigurationDirectory, expanded));
    }
}

public sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly PathResolver _paths;
    public JsonConfigurationStore(PathResolver paths) { _paths = paths; Directory.CreateDirectory(paths.ConfigurationDirectory); }
    public async Task<T> LoadAsync<T>(string fileName, T fallback, CancellationToken cancellationToken = default)
    {
        var path = _paths.Resolve(fileName);
        if (!File.Exists(path)) return fallback;
        try { await using var stream = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken) ?? fallback; }
        catch (JsonException) { return fallback; }
    }
    public async Task SaveAsync<T>(string fileName, T value, CancellationToken cancellationToken = default)
    {
        var path = _paths.Resolve(fileName); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp"; var backup = path + ".bak";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        { await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken); await stream.FlushAsync(cancellationToken); }
        if (File.Exists(path)) File.Replace(temp, path, backup, true); else File.Move(temp, path);
    }
}

public sealed class TcpHealthChecker
{
    public async Task<ServerHealthResult> CheckAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { using var client = new TcpClient(); await client.ConnectAsync(host, port, cancellationToken).AsTask().WaitAsync(timeout, cancellationToken); return ServerHealthResult.Healthy($"TCP {host}:{port} reachable"); }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException) { return ServerHealthResult.Unhealthy($"TCP {host}:{port} unavailable: {ex.Message}"); }
    }
}

public sealed class ProcessIdentity
{
    public bool Matches(Process process, RuntimeProcessState state)
    {
        try { return process.Id == state.ProcessId && Math.Abs((process.StartTime.ToUniversalTime() - state.StartTimeUtc).TotalSeconds) < 2 && ExecutableMatches(process, state.Executable); } catch { return false; }
    }
    public static bool ExecutableMatches(Process process, string executable)
    {
        try { var actual = process.MainModule?.FileName; return actual is not null && string.Equals(Path.GetFileName(actual), Path.GetFileName(executable), StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }
}

public sealed class ManagedProcess : IDisposable
{
    public Process Process { get; }
    public TimeSpan PreviousCpu { get; set; }
    public DateTime PreviousSampleUtc { get; set; } = DateTime.UtcNow;
    public TaskCompletionSource<int?> ExitCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ManagedProcess(Process process) => Process = process;
    public void Dispose() => Process.Dispose();
}

public sealed class ProcessManager : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ManagedProcess> _processes = [];
    private readonly Dictionary<ServerType, IServerAdapter> _adapters;
    private readonly GlobalSettings _settings;
    public event Action<Guid, int?>? ProcessExited;
    public ProcessManager(IEnumerable<IServerAdapter> adapters, GlobalSettings settings) { _adapters = adapters.ToDictionary(x => x.ServerType); _settings = settings; }
    public ManagedProcess? Get(Guid id) { lock (_gate) return _processes.GetValueOrDefault(id); }
    public bool TryRecover(ServerDefinition server, RuntimeProcessState state)
    {
        if (state.ServerId != server.Id) return false;
        try
        {
            var process = Process.GetProcessById(state.ProcessId);
            if (!new ProcessIdentity().Matches(process, state) || !_adapters[server.ServerType].MatchesProcess(server, process)) { process.Dispose(); return false; }
            process.EnableRaisingEvents = true;
            var managed = new ManagedProcess(process) { PreviousCpu = process.TotalProcessorTime };
            process.Exited += (_, _) => HandleExited(server.Id, managed);
            lock (_gate)
            {
                if (_processes.ContainsKey(server.Id)) { managed.Dispose(); return false; }
                _processes[server.Id] = managed;
            }
            if (process.HasExited) { HandleExited(server.Id, managed); return false; }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    public IReadOnlyList<RuntimeProcessState> GetRuntimeStates()
    {
        lock (_gate)
        {
            var states = new List<RuntimeProcessState>();
            foreach (var pair in _processes)
            {
                try
                {
                    var process = pair.Value.Process;
                    states.Add(new RuntimeProcessState { ServerId = pair.Key, ProcessId = process.Id, StartTimeUtc = process.StartTime.ToUniversalTime(), Executable = process.MainModule?.FileName ?? process.StartInfo.FileName });
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return states;
        }
    }
    public async Task StartAsync(ServerDefinition server, CancellationToken ct = default)
    {
        lock (_gate) { if (_processes.ContainsKey(server.Id)) return; }
        var process = new Process { StartInfo = _adapters[server.ServerType].BuildStartInfo(server, _settings), EnableRaisingEvents = true };
        var managed = new ManagedProcess(process);
        process.Exited += (_, _) => HandleExited(server.Id, managed);
        try
        {
            if (!await Task.Run(process.Start, ct)) throw new InvalidOperationException("The process could not be started.");
            managed.PreviousCpu = process.TotalProcessorTime;
            lock (_gate)
            {
                if (_processes.ContainsKey(server.Id)) throw new InvalidOperationException("The server is already running.");
                _processes[server.Id] = managed;
            }
            // An extremely short-lived process may have raised Exited before it was registered.
            if (process.HasExited) HandleExited(server.Id, managed);
        }
        catch
        {
            lock (_gate) { if (_processes.GetValueOrDefault(server.Id) == managed) _processes.Remove(server.Id); }
            managed.Dispose();
            throw;
        }
    }
    public async Task<StopResult> StopAsync(ServerDefinition server, CancellationToken ct = default)
    {
        ManagedProcess? managed;
        lock (_gate) managed = _processes.GetValueOrDefault(server.Id);
        if (managed is null) return new(true, false);
        var result = await _adapters[server.ServerType].StopAsync(server, new(managed.Process.Id, managed.Process.StartTime.ToUniversalTime(), server.Executable), ct);
        if (result.Stopped) await managed.ExitCompletion.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(2, server.GracefulStopTimeoutSeconds + 2)), ct);
        return result;
    }
    public async Task RestartAsync(ServerDefinition server, CancellationToken ct = default) { await StopAsync(server, ct); await Task.Delay(250, ct); await StartAsync(server, ct); }
    private void HandleExited(Guid serverId, ManagedProcess managed)
    {
        int? code = null;
        try { code = managed.Process.ExitCode; } catch { }
        var removed = false;
        lock (_gate)
        {
            if (_processes.GetValueOrDefault(serverId) == managed) { _processes.Remove(serverId); removed = true; }
        }
        managed.ExitCompletion.TrySetResult(code);
        if (removed) ProcessExited?.Invoke(serverId, code);
        managed.Dispose();
    }
    public void Dispose()
    {
        List<ManagedProcess> owned;
        lock (_gate) { owned = _processes.Values.ToList(); _processes.Clear(); }
        foreach (var process in owned) process.Dispose();
    }
}

public sealed class LogTailReader
{
    private sealed class TailState(int maximumLines)
    {
        public Queue<string> Lines { get; } = new(maximumLines);
        public int MaximumLines { get; } = maximumLines;
        public long Position { get; set; }
        public DateTime CreationUtc { get; set; }
        public string Remainder { get; set; } = "";
        public bool RemainderDisplayed { get; set; }
    }
    private readonly Dictionary<string, TailState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<string>> ReadTailAsync(string path, int maxLines, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return ["Log file does not exist yet: " + path];
        maxLines = Math.Max(1, maxLines);
        await _gate.WaitAsync(ct);
        try
        {
            var info = new FileInfo(path);
            if (!_states.TryGetValue(path, out var state) || state.CreationUtc != info.CreationTimeUtc || info.Length < state.Position || state.MaximumLines != maxLines)
            {
                state = await ReadInitialTailAsync(path, info, maxLines, ct);
                _states[path] = state;
                return state.Lines.ToArray();
            }
            if (info.Length == state.Position) return state.Lines.ToArray();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            stream.Seek(state.Position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var appended = await reader.ReadToEndAsync(ct);
            state.Position = stream.Length;
            Append(state, appended, maxLines, finalChunk: true);
            return state.Lines.ToArray();
        }
        finally { _gate.Release(); }
    }

    private static async Task<TailState> ReadInitialTailAsync(string path, FileInfo info, int maxLines, CancellationToken ct)
    {
        var window = Math.Min(info.Length, Math.Max(4096L, maxLines * 256L));
        string text;
        while (true)
        {
            var bytes = new byte[window];
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
            stream.Seek(info.Length - window, SeekOrigin.Begin);
            var read = await stream.ReadAsync(bytes.AsMemory(), ct);
            text = System.Text.Encoding.UTF8.GetString(bytes, 0, read);
            if (window == info.Length || CountLines(text) > maxLines) break;
            window = Math.Min(info.Length, window * 2);
        }
        if (window < info.Length)
        {
            var firstBreak = text.IndexOf('\n');
            text = firstBreak >= 0 ? text[(firstBreak + 1)..] : "";
        }
        var state = new TailState(maxLines) { Position = info.Length, CreationUtc = info.CreationTimeUtc };
        Append(state, text, maxLines, finalChunk: true);
        return state;
    }

    private static int CountLines(string text) => text.Count(c => c == '\n');
    private static void Append(TailState state, string text, int maxLines, bool finalChunk)
    {
        if (state.RemainderDisplayed) { RemoveLast(state.Lines); state.RemainderDisplayed = false; }
        var combined = state.Remainder + text;
        var parts = combined.Split('\n');
        var complete = parts.Length - 1;
        for (var i = 0; i < complete; i++) Enqueue(state.Lines, parts[i].TrimEnd('\r'), maxLines);
        state.Remainder = parts[^1];
        if (finalChunk && state.Remainder.Length > 0) { Enqueue(state.Lines, state.Remainder.TrimEnd('\r'), maxLines); state.RemainderDisplayed = true; }
    }
    private static void Enqueue(Queue<string> lines, string line, int maxLines) { while (lines.Count >= maxLines) lines.Dequeue(); lines.Enqueue(line); }
    private static void RemoveLast(Queue<string> lines) { if (lines.Count == 0) return; var values = lines.ToArray(); lines.Clear(); foreach (var value in values[..^1]) lines.Enqueue(value); }
}
