using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
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
    public async Task<StopResult> StopAsync(ServerDefinition definition, RunningProcessInfo processInfo, CancellationToken cancellationToken)
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

public sealed class NatsServerAdapter : ServerAdapterBase
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    public NatsServerAdapter(PathResolver paths, TcpHealthChecker tcp) : base(paths, tcp) { }
    public override ServerType ServerType => ServerType.Nats;
    public string BuildArguments(ServerDefinition d) => d.LaunchMode switch
    {
        LaunchMode.ConfigFile => $"-c {Q(Paths.Resolve(d.ConfigFilePath!))}" + Extra(d),
        LaunchMode.CustomArguments => d.AdditionalArguments ?? "",
        _ => $"--name {Q(d.Nats.ServerName ?? d.Name)} --port {d.Nats.ClientPort}" +
             (d.Nats.MonitoringPort is int m ? $" --http_port {m}" : "") +
             (d.Nats.ClusterPort is int c ? $" --cluster nats://0.0.0.0:{c}" : "") +
             (!string.IsNullOrWhiteSpace(d.Nats.StoreDirectory) ? $" --store_dir {Q(Paths.Resolve(d.Nats.StoreDirectory))}" : "") + Extra(d)
    };
    private static string Extra(ServerDefinition d) => string.IsNullOrWhiteSpace(d.AdditionalArguments) ? "" : " " + d.AdditionalArguments;
    public override ProcessStartInfo BuildStartInfo(ServerDefinition d, GlobalSettings settings) => StartInfo(d, BuildArguments(d));
    public override async Task<ServerHealthResult> CheckHealthAsync(ServerDefinition d, RunningProcessInfo? p, CancellationToken ct)
    {
        if (p is null) return ServerHealthResult.Unhealthy("Process is not running.");
        if (d.Nats.MonitoringPort is int port) try { using var response = await _http.GetAsync($"http://{d.HealthCheckHost}:{port}/varz", ct); return response.IsSuccessStatusCode ? ServerHealthResult.Healthy("NATS /varz healthy") : ServerHealthResult.Unhealthy($"NATS returned {(int)response.StatusCode}"); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return ServerHealthResult.Unhealthy(ex.Message); }
        return await Tcp.CheckAsync(d.HealthCheckHost, d.Nats.ClientPort, TimeSpan.FromSeconds(2), ct);
    }
}

public sealed class TibRvServerAdapter : ServerAdapterBase
{
    public TibRvServerAdapter(PathResolver paths, TcpHealthChecker tcp) : base(paths, tcp) { }
    public override ServerType ServerType => ServerType.TibcoRendezvous;
    public string BuildArguments(ServerDefinition d) => d.LaunchMode == LaunchMode.CustomArguments ? d.AdditionalArguments ?? "" :
        $"-service {d.TibRv.Service}" +
        (!string.IsNullOrWhiteSpace(d.TibRv.Network) ? $" -network {Q(d.TibRv.Network)}" : "") +
        (!string.IsNullOrWhiteSpace(d.TibRv.DaemonAddress) ? $" -daemon {Q(d.TibRv.DaemonAddress)}" : "") +
        (d.TibRv.HttpAdministrationPort is int h ? $" -http {h}" : "") +
        (!string.IsNullOrWhiteSpace(d.LogFilePath) ? $" -logfile {Q(Paths.Resolve(d.LogFilePath))}" : "") +
        (string.IsNullOrWhiteSpace(d.AdditionalArguments) ? "" : " " + d.AdditionalArguments);
    public override ProcessStartInfo BuildStartInfo(ServerDefinition d, GlobalSettings settings) => StartInfo(d, BuildArguments(d));
    public override Task<ServerHealthResult> CheckHealthAsync(ServerDefinition d, RunningProcessInfo? p, CancellationToken ct)
    {
        if (p is null) return Task.FromResult(ServerHealthResult.Unhealthy("Process is not running."));
        var port = d.TibRv.HttpAdministrationPort ?? d.TibRv.ListenPort ?? d.HealthCheckPort;
        return port is int value ? Tcp.CheckAsync(d.HealthCheckHost, value, TimeSpan.FromSeconds(2), ct) : Task.FromResult(ServerHealthResult.Healthy("Process is running."));
    }
}
