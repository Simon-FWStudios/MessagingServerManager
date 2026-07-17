using System.Diagnostics;

namespace MessagingServerManager.Core;

public enum ServerType { Nats, TibcoRendezvous }
public enum ServerLocation { Local, Remote }
public enum LaunchMode { ConfigFile, ManagedOptions, CustomArguments }
public enum ServerStatus { Unknown, Disabled, Invalid, Stopped, Starting, Running, Stopping, Restarting, Failed }

public sealed class ServerDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Server";
    public ServerType ServerType { get; set; }
    public ServerLocation Location { get; set; }
    public bool Enabled { get; set; } = true;
    public string Executable { get; set; } = "nats-server.exe";
    public string? WorkingDirectory { get; set; }
    public LaunchMode LaunchMode { get; set; } = LaunchMode.ManagedOptions;
    public string? ConfigFilePath { get; set; }
    public string? AdditionalArguments { get; set; }
    public string? LogFilePath { get; set; }
    public string HealthCheckHost { get; set; } = "localhost";
    public int? HealthCheckPort { get; set; }
    public bool StartWithApplication { get; set; }
    public bool AutoRestart { get; set; }
    public int GracefulStopTimeoutSeconds { get; set; } = 10;
    public int HealthCheckGracePeriodSeconds { get; set; } = 10;
    public bool ForceKillAfterTimeout { get; set; } = true;
    public NatsOptions Nats { get; set; } = new();
    public TibRvOptions TibRv { get; set; } = new();
    public ServerDefinition Clone() => System.Text.Json.JsonSerializer.Deserialize<ServerDefinition>(System.Text.Json.JsonSerializer.Serialize(this))!;
}

public sealed class NatsOptions
{
    public string? ServerName { get; set; }
    public int ClientPort { get; set; } = 4222;
    public int? MonitoringPort { get; set; } = 8222;
    public int? ClusterPort { get; set; }
    public string? StoreDirectory { get; set; }
    public bool UseTls { get; set; }
    public string? TlsCertificatePath { get; set; }
    public string? TlsPrivateKeyPath { get; set; }
    public string? TlsCaCertificatePath { get; set; }
    public bool TlsVerifyClients { get; set; }
}

public sealed class TibRvOptions
{
    public int Service { get; set; } = 7500;
    public string? Network { get; set; }
    public string? DaemonAddress { get; set; }
    public int? HttpAdministrationPort { get; set; } = 7580;
    public int? ListenPort { get; set; }
}

public sealed class GlobalSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string LoggingRootDirectory { get; set; } = "logs";
    public int MonitoringIntervalSeconds { get; set; } = 3;
    public int DefaultGracefulStopTimeoutSeconds { get; set; } = 10;
    public bool DefaultForceKillPolicy { get; set; } = true;
    public bool ConfirmForceKill { get; set; } = true;
    public int MaximumLogLines { get; set; } = 1000;
    public bool AutoStartEnabledServers { get; set; }
}

public sealed class ConfigurationEnvelope<T>
{
    public int SchemaVersion { get; set; } = 1;
    public T Data { get; set; } = default!;
}

public sealed class PortableConfigurationBundle
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public GlobalSettings Settings { get; set; } = new();
    public List<ServerDefinition> Servers { get; set; } = [];
}

public sealed class RuntimeProcessState
{
    public Guid ServerId { get; set; }
    public int ProcessId { get; set; }
    public DateTime StartTimeUtc { get; set; }
    public string Executable { get; set; } = "";
    public int? LastExitCode { get; set; }
    public DateTime? LastExitTimeUtc { get; set; }
    public int RestartCount { get; set; }
}

public sealed record RunningProcessInfo(int ProcessId, DateTime StartTimeUtc, string Executable);
public sealed record RemoteServerTelemetry(
    string ServerId, string ServerName, string Version, int ClientPort, int MonitoringPort,
    TimeSpan Uptime, double CpuPercent, long MemoryBytes, int Connections, int Subscriptions,
    long InMessages, long OutMessages, long InBytes, long OutBytes, int SlowConsumers);
public sealed record ServerHealthResult(bool IsHealthy, string Message)
{
    public static ServerHealthResult Healthy(string message = "Healthy") => new(true, message);
    public static ServerHealthResult Unhealthy(string message) => new(false, message);
}
public sealed record StopResult(bool Stopped, bool WasForced, string? Error = null);
public sealed record ValidationResult(IReadOnlyList<string> Errors) { public bool IsValid => Errors.Count == 0; }

/// <summary>Defines product-specific process construction, health checks, identity matching, and shutdown.</summary>
public interface IServerAdapter
{
    /// <summary>Gets the server product supported by this adapter.</summary>
    ServerType ServerType { get; }
    /// <summary>Builds the effective process start information for a server definition.</summary>
    ProcessStartInfo BuildStartInfo(ServerDefinition definition, GlobalSettings settings);
    /// <summary>Checks process and product health without blocking the caller.</summary>
    Task<ServerHealthResult> CheckHealthAsync(ServerDefinition definition, RunningProcessInfo? process, CancellationToken cancellationToken);
    /// <summary>Stops a validated process gracefully where supported, with configured forced termination fallback.</summary>
    Task<StopResult> StopAsync(ServerDefinition definition, RunningProcessInfo process, CancellationToken cancellationToken);
    /// <summary>Determines whether a live process matches the configured executable identity.</summary>
    bool MatchesProcess(ServerDefinition definition, Process process);
}

