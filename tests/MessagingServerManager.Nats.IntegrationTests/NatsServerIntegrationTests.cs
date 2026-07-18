using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using MessagingServerManager.App;
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
    public async Task Bare_path_executable_with_blank_working_directory_starts_generated_config_server()
    {
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(_executable) + Path.PathSeparator + previousPath);
        try
        {
            var clientPort = GetAvailablePort();
            var monitoringPort = GetAvailablePort();
            var paths = new PathResolver(_root);
            var adapter = new NatsServerAdapter(paths, new TcpHealthChecker());
            var definition = new ServerDefinition
            {
                Name = "PATH NATS",
                Executable = "nats-server.exe",
                WorkingDirectory = null,
                LaunchMode = LaunchMode.ConfigFile,
                ManageConfigFile = true,
                ConfigFilePath = "servers/path-nats/nats.conf",
                LogFilePath = "path-nats/nats.log",
                GracefulStopTimeoutSeconds = 1,
                HealthCheckHost = "127.0.0.1",
                Nats = new NatsOptions { ServerName = "path-nats", ClientPort = clientPort, MonitoringPort = monitoringPort, StoreDirectory = "data/path-nats" }
            };
            var manager = Track(new ProcessManager([adapter], new GlobalSettings()), definition);

            await manager.StartAsync(definition);
            var health = await WaitForHealthyAsync(adapter, manager, definition);
            var startInfo = adapter.BuildStartInfo(definition, new GlobalSettings());

            Assert.True(health.IsHealthy, health.Message);
            Assert.True(Path.IsPathRooted(startInfo.FileName), startInfo.FileName);
            Assert.Equal(Path.Combine(_root, "servers", "path-nats"), startInfo.WorkingDirectory);
            Assert.True(File.Exists(Path.Combine(_root, "servers", "path-nats", "nats.conf")));
            Assert.True(File.Exists(Path.Combine(_root, "logs", "path-nats", "nats.log")));
        }
        finally { Environment.SetEnvironmentVariable("PATH", previousPath); }
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
    public async Task Batched_message_flow_populates_metric_sparklines_over_time()
    {
        var (manager, adapter, definition) = CreateManagedServer();
        await manager.StartAsync(definition);
        _ = await WaitForHealthyAsync(adapter, manager, definition);

        var row = new ServerRowViewModel(definition)
        {
            Pid = manager.Get(definition.Id)!.Process.Id,
            Status = ServerStatus.Running
        };

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, definition.Nats.ClientPort);
        await using var stream = client.GetStream();
        Assert.StartsWith("INFO ", await ReadNatsLineAsync(stream));
        await WriteNatsProtocolAsync(stream, "CONNECT {\"verbose\":false}\r\nSUB telemetry.chart 1\r\nPING\r\n");
        await WaitForNatsLineAsync(stream, "PONG", TimeSpan.FromSeconds(5));

        for (var batch = 0; batch < 5; batch++)
        {
            await PublishBatchAsync(stream, "telemetry.chart", 75, $"chart-{batch}");
            await Task.Delay(1100);
            var telemetry = await adapter.GetTelemetryAsync(definition, CancellationToken.None);
            row.ApplyTelemetry(telemetry);
            row.MarkTelemetryAvailable();
            row.RecordMetricSample(telemetry.SampleTime, 10);
        }

        Assert.True(row.HasRawTelemetry);
        Assert.True(row.MessageRateTotal > 0, $"Expected message rate to be calculated, actual text was '{row.MessageRateText}'.");
        Assert.True(row.ConnectionsSparkline.Count >= 5, $"Expected multiple connection sparkline points, got {row.ConnectionsSparkline.Count}.");
        Assert.True(row.MessageRateSparkline.Count >= 5, $"Expected multiple message-rate sparkline points, got {row.MessageRateSparkline.Count}.");
        Assert.True(row.CpuSparkline.Count >= 5, $"Expected multiple CPU sparkline points, got {row.CpuSparkline.Count}.");
        Assert.True(row.MemorySparkline.Count >= 5, $"Expected multiple memory sparkline points, got {row.MemorySparkline.Count}.");
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
        catch (HttpRequestException ex) when (ex.ToString().Contains("certificate",StringComparison.OrdinalIgnoreCase))
        {
            AssertTlsInspectionBypassEnabled("HTTPS telemetry certificate validation", ex.Message);
            return;
        }
        Assert.Equal("integration-tls", telemetry.ServerName);
        Assert.True(telemetry.HealthEndpointHealthy);
    }

    [Fact]
    public async Task Batch_certificates_support_mutual_tls_and_local_and_remote_ui_monitoring_on_sample_ports()
    {
        const int clientPort = 4223;
        const int monitoringPort = 8223;
        if (!PortIsAvailable(clientPort) || !PortIsAvailable(monitoringPort))
            throw SkipException.ForSkip("Ports 4223 and 8223 must be available for the sample TLS UI integration test.");

        var certificateDirectory = Path.Combine(_root, "batch-certificates");
        await GenerateBatchCertificatesAsync(certificateDirectory);
        var ca = Path.Combine(certificateDirectory, "ca.pem");
        var serverCertificate = Path.Combine(certificateDirectory, "nats-server.pem");
        var serverKey = Path.Combine(certificateDirectory, "nats-server.key");
        var clientCertificate = Path.Combine(certificateDirectory, "nats-client.pem");
        var clientKey = Path.Combine(certificateDirectory, "nats-client.key");

        var paths = new PathResolver(_root);
        var adapter = new NatsServerAdapter(paths, new TcpHealthChecker());
        var localDefinition = new ServerDefinition
        {
            Name = "Local NATS TLS UI", Executable = _executable, WorkingDirectory = _root,
            LaunchMode = LaunchMode.ManagedOptions, LogFilePath = "batch-tls.log", HealthCheckHost = "localhost",
            GracefulStopTimeoutSeconds = 1,
            Nats = new NatsOptions
            {
                ServerName = "batch-tls-ui", ClientPort = clientPort, MonitoringPort = monitoringPort,
                UseTls = true, TlsCertificatePath = serverCertificate, TlsPrivateKeyPath = serverKey,
                TlsCaCertificatePath = ca, TlsVerifyClients = true,
                TlsClientCertificatePath = clientCertificate, TlsClientPrivateKeyPath = clientKey
            }
        };
        var manager = Track(new ProcessManager([adapter], new GlobalSettings()), localDefinition);
        await manager.StartAsync(localDefinition);
        _ = await WaitForHealthyAsync(adapter, manager, localDefinition);

        await VerifyMutualTlsClientAsync(clientPort, ca, clientCertificate, clientKey);
        if (!await MonitoringPresentsExpectedCertificateAsync(monitoringPort, serverCertificate, clientCertificate, clientKey))
        {
            AssertTlsInspectionBypassEnabled("local and remote HTTPS/UI telemetry assertions", "Local TLS inspection replaced the generated NATS certificate.");
            return;
        }

        var localTelemetry = await adapter.GetTelemetryAsync(localDefinition, CancellationToken.None);
        var localRow = new ServerRowViewModel(localDefinition)
        {
            Pid = manager.Get(localDefinition.Id)!.Process.Id,
            Status = ServerStatus.Running
        };
        localRow.ApplyTelemetry(localTelemetry);
        localRow.MarkTelemetryAvailable();

        var remoteDefinition = localDefinition.Clone();
        remoteDefinition.Id = Guid.NewGuid();
        remoteDefinition.Name = "Remote NATS TLS UI";
        remoteDefinition.Location = ServerLocation.Remote;
        remoteDefinition.Executable = "";
        var remoteTelemetry = await adapter.GetTelemetryAsync(remoteDefinition, CancellationToken.None);
        var remoteRow = new ServerRowViewModel(remoteDefinition) { Status = ServerStatus.Running };
        remoteRow.ApplyTelemetry(remoteTelemetry);
        remoteRow.MarkTelemetryAvailable();

        Assert.Equal("https://localhost:8223/varz", localRow.Endpoint);
        Assert.Equal("https://localhost:8223/varz", remoteRow.Endpoint);
        Assert.True(localRow.CanStop);
        Assert.False(remoteRow.CanStop);
        Assert.Null(remoteRow.Pid);
        Assert.Equal("Local", localRow.LocationText);
        Assert.Equal("Remote", remoteRow.LocationText);
        Assert.Equal("batch-tls-ui", localTelemetry.ServerName);
        Assert.Equal(localTelemetry.ServerId, remoteTelemetry.ServerId);
        Assert.True(localRow.HasRawTelemetry);
        Assert.True(remoteRow.HasRawTelemetry);

        var stopped = await manager.StopAsync(localDefinition);
        Assert.True(stopped.Stopped, stopped.Error);
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
        var caRequest = new CertificateRequest("CN=Messaging Server Manager Integration Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); serverRequest.CertificateExtensions.Add(names.Build());
        using var serverCertificate = serverRequest.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(12), RandomNumberGenerator.GetBytes(16));
        var caPath = Path.Combine(_root, "generated-ca.pem"); var certificatePath = Path.Combine(_root, "generated-server.pem"); var keyPath = Path.Combine(_root, "generated-server.key");
        File.WriteAllText(caPath, ca.ExportCertificatePem()); File.WriteAllText(certificatePath, serverCertificate.ExportCertificatePem()); File.WriteAllText(keyPath, serverKey.ExportPkcs8PrivateKeyPem());
        return (caPath, certificatePath, keyPath);
    }

    private static void AssertTlsInspectionBypassEnabled(string skippedCoverage, string detail)
    {
        Assert.True(string.Equals(Environment.GetEnvironmentVariable("MSM_ALLOW_TLS_INSPECTION_TEST_BYPASS"), "1", StringComparison.Ordinal),
            $"{skippedCoverage} could not run: {detail} Exclude localhost from TLS inspection, or explicitly set MSM_ALLOW_TLS_INSPECTION_TEST_BYPASS=1 for this intercepted workstation.");
        Console.WriteLine($"BYPASSED by MSM_ALLOW_TLS_INSPECTION_TEST_BYPASS=1: {skippedCoverage}. {detail}");
    }

    private static bool PortIsAvailable(int port)
    {
        try { using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); return true; }
        catch (SocketException) { return false; }
    }

    private static async Task GenerateBatchCertificatesAsync(string outputDirectory)
    {
        var script = Path.Combine(AppContext.BaseDirectory, "generate-nats-test-certificates.bat");
        Assert.True(File.Exists(script), $"Certificate generator was not copied to the test output: {script}");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c \"\"{script}\" \"{outputDirectory}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode == 1 && output.Contains("openssl.exe was not found", StringComparison.OrdinalIgnoreCase))
            throw SkipException.ForSkip("OpenSSL is required for the batch-certificate UI integration test.");
        Assert.True(process.ExitCode == 0, $"Certificate generator failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        foreach (var file in new[] { "ca.pem", "ca.key", "nats-server.pem", "nats-server.key", "nats-client.pem", "nats-client.key" })
            Assert.True(File.Exists(Path.Combine(outputDirectory, file)), $"Certificate generator did not create {file}.");
    }

    private static async Task VerifyMutualTlsClientAsync(int port, string caPath, string certificatePath, string keyPath)
    {
        using var ca = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(caPath));
        using var publicClientCertificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(certificatePath));
        using var clientKey = RSA.Create();
        clientKey.ImportFromPem(await File.ReadAllTextAsync(keyPath));
        using var pemClient = publicClientCertificate.CopyWithPrivateKey(clientKey);
        using var clientCertificate = new X509Certificate2(pemClient.Export(X509ContentType.Pfx));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var network = tcp.GetStream();
        var info = await ReadNatsLineAsync(network);
        Assert.StartsWith("INFO ", info);
        using var tls = new SslStream(network, false, (_, certificate, chain, _) =>
        {
            if (certificate is null || chain is null) return false;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(certificate));
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ClientCertificates = new X509CertificateCollection { clientCertificate }
        });
    }

    private static async Task<bool> MonitoringPresentsExpectedCertificateAsync(int port, string expectedCertificatePath, string clientCertificatePath, string clientKeyPath)
    {
        using var expected = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(expectedCertificatePath));
        using var publicClient = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(clientCertificatePath));
        using var key = RSA.Create();
        key.ImportFromPem(await File.ReadAllTextAsync(clientKeyPath));
        using var clientWithKey = publicClient.CopyWithPrivateKey(key);
        using var client = new X509Certificate2(clientWithKey.Export(X509ContentType.Pfx));
        byte[]? presented = null;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            presented = certificate?.GetRawCertData();
            return true;
        });
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ClientCertificates = new X509CertificateCollection { client }
        });
        return presented is not null && presented.AsSpan().SequenceEqual(expected.RawData);
    }

    private static async Task<string> ReadNatsLineAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var singleByte = new byte[1];
        while (bytes.Count < 64 * 1024)
        {
            var read = await stream.ReadAsync(singleByte).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            if (read == 0 || singleByte[0] == (byte)'\n') break;
            if (singleByte[0] != (byte)'\r') bytes.Add(singleByte[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static async Task WaitForNatsLineAsync(Stream stream, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var line = await ReadNatsLineAsync(stream);
            if (line.Equals(expected, StringComparison.OrdinalIgnoreCase)) return;
        }
        throw new TimeoutException($"NATS protocol line '{expected}' was not received within {timeout}.");
    }

    private static async Task PublishBatchAsync(Stream stream, string subject, int count, string payload)
    {
        var protocol = new StringBuilder();
        var byteCount = Encoding.ASCII.GetByteCount(payload);
        for (var index = 0; index < count; index++)
            protocol.Append("PUB ").Append(subject).Append(' ').Append(byteCount).Append("\r\n").Append(payload).Append("\r\n");
        await WriteNatsProtocolAsync(stream, protocol.ToString());
    }

    private static async Task WriteNatsProtocolAsync(Stream stream, string protocol)
    {
        var bytes = Encoding.ASCII.GetBytes(protocol);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
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
