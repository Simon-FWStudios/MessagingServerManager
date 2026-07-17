using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using MessagingServerManager.Core;
using MessagingServerManager.Infrastructure;

namespace MessagingServerManager.ServerAdapters;

public abstract class ServerAdapterBase : IServerAdapter
{
    protected readonly PathResolver Paths;
    protected readonly TcpHealthChecker Tcp;
    protected ServerAdapterBase(PathResolver paths, TcpHealthChecker tcp) { Paths = paths; Tcp = tcp; }
    public abstract ServerType ServerType { get; }
    public abstract ProcessStartInfo BuildStartInfo(ServerDefinition definition, GlobalSettings settings);
    public abstract Task<ServerHealthResult> CheckHealthAsync(ServerDefinition definition, RunningProcessInfo? process, CancellationToken cancellationToken);
    public virtual bool MatchesProcess(ServerDefinition definition, Process process) => ProcessIdentity.ExecutableMatches(process, definition.Executable);
    public virtual async Task<StopResult> StopAsync(ServerDefinition definition, RunningProcessInfo processInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processInfo.ProcessId);
            if (!MatchesProcess(definition, process) || Math.Abs((process.StartTime.ToUniversalTime() - processInfo.StartTimeUtc).TotalSeconds) >= 2) return new(false, false, "Process identity no longer matches.");
            try { if (process.CloseMainWindow() && await WaitAsync(process, definition.GracefulStopTimeoutSeconds, cancellationToken)) return new(true, false); } catch { }
            if (!definition.ForceKillAfterTimeout) return new(false, false, "Graceful stop timed out.");
            process.Kill(true); await process.WaitForExitAsync(cancellationToken); return new(true, true);
        }
        catch (ArgumentException) { return new(true, false); }
        catch (Exception ex) { return new(false, false, ex.Message); }
    }
    private static async Task<bool> WaitAsync(Process process, int seconds, CancellationToken ct) { try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(seconds), ct); return true; } catch (TimeoutException) { return false; } }
    protected ProcessStartInfo StartInfo(ServerDefinition d, string args) => new()
    {
        FileName = Environment.ExpandEnvironmentVariables(d.Executable), Arguments = args,
        WorkingDirectory = string.IsNullOrWhiteSpace(d.WorkingDirectory) ? Paths.ConfigurationDirectory : Paths.Resolve(d.WorkingDirectory),
        UseShellExecute = false, CreateNoWindow = true
    };
    protected static string Q(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}