/// <summary>Provides product telemetry for a server that is monitored without owning its process.</summary>
public interface IRemoteServerMonitor
{
    Task<RemoteServerTelemetry> GetTelemetryAsync(ServerDefinition definition, CancellationToken cancellationToken);
}

/// <summary>Loads and atomically saves durable application configuration.</summary>
public interface IConfigurationStore
{
    Task<T> LoadAsync<T>(string fileName, T fallback, CancellationToken cancellationToken = default);
    Task SaveAsync<T>(string fileName, T value, CancellationToken cancellationToken = default);
}

public static class ServerValidator
{
    public static ValidationResult Validate(ServerDefinition server, IEnumerable<ServerDefinition> all, Func<string, string> resolvePath)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(server.Name)) errors.Add("Name is required.");
        if (server.Location == ServerLocation.Remote && server.ServerType != ServerType.Nats) errors.Add("Remote monitoring currently supports NATS servers only.");
        if (server.Location == ServerLocation.Local && string.IsNullOrWhiteSpace(server.Executable)) errors.Add("Executable is required.");
        if (server.Location == ServerLocation.Remote && string.IsNullOrWhiteSpace(server.HealthCheckHost)) errors.Add("Remote host is required.");
        if (server.Location == ServerLocation.Remote && server.Nats.MonitoringPort is null) errors.Add("A remote NATS monitoring port is required.");
        if (all.Any(x => x.Id != server.Id && string.Equals(x.Name, server.Name, StringComparison.OrdinalIgnoreCase))) errors.Add("Server names must be unique.");
        var ports = GetPorts(server).Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (ports.Any(x => x is < 1 or > 65535)) errors.Add("Ports must be between 1 and 65535.");
        if (ports.Count != ports.Distinct().Count()) errors.Add("Ports within a server definition must be unique.");
        if (server.ServerType == ServerType.Nats && server.Nats.MonitoringPort == server.Nats.ClientPort) errors.Add("NATS monitoring and client ports must differ.");
        if (server.GracefulStopTimeoutSeconds < 0) errors.Add("Graceful stop timeout cannot be negative.");
        if (server.HealthCheckGracePeriodSeconds < 0) errors.Add("Health-check grace period cannot be negative.");
        var others = all.Where(x => x.Id != server.Id && x.Location == ServerLocation.Local && server.Location == ServerLocation.Local).SelectMany(GetPorts).Where(x => x.HasValue).Select(x => x!.Value).ToHashSet();
        foreach (var port in ports.Distinct().Where(others.Contains)) errors.Add($"Port {port} is already configured.");
        if (server.Location == ServerLocation.Local && server.LaunchMode == LaunchMode.ConfigFile && (string.IsNullOrWhiteSpace(server.ConfigFilePath) || !File.Exists(resolvePath(server.ConfigFilePath)))) errors.Add("The config file does not exist.");
        if (server.Location == ServerLocation.Local && !string.IsNullOrWhiteSpace(server.WorkingDirectory) && !Directory.Exists(resolvePath(server.WorkingDirectory))) errors.Add("The working directory does not exist.");
        if (server.Location == ServerLocation.Local && server.ServerType == ServerType.Nats && server.Nats.UseTls && server.LaunchMode == LaunchMode.ManagedOptions)
        {
            if (string.IsNullOrWhiteSpace(server.Nats.TlsCertificatePath) || !File.Exists(resolvePath(server.Nats.TlsCertificatePath))) errors.Add("A valid NATS TLS certificate file is required.");
            if (string.IsNullOrWhiteSpace(server.Nats.TlsPrivateKeyPath) || !File.Exists(resolvePath(server.Nats.TlsPrivateKeyPath))) errors.Add("A valid NATS TLS private-key file is required.");
            if (!string.IsNullOrWhiteSpace(server.Nats.TlsCaCertificatePath) && !File.Exists(resolvePath(server.Nats.TlsCaCertificatePath))) errors.Add("The NATS TLS CA certificate file does not exist.");
        }
        if (server.Location == ServerLocation.Remote && server.Nats.UseTls && !string.IsNullOrWhiteSpace(server.Nats.TlsCaCertificatePath) && !File.Exists(resolvePath(server.Nats.TlsCaCertificatePath))) errors.Add("The monitoring TLS CA certificate file does not exist.");
        try { if (server.Location == ServerLocation.Local && !string.IsNullOrWhiteSpace(server.LogFilePath)) _ = resolvePath(server.LogFilePath); } catch { errors.Add("The log path cannot be resolved."); }
        return new(errors);
    }
    public static IEnumerable<int?> GetPorts(ServerDefinition s) => s.ServerType == ServerType.Nats
        ? new int?[] { s.Nats.ClientPort, s.Nats.MonitoringPort, s.Nats.ClusterPort }
        : new int?[] { s.TibRv.Service, s.TibRv.HttpAdministrationPort, s.TibRv.ListenPort };
}

public static class CpuUsageCalculator
{
    public static double Calculate(TimeSpan cpuDelta, TimeSpan wallDelta, int processorCount) => wallDelta.TotalMilliseconds <= 0 ? 0 : Math.Clamp(cpuDelta.TotalMilliseconds / wallDelta.TotalMilliseconds / Math.Max(1, processorCount) * 100, 0, 100);
}
