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
        if (p is null && d.Location == ServerLocation.Local) return ServerHealthResult.Unhealthy("Process is not running.");
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
        var rawJson = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        var start = Date(root, "start"); var now = Date(root, "now");
        var uptime = start.HasValue && now.HasValue ? now.Value - start.Value : TimeSpan.Zero;
        using var healthResponse = await client.GetAsync(GetMonitoringUri(d, "healthz"), ct);
        var clusterName = root.TryGetProperty("cluster", out var cluster) && cluster.ValueKind == JsonValueKind.Object ? String(cluster, "name") : "";
        var tags = root.TryGetProperty("server_tags", out var tagValues) && tagValues.ValueKind == JsonValueKind.Array ? string.Join(", ", tagValues.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x))) : "";
        var metadata = root.TryGetProperty("server_metadata", out var metadataValue) && metadataValue.ValueKind == JsonValueKind.Object ? metadataValue.GetRawText() : "";
        return new(
            String(root, "server_id"), String(root, "server_name", String(root, "name")), String(root, "version"),
            Int(root, "port", d.Nats.ClientPort), Int(root, d.Nats.UseTls ? "https_port" : "http_port", port), uptime,
            Double(root, "cpu"), Long(root, "mem"), Int(root, "connections"), Int(root, "subscriptions"),
            Long(root, "in_msgs"), Long(root, "out_msgs"), Long(root, "in_bytes"), Long(root, "out_bytes"), Int(root, "slow_consumers"),
            Int(root, "max_connections"), Int(root, "total_connections"), Int(root, "routes"), Int(root, "remotes"), Int(root, "leafnodes"),
            clusterName, tags, metadata, now ?? DateTimeOffset.UtcNow, healthResponse.IsSuccessStatusCode,
            JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static Uri GetMonitoringUri(ServerDefinition definition)
        => GetMonitoringUri(definition, "varz");
    private static Uri GetMonitoringUri(ServerDefinition definition, string path)
    {
        var port = definition.Nats.MonitoringPort ?? throw new InvalidOperationException("A NATS monitoring port is required.");
        return new UriBuilder(definition.Nats.UseTls ? "https" : "http", definition.HealthCheckHost, port, path).Uri;
    }
    private HttpClient? CreateCustomCaClient(ServerDefinition d)
    {
        if (!d.Nats.UseTls || string.IsNullOrWhiteSpace(d.Nats.TlsCaCertificatePath)) return null;
        var ca = X509Certificate2.CreateFromPem(File.ReadAllText(Paths.Resolve(d.Nats.TlsCaCertificatePath)));
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

public sealed class TibRvServerAdapter : ServerAdapterBase, ITibRvMonitor
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
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
    public override async Task<ServerHealthResult> CheckHealthAsync(ServerDefinition d, RunningProcessInfo? p, CancellationToken ct)
    {
        if (p is null) return ServerHealthResult.Unhealthy("Process is not running.");
        var port = d.TibRv.HttpAdministrationPort ?? d.TibRv.ListenPort ?? d.HealthCheckPort;
        if (d.TibRv.HttpAdministrationPort is not null) return ServerHealthResult.Healthy("Process is running; HTTP metrics configured.");
        return port is int value ? await Tcp.CheckAsync(d.HealthCheckHost, value, TimeSpan.FromSeconds(2), ct) : ServerHealthResult.Healthy("Process is running.");
    }
    public async Task<TibRvTelemetry> GetTelemetryAsync(ServerDefinition d, CancellationToken ct)
    {
        var port = d.TibRv.HttpAdministrationPort ?? throw new InvalidOperationException("An HTTP administration port is required for TIBCO RV metrics.");
        var raw = await Http.GetStringAsync(new UriBuilder("http", d.HealthCheckHost, port, "metrics").Uri, ct);
        return ParseMetrics(raw);
    }
    public static TibRvTelemetry ParseMetrics(string raw)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in raw.Split('\n'))
        {
            var line = source.Trim(); if (line.Length == 0 || line[0] == '#') continue;
            var separator = line.LastIndexOfAny([' ', '\t']); if (separator <= 0 || !double.TryParse(line[(separator + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) continue;
            var descriptor = line[..separator]; var brace = descriptor.IndexOf('{'); var name = brace >= 0 ? descriptor[..brace] : descriptor;
            values[name] = values.GetValueOrDefault(name) + value;
            if (labels.Count == 0 && brace >= 0 && descriptor.EndsWith('}')) foreach (var pair in descriptor[(brace + 1)..^1].Split(',')) { var equals = pair.IndexOf('='); if (equals > 0) labels[pair[..equals].Trim()] = pair[(equals + 1)..].Trim().Trim('"'); }
        }
        double Metric(string name) => values.GetValueOrDefault(name);
        string Label(string name) => labels.GetValueOrDefault(name, "");
        return new(TimeSpan.FromSeconds(Metric("rv_service_uptime")), (int)Metric("rv_service_client_connections"), (int)Metric("rv_service_subscriptions"),
            (long)Metric("rv_service_inbound_messages_total"), (long)Metric("rv_service_outbound_messages_total"), (long)Metric("rv_service_inbound_bytes_total"), (long)Metric("rv_service_outbound_bytes_total"),
            (long)Metric("rv_service_inbound_packets_total"), (long)Metric("rv_service_outbound_packets_total"), (long)Metric("rv_service_inbound_dataloss_total"), (long)Metric("rv_service_outbound_dataloss_total"),
            (long)Metric("rv_service_packets_retransmitted_total"), (long)Metric("rv_service_packets_missed_total"), Label("component"), Label("version"), Label("host"), Label("service"), Label("network"), DateTimeOffset.UtcNow, raw);
    }
    private string ResolveLogPath(ServerDefinition d, GlobalSettings settings)
    {
        var path = Path.IsPathRooted(Environment.ExpandEnvironmentVariables(d.LogFilePath!)) ? Paths.Resolve(d.LogFilePath!) : Paths.Resolve(Path.Combine(settings.LoggingRootDirectory, d.LogFilePath!));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
