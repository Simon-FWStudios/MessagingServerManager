using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
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
    public virtual Task PrepareAsync(ServerDefinition definition, GlobalSettings settings, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(definition.LogFilePath)) _ = ResolveLogPath(definition, settings);
        return Task.CompletedTask;
    }
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
    protected string ResolveLogPath(ServerDefinition d, GlobalSettings settings)
    {
        var path = Path.IsPathRooted(Environment.ExpandEnvironmentVariables(d.LogFilePath!)) ? Paths.Resolve(d.LogFilePath!) : Paths.Resolve(Path.Combine(settings.LoggingRootDirectory, d.LogFilePath!));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
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
    public override ProcessStartInfo BuildStartInfo(ServerDefinition d, GlobalSettings settings)
    {
        var startInfo = StartInfo(d, BuildArguments(d, settings));
        if (d.LaunchMode == LaunchMode.ConfigFile && string.IsNullOrWhiteSpace(d.WorkingDirectory)) startInfo.WorkingDirectory = Path.GetDirectoryName(Paths.Resolve(d.ConfigFilePath!))!;
        return startInfo;
    }
    public override async Task PrepareAsync(ServerDefinition d, GlobalSettings settings, CancellationToken ct)
    {
        await base.PrepareAsync(d, settings, ct);
        if (d.Location != ServerLocation.Local || d.LaunchMode != LaunchMode.ConfigFile) return;
        var path = Paths.Resolve(d.ConfigFilePath!); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!d.ManageConfigFile)
        {
            if (File.Exists(path)) EnsureExternalConfigLogDirectory(path);
            return;
        }
        var lines = new List<string> { $"server_name: {Yaml(d.Nats.ServerName ?? d.Name)}", $"port: {d.Nats.ClientPort}", $"max_payload: {d.Nats.MaxPayloadBytes}" };
        if (d.Nats.MonitoringPort is int monitoringPort) lines.Add($"{(d.Nats.UseTls ? "https" : "http")}: {monitoringPort}");
        if (d.Nats.ClusterPort is int clusterPort) lines.Add($"cluster {{ listen: nats://0.0.0.0:{clusterPort} }}");
        if (!string.IsNullOrWhiteSpace(d.Nats.StoreDirectory)) lines.Add($"jetstream {{ store_dir: {Yaml(Paths.Resolve(d.Nats.StoreDirectory))} }}");
        if (!string.IsNullOrWhiteSpace(d.LogFilePath)) lines.Add($"log_file: {Yaml(ResolveLogPath(d, settings))}");
        if (d.Nats.UseTls)
        {
            lines.Add("tls {"); lines.Add($"  cert_file: {Yaml(Paths.Resolve(d.Nats.TlsCertificatePath!))}"); lines.Add($"  key_file: {Yaml(Paths.Resolve(d.Nats.TlsPrivateKeyPath!))}");
            if (!string.IsNullOrWhiteSpace(d.Nats.TlsCaCertificatePath)) lines.Add($"  ca_file: {Yaml(Paths.Resolve(d.Nats.TlsCaCertificatePath))}");
            lines.Add($"  verify: {d.Nats.TlsVerifyClients.ToString().ToLowerInvariant()}"); lines.Add("}");
        }
        if (!string.IsNullOrWhiteSpace(d.AdditionalArguments)) lines.AddRange(d.AdditionalArguments.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllLinesAsync(temporary, lines, ct); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void EnsureExternalConfigLogDirectory(string configPath)
    {
        var entry = File.ReadLines(configPath).Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("log_file", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return;
        var separator = entry.IndexOf(':'); if (separator < 0) return;
        var configured = entry[(separator + 1)..].Trim().Trim('"', '\''); if (configured.Length == 0) return;
        var expanded = Environment.ExpandEnvironmentVariables(configured);
        var resolved = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(Path.GetDirectoryName(configPath)!, expanded));
        Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
    }
    private static string Yaml(string value) => JsonSerializer.Serialize(value);
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
        using var customClient = CreateTlsClient(d);
        var client = customClient ?? _http;
        var rawJson = await GetStringWithRetryAsync(client, endpoint, ct);
        using var document = JsonDocument.Parse(rawJson);
        var root = document.RootElement;
        var start = Date(root, "start"); var now = Date(root, "now");
        var uptime = start.HasValue && now.HasValue ? now.Value - start.Value : TimeSpan.Zero;
        var healthEndpointHealthy = await CheckHealthEndpointAsync(client, GetMonitoringUri(d, "healthz"), ct);
        var clusterName = root.TryGetProperty("cluster", out var cluster) && cluster.ValueKind == JsonValueKind.Object ? String(cluster, "name") : "";
        var tags = root.TryGetProperty("server_tags", out var tagValues) && tagValues.ValueKind == JsonValueKind.Array ? string.Join(", ", tagValues.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x))) : "";
        var metadata = root.TryGetProperty("server_metadata", out var metadataValue) && metadataValue.ValueKind == JsonValueKind.Object ? metadataValue.GetRawText() : "";
        return new(
            String(root, "server_id"), String(root, "server_name", String(root, "name")), String(root, "version"),
            Int(root, "port", d.Nats.ClientPort), Int(root, d.Nats.UseTls ? "https_port" : "http_port", port), uptime,
            Double(root, "cpu"), Long(root, "mem"), Int(root, "connections"), Int(root, "subscriptions"),
            Long(root, "in_msgs"), Long(root, "out_msgs"), Long(root, "in_bytes"), Long(root, "out_bytes"), Int(root, "slow_consumers"),
            Int(root, "max_connections"), Int(root, "total_connections"), Int(root, "routes"), Int(root, "remotes"), Int(root, "leafnodes"),
            clusterName, tags, metadata, now ?? DateTimeOffset.UtcNow, healthEndpointHealthy,
            JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static async Task<string> GetStringWithRetryAsync(HttpClient client, Uri endpoint, CancellationToken ct)
    {
        Exception? failure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(endpoint, ct);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                failure = ex;
                if (attempt == 0) await Task.Delay(200, ct);
            }
        }
        throw new HttpRequestException($"Telemetry request failed after retry: {failure?.Message}", failure);
    }
    private static async Task<bool> CheckHealthEndpointAsync(HttpClient client, Uri endpoint, CancellationToken ct)
    {
        try { using var response = await client.GetAsync(endpoint, ct); return response.IsSuccessStatusCode; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return false; }
    }
    public static Uri GetMonitoringUri(ServerDefinition definition)
        => GetMonitoringUri(definition, "varz");
    private static Uri GetMonitoringUri(ServerDefinition definition, string path)
    {
        var port = definition.Nats.MonitoringPort ?? throw new InvalidOperationException("A NATS monitoring port is required.");
        return new UriBuilder(definition.Nats.UseTls ? "https" : "http", definition.HealthCheckHost, port, path).Uri;
    }
    private HttpClient? CreateTlsClient(ServerDefinition d)
    {
        if (!d.Nats.UseTls) return null;
        var hasCustomCa = !string.IsNullOrWhiteSpace(d.Nats.TlsCaCertificatePath);
        var hasClientCertificate = !string.IsNullOrWhiteSpace(d.Nats.TlsClientCertificatePath) && !string.IsNullOrWhiteSpace(d.Nats.TlsClientPrivateKeyPath);
        if (!hasCustomCa && !hasClientCertificate) return null;
        var handler = new HttpClientHandler();
        if (hasClientCertificate)
        {
            var certificatePem = File.ReadAllText(Paths.Resolve(d.Nats.TlsClientCertificatePath!));
            var keyPem = File.ReadAllText(Paths.Resolve(d.Nats.TlsClientPrivateKeyPath!));
            using var publicCertificate = X509Certificate2.CreateFromPem(certificatePem);
            using var privateKey = RSA.Create();
            privateKey.ImportFromPem(keyPem);
            using var certificateWithKey = publicCertificate.CopyWithPrivateKey(privateKey);
            handler.ClientCertificates.Add(new X509Certificate2(certificateWithKey.Export(X509ContentType.Pfx)));
        }
        if (hasCustomCa)
        {
            var caPem = File.ReadAllText(Paths.Resolve(d.Nats.TlsCaCertificatePath!));
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
                using var serverCertificate = new X509Certificate2(certificate);
                using var ca = X509Certificate2.CreateFromPem(caPem);
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(serverCertificate);
            };
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    }
    private static string String(JsonElement root, string name, string fallback = "") => root.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
    private static int Int(JsonElement root, string name, int fallback = 0) => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static long Long(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static double Double(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0;
    private static DateTimeOffset? Date(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var result) ? result : null;
}

public sealed class TibRvServerAdapter : ServerAdapterBase, ITibRvMonitor
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    public TibRvServerAdapter(PathResolver paths, TcpHealthChecker tcp) : base(paths, tcp) { }
    public override ServerType ServerType => ServerType.TibcoRendezvous;
    public string BuildArguments(ServerDefinition d) => BuildArguments(d, new GlobalSettings());
    public string BuildArguments(ServerDefinition d, GlobalSettings settings) => d.LaunchMode == LaunchMode.CustomArguments ? d.AdditionalArguments ?? "" :
        $"-listen {d.TibRv.ListenHost}:{d.TibRv.ListenPort}" +
        $" -reliability {d.TibRv.ReliabilitySeconds}" +
        (d.TibRv.HttpAdministrationPort is int h ? $" -http {d.TibRv.HttpAdministrationHost}:{h}" : "") +
        (!string.IsNullOrWhiteSpace(d.TibRv.Network) ? $" -network {Q(d.TibRv.Network)}" : "") +
        (!string.IsNullOrWhiteSpace(d.LogFilePath) ? $" -logfile {Q(ResolveLogPath(d, settings))}" : "") +
        (string.IsNullOrWhiteSpace(d.AdditionalArguments) ? "" : " " + d.AdditionalArguments);
    public override ProcessStartInfo BuildStartInfo(ServerDefinition d, GlobalSettings settings) => StartInfo(d, BuildArguments(d, settings));
    public override async Task<ServerHealthResult> CheckHealthAsync(ServerDefinition d, RunningProcessInfo? p, CancellationToken ct)
    {
        var port = d.TibRv.HttpAdministrationPort ?? d.TibRv.ListenPort;
        if (p is null && d.Location == ServerLocation.Local) return ServerHealthResult.Unhealthy("Process is not running.");
        if (p is null) return port is int remotePort ? await Tcp.CheckAsync(d.HealthCheckHost, remotePort, TimeSpan.FromSeconds(2), ct) : ServerHealthResult.Unhealthy("No remote reachability port is configured.");
        if (d.TibRv.HttpAdministrationPort is not null) return ServerHealthResult.Healthy("Process is running; HTTP metrics configured.");
        return port is int value ? await Tcp.CheckAsync(d.HealthCheckHost, value, TimeSpan.FromSeconds(2), ct) : ServerHealthResult.Healthy("Process is running.");
    }
    public async Task<TibRvTelemetry> GetTelemetryAsync(ServerDefinition d, CancellationToken ct)
    {
        var port = d.TibRv.HttpAdministrationPort ?? throw new InvalidOperationException("An HTTP administration port is required for TIBCO RV metrics.");
        var raw = await Http.GetStringAsync(new UriBuilder("http", d.HealthCheckHost, port, "metrics").Uri, ct);
        return ParseMetrics(raw, d.TibRv.ListenPort.ToString(CultureInfo.InvariantCulture), d.TibRv.Network);
    }
    public static TibRvTelemetry ParseMetrics(string raw, string? configuredService = null, string? configuredNetwork = null)
    {
        var samples = new List<(string Name, double Value, Dictionary<string, string> Labels)>();
        foreach (var source in raw.Split('\n'))
        {
            var line = source.Trim(); if (line.Length == 0 || line[0] == '#') continue;
            var separator = line.LastIndexOfAny([' ', '\t']); if (separator <= 0 || !double.TryParse(line[(separator + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)) continue;
            var descriptor = line[..separator]; var brace = descriptor.IndexOf('{'); var name = brace >= 0 ? descriptor[..brace] : descriptor;
            var sampleLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (brace >= 0 && descriptor.EndsWith('}')) foreach (var pair in descriptor[(brace + 1)..^1].Split(',')) { var equals = pair.IndexOf('='); if (equals > 0) sampleLabels[pair[..equals].Trim()] = pair[(equals + 1)..].Trim().Trim('"'); }
            samples.Add((name, value, sampleLabels));
        }
        var labels = samples.Select(x => x.Labels).FirstOrDefault(x =>
            (string.IsNullOrWhiteSpace(configuredService) || x.GetValueOrDefault("service") == configuredService) &&
            (string.IsNullOrWhiteSpace(configuredNetwork) || x.GetValueOrDefault("network") == configuredNetwork));
        if (labels is null && (!string.IsNullOrWhiteSpace(configuredService) && samples.Any(x => x.Labels.ContainsKey("service")) || !string.IsNullOrWhiteSpace(configuredNetwork) && samples.Any(x => x.Labels.ContainsKey("network"))))
            throw new InvalidDataException("TIBCO RV metrics do not contain the configured service/network label set.");
        labels ??= samples.Select(x => x.Labels).FirstOrDefault(x => x.Count > 0) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool Selected(Dictionary<string, string> candidate) => candidate.Count == 0 ||
            (!labels.TryGetValue("service", out var service) || candidate.GetValueOrDefault("service") == service) &&
            (!labels.TryGetValue("network", out var network) || candidate.GetValueOrDefault("network") == network);
        var values = samples.Where(x => Selected(x.Labels)).GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Sum(y => y.Value), StringComparer.Ordinal);
        double Metric(string name) => values.GetValueOrDefault(name);
        string Label(string name) => labels.GetValueOrDefault(name, "");
        return new(TimeSpan.FromSeconds(Metric("rv_service_uptime")), (int)Metric("rv_service_client_connections"), (int)Metric("rv_service_subscriptions"),
            (long)Metric("rv_service_inbound_messages_total"), (long)Metric("rv_service_outbound_messages_total"), (long)Metric("rv_service_inbound_bytes_total"), (long)Metric("rv_service_outbound_bytes_total"),
            (long)Metric("rv_service_inbound_packets_total"), (long)Metric("rv_service_outbound_packets_total"), (long)Metric("rv_service_inbound_dataloss_total"), (long)Metric("rv_service_outbound_dataloss_total"),
            (long)Metric("rv_service_packets_retransmitted_total"), (long)Metric("rv_service_packets_missed_total"), Label("component"), Label("version"), Label("host"), Label("service"), Label("network"), DateTimeOffset.UtcNow, raw);
    }
}
