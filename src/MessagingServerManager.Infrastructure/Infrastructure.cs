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
    public ManagedProcess(Process process) => Process = process;
    public void Dispose() => Process.Dispose();
}

public sealed class ProcessManager : IDisposable
{
    private readonly Dictionary<Guid, ManagedProcess> _processes = [];
    private readonly Dictionary<ServerType, IServerAdapter> _adapters;
    private readonly GlobalSettings _settings;
    public event Action<Guid, int?>? ProcessExited;
    public ProcessManager(IEnumerable<IServerAdapter> adapters, GlobalSettings settings) { _adapters = adapters.ToDictionary(x => x.ServerType); _settings = settings; }
    public ManagedProcess? Get(Guid id) => _processes.GetValueOrDefault(id);
    public async Task StartAsync(ServerDefinition server, CancellationToken ct = default)
    {
        if (_processes.ContainsKey(server.Id)) return;
        var process = new Process { StartInfo = _adapters[server.ServerType].BuildStartInfo(server, _settings), EnableRaisingEvents = true };
        process.Exited += (_, _) => { int? code = null; try { code = process.ExitCode; } catch { } _processes.Remove(server.Id, out var owned); owned?.Dispose(); ProcessExited?.Invoke(server.Id, code); };
        if (!await Task.Run(process.Start, ct)) throw new InvalidOperationException("The process could not be started.");
        _processes[server.Id] = new ManagedProcess(process) { PreviousCpu = process.TotalProcessorTime };
    }
    public async Task<StopResult> StopAsync(ServerDefinition server, CancellationToken ct = default)
    {
        if (!_processes.TryGetValue(server.Id, out var managed)) return new(true, false);
        return await _adapters[server.ServerType].StopAsync(server, new(managed.Process.Id, managed.Process.StartTime.ToUniversalTime(), server.Executable), ct);
    }
    public async Task RestartAsync(ServerDefinition server, CancellationToken ct = default) { await StopAsync(server, ct); await Task.Delay(250, ct); await StartAsync(server, ct); }
    public void Dispose() { foreach (var p in _processes.Values) p.Dispose(); _processes.Clear(); }
}

public sealed class LogTailReader
{
    public async Task<IReadOnlyList<string>> ReadTailAsync(string path, int maxLines, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return ["Log file does not exist yet: " + path];
        var queue = new Queue<string>(maxLines);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        using var reader = new StreamReader(fs);
        while (await reader.ReadLineAsync(ct) is { } line) { if (queue.Count == maxLines) queue.Dequeue(); queue.Enqueue(line); }
        return queue.ToArray();
    }
}
