using System.Net;
using System.Net.Sockets;
using MessagingServerManager.Core;
using MessagingServerManager.Infrastructure;
using MessagingServerManager.ServerAdapters;
using Xunit.Sdk;

namespace MessagingServerManager.Nats.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class NatsServerIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MessagingServerManager.NatsTests", Guid.NewGuid().ToString("N"));
    private readonly List<(ProcessManager Manager, ServerDefinition Definition)> _running = [];
    private string _executable = "";

    public Task InitializeAsync()
    {
        _executable = LocateNatsServer();
        if (!File.Exists(_executable)) throw SkipException.ForSkip("Set NATS_SERVER_PATH or download nats-server under tools/nats-server to run real NATS integration tests.");
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Managed_mode_starts_real_server_and_passes_varz_health_check()
    {
        var (manager, adapter, definition) = CreateManagedServer();
        await manager.StartAsync(definition);

        var health = await WaitForHealthyAsync(adapter, manager, definition);
        var telemetry = await adapter.GetTelemetryAsync(definition, CancellationToken.None);
        var state = Assert.Single(manager.GetRuntimeStates());

        Assert.True(health.IsHealthy, health.Message);
        Assert.True(state.ProcessId > 0);
        Assert.Equal(definition.Nats.ClientPort, telemetry.ClientPort);
        Assert.Equal(definition.Nats.MonitoringPort, telemetry.MonitoringPort);
        Assert.True(telemetry.MemoryBytes > 0);
        Assert.False(string.IsNullOrWhiteSpace(telemetry.ServerId));
        Assert.Equal(Path.GetFileName(_executable), Path.GetFileName(state.Executable), ignoreCase: true);
        var managedLog = Path.Combine(_root, "logs", definition.LogFilePath!);
        await WaitUntilAsync(() => File.Exists(managedLog) && new FileInfo(managedLog).Length > 0, TimeSpan.FromSeconds(10));
        Assert.Contains(await new LogTailReader().ReadTailAsync(managedLog, 100), line => line.Contains("Server is ready", StringComparison.OrdinalIgnoreCase));

        var stopped = await manager.StopAsync(definition);
        Assert.True(stopped.Stopped, stopped.Error);
        Assert.Null(manager.Get(definition.Id));
    }

    [Fact]
    public async Task Restart_replaces_real_nats_process_and_restores_health()
    {
        var (manager, adapter, definition) = CreateManagedServer();
        await manager.StartAsync(definition);
        _ = await WaitForHealthyAsync(adapter, manager, definition);
        var firstPid = manager.Get(definition.Id)!.Process.Id;

        await manager.RestartAsync(definition);
        var secondPid = manager.Get(definition.Id)!.Process.Id;
        var health = await WaitForHealthyAsync(adapter, manager, definition);

        Assert.NotEqual(firstPid, secondPid);
        Assert.True(health.IsHealthy, health.Message);
    }

    [Fact]
    public async Task Config_mode_creates_a_real_log_that_tail_reader_can_read()
    {
        var clientPort = GetAvailablePort();
        var monitoringPort = GetAvailablePort();
        var logPath = Path.Combine(_root, "logs", "nats.log");
        var configPath = Path.Combine(_root, "nats.conf");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var normalizedLog = logPath.Replace('\\', '/');
        await File.WriteAllTextAsync(configPath, $"server_name: integration-config\nport: {clientPort}\nhttp: {monitoringPort}\nlog_file: \"{normalizedLog}\"\ndebug: false\ntrace: false\n");

        var paths = new PathResolver(_root);
        var adapter = new NatsServerAdapter(paths, new TcpHealthChecker());
        var definition = new ServerDefinition
        {
            Name = "Real NATS config test",
            Executable = _executable,
            WorkingDirectory = _root,
            LaunchMode = LaunchMode.ConfigFile,
            ConfigFilePath = configPath,
            LogFilePath = logPath,
            GracefulStopTimeoutSeconds = 1,
            Nats = new NatsOptions { ServerName = "integration-config", ClientPort = clientPort, MonitoringPort = monitoringPort }
        };
        var manager = Track(new ProcessManager([adapter], new GlobalSettings()), definition);
        await manager.StartAsync(definition);
        _ = await WaitForHealthyAsync(adapter, manager, definition);

        await WaitUntilAsync(async () => File.Exists(logPath) && (await new LogTailReader().ReadTailAsync(logPath, 100)).Any(line => line.Contains("Server is ready", StringComparison.OrdinalIgnoreCase)), TimeSpan.FromSeconds(10));
        var lines = await new LogTailReader().ReadTailAsync(logPath, 100);

        Assert.Contains(lines, line => line.Contains("Server is ready", StringComparison.OrdinalIgnoreCase));
    }

    private (ProcessManager Manager, NatsServerAdapter Adapter, ServerDefinition Definition) CreateManagedServer()
    {
        var clientPort = GetAvailablePort();
        var monitoringPort = GetAvailablePort();
        var paths = new PathResolver(_root);
        var adapter = new NatsServerAdapter(paths, new TcpHealthChecker());
        var definition = new ServerDefinition
        {
            Name = "Real NATS managed test",
            Executable = _executable,
            WorkingDirectory = _root,
            LaunchMode = LaunchMode.ManagedOptions,
            LogFilePath = Path.Combine("managed", $"{Guid.NewGuid():N}.log"),
            GracefulStopTimeoutSeconds = 1,
            ForceKillAfterTimeout = true,
            HealthCheckHost = "127.0.0.1",
            Nats = new NatsOptions { ServerName = "integration-managed", ClientPort = clientPort, MonitoringPort = monitoringPort }
        };
        return (Track(new ProcessManager([adapter], new GlobalSettings()), definition), adapter, definition);
    }

    private ProcessManager Track(ProcessManager manager, ServerDefinition definition)
    {
        _running.Add((manager, definition));
        return manager;
    }

    private static async Task<ServerHealthResult> WaitForHealthyAsync(NatsServerAdapter adapter, ProcessManager manager, ServerDefinition definition)
    {
        ServerHealthResult result = ServerHealthResult.Unhealthy("Not checked");
        await WaitUntilAsync(async () =>
        {
            var process = manager.Get(definition.Id)?.Process;
            if (process is null) return false;
            result = await adapter.CheckHealthAsync(definition, new RunningProcessInfo(process.Id, process.StartTime.ToUniversalTime(), definition.Executable), CancellationToken.None);
            return result.IsHealthy;
        }, TimeSpan.FromSeconds(10));
        return result;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout) => await WaitUntilAsync(() => Task.FromResult(predicate()), timeout);
    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Condition was not met within {timeout}.");
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string LocateNatsServer()
    {
        var configured = Environment.GetEnvironmentVariable("NATS_SERVER_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MessagingServerManager.sln"))) directory = directory.Parent;
        if (directory is null) return "";
        var toolsRoot = Path.Combine(directory.FullName, "tools", "nats-server");
        if (!Directory.Exists(toolsRoot)) return "";
        return Directory.EnumerateFiles(toolsRoot, "nats-server.exe", SearchOption.AllDirectories)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "";
    }

    public async Task DisposeAsync()
    {
        foreach (var (manager, definition) in _running)
        {
            try { await manager.StopAsync(definition); } catch { }
            manager.Dispose();
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
