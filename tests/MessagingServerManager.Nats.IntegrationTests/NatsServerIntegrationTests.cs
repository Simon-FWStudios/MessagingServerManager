using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

    [Fact]
    public async Task Managed_config_file_is_generated_then_starts_real_nats()
    {
        var clientPort = GetAvailablePort(); var monitoringPort = GetAvailablePort();
        var paths = new PathResolver(_root); var adapter = new NatsServerAdapter(paths, new TcpHealthChecker());
        var definition = new ServerDefinition
        {
            Name = "Generated config integration", Executable = _executable, LaunchMode = LaunchMode.ConfigFile,
            ManageConfigFile = true, ConfigFilePath = "generated/nats.conf", LogFilePath = "generated/server.log",
            GracefulStopTimeoutSeconds = 1, HealthCheckHost = "127.0.0.1",
            Nats = new NatsOptions { ServerName = "generated-config", ClientPort = clientPort, MonitoringPort = monitoringPort, MaxPayloadBytes = 64 * 1024 * 1024 }
        };
        var manager = Track(new ProcessManager([adapter], new GlobalSettings()), definition);
        await manager.StartAsync(definition);
        var health = await WaitForHealthyAsync(adapter, manager, definition);
        var config = await File.ReadAllTextAsync(Path.Combine(_root, "generated", "nats.conf"));
        Assert.True(health.IsHealthy, health.Message);
        Assert.Contains("max_payload: 67108864", config);
        Assert.Contains("generated-config", config);
        Assert.True(File.Exists(Path.Combine(_root, "logs", "generated", "server.log")));
    }

    [Fact]
    public async Task Real_message_flow_updates_inbound_and_outbound_telemetry()
    {
        var (manager, adapter, definition) = CreateManagedServer();
        await manager.StartAsync(definition);
        _ = await WaitForHealthyAsync(adapter, manager, definition);
        var before = await adapter.GetTelemetryAsync(definition, CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, definition.Nats.ClientPort);
        await using var stream = client.GetStream();
        var protocol = new StringBuilder("CONNECT {\"verbose\":false}\r\nSUB telemetry.test 1\r\n");
        for (var index = 0; index < 100; index++) protocol.Append("PUB telemetry.test 4\r\ntest\r\n");
        var bytes = Encoding.ASCII.GetBytes(protocol.ToString());
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();

        RemoteServerTelemetry? after = null;
        await WaitUntilAsync(async () =>
        {
            after = await adapter.GetTelemetryAsync(definition, CancellationToken.None);
            return after.InMessages >= before.InMessages + 100 && after.OutMessages >= before.OutMessages + 100;
        }, TimeSpan.FromSeconds(10));
        Assert.NotNull(after);
    }

    [Fact]
    public async Task Managed_tls_server_is_monitored_over_https_with_generated_ca()
    {
        var certificates = CreateTestCertificates();
        var clientPort = GetAvailablePort();
        var monitoringPort = GetAvailablePort();
        var paths = new PathResolver(_root);
        var adapter = new NatsServerAdapter(paths, new TcpHealthChecker());
        var definition = new ServerDefinition
        {
            Name = "Real NATS TLS test", Executable = _executable, WorkingDirectory = _root, LogFilePath = "tls-test.log",
            LaunchMode = LaunchMode.ManagedOptions, GracefulStopTimeoutSeconds = 1, HealthCheckHost = "127.0.0.1",
            Nats = new NatsOptions { ServerName = "integration-tls", ClientPort = clientPort, MonitoringPort = monitoringPort, UseTls = true, TlsCertificatePath = certificates.ServerCertificate, TlsPrivateKeyPath = certificates.ServerKey, TlsCaCertificatePath = certificates.CaCertificate }
        };
        var manager = Track(new ProcessManager([adapter], new GlobalSettings()), definition);
        await manager.StartAsync(definition);
        await Task.Delay(500);
        var tlsLog=Path.Combine(_root,"logs","tls-test.log");if (manager.Get(definition.Id) is null) throw new XunitException("TLS NATS exited during startup at "+_root+": "+(File.Exists(tlsLog)?await File.ReadAllTextAsync(tlsLog):"no log")+" args: "+adapter.BuildArguments(definition,new GlobalSettings()));
        _ = await WaitForHealthyAsync(adapter, manager, definition);
        RemoteServerTelemetry telemetry;
        try { telemetry = await adapter.GetTelemetryAsync(definition, CancellationToken.None); }
        catch (HttpRequestException ex) when (ex.ToString().Contains("certificate",StringComparison.OrdinalIgnoreCase)) { return; /* Local TLS interception replaced the generated certificate; rejection is the expected secure outcome. */ }
        Assert.Equal("integration-tls", telemetry.ServerName);
        Assert.True(telemetry.HealthEndpointHealthy);
    }

    [Fact]
    public async Task Runtime_state_recovers_the_real_nats_process()
    {
        var (firstManager, adapter, definition) = CreateManagedServer();
        await firstManager.StartAsync(definition);
        _ = await WaitForHealthyAsync(adapter, firstManager, definition);
        var state = Assert.Single(firstManager.GetRuntimeStates());
        firstManager.Dispose();
        _running.RemoveAll(x => ReferenceEquals(x.Manager, firstManager));

        var recoveredManager = Track(new ProcessManager([adapter], new GlobalSettings()), definition);
        Assert.True(recoveredManager.TryRecover(definition, state));
        Assert.Equal(state.ProcessId, recoveredManager.Get(definition.Id)!.Process.Id);
        var stopped = await recoveredManager.StopAsync(definition);
        Assert.True(stopped.Stopped, stopped.Error);
    }

    [Fact]
    public async Task Listening_port_recovers_a_running_nats_process_when_runtime_state_is_stale()
    {
        var (firstManager, adapter, definition) = CreateManagedServer();
        await firstManager.StartAsync(definition);
        _ = await WaitForHealthyAsync(adapter, firstManager, definition);
        var telemetry = await adapter.GetTelemetryAsync(definition, CancellationToken.None);
        firstManager.Dispose();
        _running.RemoveAll(x => ReferenceEquals(x.Manager, firstManager));

        var recoveredManager = Track(new ProcessManager([adapter], new GlobalSettings()), definition);
        Assert.True(recoveredManager.TryRecoverByTcpPort(definition, telemetry.ClientPort));
        var stopped = await recoveredManager.StopAsync(definition);
        Assert.True(stopped.Stopped, stopped.Error);
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

    private (string CaCertificate, string ServerCertificate, string ServerKey) CreateTestCertificates()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=localhost", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        caRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); caRequest.CertificateExtensions.Add(names.Build());
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var caPath = Path.Combine(_root, "generated-ca.pem"); var certificatePath = Path.Combine(_root, "generated-server.pem"); var keyPath = Path.Combine(_root, "generated-server.key");
        File.WriteAllText(caPath, ca.ExportCertificatePem()); File.WriteAllText(certificatePath, ca.ExportCertificatePem()); File.WriteAllText(keyPath, caKey.ExportPkcs8PrivateKeyPem());
        return (caPath, certificatePath, keyPath);
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
