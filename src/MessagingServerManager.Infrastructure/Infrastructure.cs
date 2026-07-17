using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SaveGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly PathResolver _paths;
    public JsonConfigurationStore(PathResolver paths) { _paths = paths; Directory.CreateDirectory(paths.ConfigurationDirectory); }
    public async Task<T> LoadAsync<T>(string fileName, T fallback, CancellationToken cancellationToken = default)
    {
        var path = _paths.Resolve(fileName);
        if (!File.Exists(path)) return fallback;
        var loaded = await TryLoadAsync<T>(path, cancellationToken);
        if (loaded.Success) return ApplyLoadedMigrations(loaded.Value!);
        var backup = path + ".bak";
        if (File.Exists(backup))
        {
            loaded = await TryLoadAsync<T>(backup, cancellationToken);
            if (loaded.Success) return ApplyLoadedMigrations(loaded.Value!);
        }
        return fallback;
    }
    private static T ApplyLoadedMigrations<T>(T value)
    {
        if (value is GlobalSettings settings) ConfigurationMigrations.Apply(settings);
        var type = value?.GetType();
        if (type?.IsGenericType == true && type.GetGenericTypeDefinition() == typeof(ConfigurationEnvelope<>))
        {
            var property = type.GetProperty(nameof(ConfigurationEnvelope<object>.SchemaVersion))!;
            var version = (int)property.GetValue(value)!;
            if (version is < 1 or > ConfigurationMigrations.CurrentSchemaVersion) throw new InvalidDataException($"Unsupported configuration schema version {version}.");
            property.SetValue(value, ConfigurationMigrations.CurrentSchemaVersion);
        }
        return value;
    }
    public async Task SaveAsync<T>(string fileName, T value, CancellationToken cancellationToken = default)
    {
        var path = _paths.Resolve(fileName); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var gate = SaveGates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; var backup = path + ".bak";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            { await JsonSerializer.SerializeAsync(stream, value, Options, cancellationToken); await stream.FlushAsync(cancellationToken); }
            if (File.Exists(path)) File.Replace(temp, path, backup, true); else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            gate.Release();
        }
    }
    private static async Task<(bool Success, T? Value)> TryLoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken);
            return value is null ? (false, default) : (true, value);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return (false, default); }
    }
}

public sealed class ConfigurationTransferService
{
    public const int CurrentSchemaVersion = 2;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public async Task ExportAsync(string path, PortableConfigurationBundle bundle, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(stream, bundle, Options, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporary, fullPath, true);
    }

    public async Task<PortableConfigurationBundle> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var bundle = await JsonSerializer.DeserializeAsync<PortableConfigurationBundle>(stream, Options, cancellationToken)
            ?? throw new InvalidDataException("The selected file does not contain a configuration bundle.");
        if (bundle.SchemaVersion is < 1 or > CurrentSchemaVersion) throw new InvalidDataException($"Unsupported configuration schema version {bundle.SchemaVersion}.");
        bundle.Settings ??= new GlobalSettings();
        bundle.Servers ??= [];
        ConfigurationMigrations.Apply(bundle);
        return bundle;
    }
}

public static class ConfigurationMigrations
{
    public const int CurrentSchemaVersion = 2;
    public static bool Apply(GlobalSettings settings)
    {
        if (settings.SchemaVersion is < 1 or > CurrentSchemaVersion) throw new InvalidDataException($"Unsupported settings schema version {settings.SchemaVersion}.");
        var changed = settings.SchemaVersion < CurrentSchemaVersion;
        if (settings.MonitoringLogMaximumBytes <= 0) { settings.MonitoringLogMaximumBytes = 5 * 1024 * 1024; changed = true; }
        if (settings.MonitoringLogRetainedFiles < 1) { settings.MonitoringLogRetainedFiles = 3; changed = true; }
        settings.SchemaVersion = CurrentSchemaVersion;
        return changed;
    }
    public static bool Apply<T>(ConfigurationEnvelope<T> envelope)
    {
        if (envelope.SchemaVersion is < 1 or > CurrentSchemaVersion) throw new InvalidDataException($"Unsupported configuration schema version {envelope.SchemaVersion}.");
        var changed = envelope.SchemaVersion < CurrentSchemaVersion;
        envelope.SchemaVersion = CurrentSchemaVersion;
        return changed;
    }
    public static void Apply(PortableConfigurationBundle bundle)
    {
        ConfigurationMigrations.Apply(bundle.Settings);
        bundle.SchemaVersion = CurrentSchemaVersion;
    }
}