public sealed class NatsServerAdapter : ServerAdapterBase, IRemoteServerMonitor
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    public NatsServerAdapter(PathResolver paths, TcpHealthChecker tcp) : base(paths, tcp) { }
    public override ServerType ServerType => ServerType.Nats;
    public string BuildArguments(ServerDefinition d) => BuildArguments(d, new GlobalSettings());
    public string BuildArguments(ServerDefinition d, GlobalSettings settings) => d.LaunchMode switch
    {
        LaunchMode.ConfigFile => $"-c {Q(Paths.Resolve(d.ConfigFilePath!))}" + Extra(d),
        LaunchMode.CustomArguments => d.AdditionalArguments ?? "",
        _ => $"--name {Q(d.Nats.ServerName ?? d.Name)} --port {d.Nats.ClientPort}" +
             (d.Nats.MonitoringPort is int m ? d.Nats.UseTls ? $" --https_port {m}" : $" --http_port {m}" : "") +
             (d.Nats.ClusterPort is int c ? $" --cluster nats://0.0.0.0:{c}" : "") +
             (!string.IsNullOrWhiteSpace(d.Nats.StoreDirectory) ? $" --store_dir {Q(Paths.Resolve(d.Nats.StoreDirectory))}" : "") + Extra(d)
             + (!string.IsNullOrWhiteSpace(d.LogFilePath) ? $" --log {Q(ResolveLogPath(d, settings))}" : "") + TlsArguments(d)
    };
    private string TlsArguments(ServerDefinition d)
    {
        if (!d.Nats.UseTls) return "";
        var arguments = $" --tls --tlscert {Q(Paths.Resolve(d.Nats.TlsCertificatePath!))} --tlskey {Q(Paths.Resolve(d.Nats.TlsPrivateKeyPath!))}";
        if (!string.IsNullOrWhiteSpace(d.Nats.TlsCaCertificatePath)) arguments += $" --tlscacert {Q(Paths.Resolve(d.Nats.TlsCaCertificatePath))}";
        if (d.Nats.TlsVerifyClients) arguments += " --tlsverify";
        return arguments;
    }
    private static string Extra(ServerDefinition d) => string.IsNullOrWhiteSpace(d.AdditionalArguments) ? "" : " " + d.AdditionalArguments;
    public override ProcessStartInfo BuildStartInfo(ServerDefinition d, GlobalSettings settings) => StartInfo(d, BuildArguments(d, settings));
    public override async Task<StopResult> StopAsync(ServerDefinition definition, RunningProcessInfo processInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var signal = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ExpandEnvironmentVariables(definition.Executable),
                Arguments = $"--signal stop={processInfo.ProcessId}",
                WorkingDirectory = string.IsNullOrWhiteSpace(definition.WorkingDirectory) ? Paths.ConfigurationDirectory : Paths.Resolve(definition.WorkingDirectory),
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (signal is not null) await signal.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            using var target = Process.GetProcessById(processInfo.ProcessId);
            await target.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(Math.Max(1, definition.GracefulStopTimeoutSeconds)), cancellationToken);
            return new(true, false);
        }
        catch (ArgumentException) { return new(true, false); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return await base.StopAsync(definition, processInfo, cancellationToken);
        }
    }
    public override async Task<ServerHealthResult> CheckHealthAsync(ServerDefinition d, RunningProcessInfo? p, CancellationToken ct)
    {
        if (p is null) return ServerHealthResult.Unhealthy("Process is not running.");
        if (d.Nats.MonitoringPort is not null) try { _ = await GetTelemetryAsync(d, ct); return ServerHealthResult.Healthy($"NATS {(d.Nats.UseTls ? "HTTPS" : "HTTP")} /varz healthy"); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return ServerHealthResult.Unhealthy(ex.Message); }
        return await Tcp.CheckAsync(d.HealthCheckHost, d.Nats.ClientPort, TimeSpan.FromSeconds(2), ct);
    }
    public async Task<RemoteServerTelemetry> GetTelemetryAsync(ServerDefinition d, CancellationToken ct)
    {
        var port = d.Nats.MonitoringPort ?? throw new InvalidOperationException("A NATS monitoring port is required.");
        var endpoint = GetMonitoringUri(d);
        using var customClient = CreateCustomCaClient(d);
        var client = customClient ?? _http;
        using var response = await client.GetAsync(endpoint, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = document.RootElement;
        var start = Date(root, "start"); var now = Date(root, "now");
        var uptime = start.HasValue && now.HasValue ? now.Value - start.Value : TimeSpan.Zero;
        return new(
            String(root, "server_id"), String(root, "server_name", String(root, "name")), String(root, "version"),
            Int(root, "port", d.Nats.ClientPort), Int(root, d.Nats.UseTls ? "https_port" : "http_port", port), uptime,
            Double(root, "cpu"), Long(root, "mem"), Int(root, "connections"), Int(root, "subscriptions"),
            Long(root, "in_msgs"), Long(root, "out_msgs"), Long(root, "in_bytes"), Long(root, "out_bytes"), Int(root, "slow_consumers"));
    }
    public static Uri GetMonitoringUri(ServerDefinition definition)
    {
        var port = definition.Nats.MonitoringPort ?? throw new InvalidOperationException("A NATS monitoring port is required.");
        return new UriBuilder(definition.Nats.UseTls ? "https" : "http", definition.HealthCheckHost, port, "varz").Uri;
    }
    private HttpClient? CreateCustomCaClient(ServerDefinition d)
    {
        if (!d.Nats.UseTls || string.IsNullOrWhiteSpace(d.Nats.TlsCaCertificatePath)) return null;
        var ca = X509Certificate2.CreateFromPemFile(Paths.Resolve(d.Nats.TlsCaCertificatePath));
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null) return false;
            if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(certificate));
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    }
    private static string String(JsonElement root, string name, string fallback = "") => root.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
    private static int Int(JsonElement root, string name, int fallback = 0) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static long Long(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static double Double(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0;
    private static DateTimeOffset? Date(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var result) ? result : null;
    private string ResolveLogPath(ServerDefinition d, GlobalSettings settings)
    {
        var path = Path.IsPathRooted(Environment.ExpandEnvironmentVariables(d.LogFilePath!)) ? Paths.Resolve(d.LogFilePath!) : Paths.Resolve(Path.Combine(settings.LoggingRootDirectory, d.LogFilePath!));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}

public sealed class TibRvServerAdapter : ServerAdapterBase
{
    public TibRvServerAdapter(PathResolver paths, TcpHealthChecker tcp) : base(paths, tcp) { }
    public override ServerType ServerType => ServerType.TibcoRendezvous;
    public string BuildArguments(ServerDefinition d) => BuildArguments(d, new GlobalSettings());
    public string BuildArguments(ServerDefinition d, GlobalSettings settings) => d.LaunchMode == LaunchMode.CustomArguments ? d.AdditionalArguments ?? "" :
        $"-service {d.TibRv.Service}" +
        (!string.IsNullOrWhiteSpace(d.TibRv.Network) ? $" -network {Q(d.TibRv.Network)}" : "") +
        (!string.IsNullOrWhiteSpace(d.TibRv.DaemonAddress) ? $" -daemon {Q(d.TibRv.DaemonAddress)}" : "") +
        (d.TibRv.HttpAdministrationPort is int h ? $" -http {h}" : "") +
        (!string.IsNullOrWhiteSpace(d.LogFilePath) ? $" -logfile {Q(ResolveLogPath(d, settings))}" : "") +
        (string.IsNullOrWhiteSpace(d.AdditionalArguments) ? "" : " " + d.AdditionalArguments);
    public override ProcessStartInfo BuildStartInfo(ServerDefinition d, GlobalSettings settings) => StartInfo(d, BuildArguments(d, settings));
    public override Task<ServerHealthResult> CheckHealthAsync(ServerDefinition d, RunningProcessInfo? p, CancellationToken ct)
    {
        if (p is null) return Task.FromResult(ServerHealthResult.Unhealthy("Process is not running."));
        var port = d.TibRv.HttpAdministrationPort ?? d.TibRv.ListenPort ?? d.HealthCheckPort;
        return port is int value ? Tcp.CheckAsync(d.HealthCheckHost, value, TimeSpan.FromSeconds(2), ct) : Task.FromResult(ServerHealthResult.Healthy("Process is running."));
    }
    private string ResolveLogPath(ServerDefinition d, GlobalSettings settings)
    {
        var path = Path.IsPathRooted(Environment.ExpandEnvironmentVariables(d.LogFilePath!)) ? Paths.Resolve(d.LogFilePath!) : Paths.Resolve(Path.Combine(settings.LoggingRootDirectory, d.LogFilePath!));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