public sealed class RotatingTextLog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task AppendLineAsync(string path, string line, long maximumBytes, int retainedFiles, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length >= Math.Max(1024, maximumBytes)) Rotate(path, Math.Max(1, retainedFiles));
            await File.AppendAllTextAsync(path, line + Environment.NewLine, ct);
        }
        finally { _gate.Release(); }
    }
    private static void Rotate(string path, int retainedFiles)
    {
        var oldest = path + "." + retainedFiles;
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var index = retainedFiles - 1; index >= 1; index--)
        {
            var source = path + "." + index;
            if (File.Exists(source)) File.Move(source, path + "." + (index + 1), true);
        }
        File.Move(path, path + ".1", true);
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
        try
        {
            var actual = process.MainModule?.FileName;
            if (actual is null) return false;
            var expanded = Environment.ExpandEnvironmentVariables(executable);
            return Path.IsPathRooted(expanded)
                ? string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expanded), StringComparison.OrdinalIgnoreCase)
                : string.Equals(Path.GetFileName(actual), Path.GetFileName(expanded), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _operationGates = new();
    private readonly Dictionary<ServerType, IServerAdapter> _adapters;
    private readonly GlobalSettings _settings;
    private volatile bool _disposed;
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
            var managed = new ManagedProcess(process) { PreviousCpu = process.TotalProcessorTime };
            lock (_gate)
            {
                if (_processes.ContainsKey(server.Id)) { managed.Dispose(); return false; }
                _processes[server.Id] = managed;
                process.Exited += (_, _) => HandleExited(server.Id, managed);
                process.EnableRaisingEvents = true;
            }
            if (process.HasExited) { HandleExited(server.Id, managed); return false; }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            lock (_gate)
            {
                if (_processes.TryGetValue(server.Id, out var registered)) { _processes.Remove(server.Id); registered.Dispose(); }
            }
            return false;
        }
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operationGate = _operationGates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await operationGate.WaitAsync(ct);
        try { await StartCoreAsync(server, ct); }
        finally { operationGate.Release(); }
    }
    private async Task StartCoreAsync(ServerDefinition server, CancellationToken ct)
    {
        lock (_gate) { if (_processes.ContainsKey(server.Id)) return; }
        var adapter = _adapters[server.ServerType];
        await adapter.PrepareAsync(server, _settings, ct);
        var process = new Process { StartInfo = adapter.BuildStartInfo(server, _settings), EnableRaisingEvents = true };
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operationGate = _operationGates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await operationGate.WaitAsync(ct);
        try { return await StopCoreAsync(server, ct); }
        finally { operationGate.Release(); }
    }
    private async Task<StopResult> StopCoreAsync(ServerDefinition server, CancellationToken ct)
    {
        ManagedProcess? managed;
        lock (_gate) managed = _processes.GetValueOrDefault(server.Id);
        if (managed is null) return new(true, false);
        var result = await _adapters[server.ServerType].StopAsync(server, new(managed.Process.Id, managed.Process.StartTime.ToUniversalTime(), server.Executable), ct);
        if (result.Stopped) await managed.ExitCompletion.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(2, server.GracefulStopTimeoutSeconds + 2)), ct);
        return result;
    }
    public async Task RestartAsync(ServerDefinition server, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var operationGate = _operationGates.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await operationGate.WaitAsync(ct);
        try
        {
            var stopped = await StopCoreAsync(server, ct);
            if (!stopped.Stopped) throw new InvalidOperationException(stopped.Error ?? "The server could not be stopped for restart.");
            await Task.Delay(250, ct);
            await StartCoreAsync(server, ct);
        }
        finally { operationGate.Release(); }
    }
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
        if (_disposed) return;
        _disposed = true;
        List<ManagedProcess> owned;
        lock (_gate) { owned = _processes.Values.ToList(); _processes.Clear(); }
        foreach (var process in owned) process.Dispose();
        _operationGates.Clear();
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
