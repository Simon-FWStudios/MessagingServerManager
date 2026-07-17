using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MessagingServerManager.Core;
using MessagingServerManager.Infrastructure;
namespace MessagingServerManager.App;

sealed record MetricHistoryPoint(DateTimeOffset SampleTime, double Connections, double MessageRate, double Cpu, double MemoryMegabytes);

public sealed class ServerRowViewModel : ObservableObject
{
    bool telemetryStale; DateTimeOffset? lastTelemetrySuccess;
    public ServerDefinition Definition { get; }
    ServerStatus status; int? pid; TimeSpan uptime; double cpu; long memory; string health = "Not checked"; string? error; int? exit; DateTime? started; DateTime? lastChecked; DateTime? lastExitTime; int restartCount; string serverId = ""; string serverName = ""; string serverVersion = ""; int connections; int subscriptions; long inMessages; long outMessages; long inBytes; long outBytes; int slowConsumers; int maximumConnections; int totalConnections; int routes; int remotes; int leafNodes; string clusterName = ""; string serverTags = ""; string serverMetadata = ""; string rawTelemetry = ""; bool healthEndpointHealthy; bool hasRateSample; DateTimeOffset? previousSample; long previousInMessages; long previousOutMessages; long previousInBytes; long previousOutBytes; double inMessageRate; double outMessageRate; double inByteRate; double outByteRate;
    readonly List<MetricHistoryPoint> metricHistory = [];
    public ServerRowViewModel(ServerDefinition d) { Definition = d; status = d.Enabled ? ServerStatus.Stopped : ServerStatus.Disabled; }
    public string Name => Definition.Name; public string TypeText => Definition.ServerType == ServerType.Nats ? "NATS" : "TIBCO RV"; public string LocationText => Definition.Location.ToString(); public string Endpoint => Definition.ServerType == ServerType.Nats ? $"{(Definition.Nats.UseTls ? "https" : "http")}://{Definition.HealthCheckHost}:{Definition.Nats.MonitoringPort}/varz" : Definition.TibRv.HttpAdministrationPort is int rvPort ? $"http://{Definition.HealthCheckHost}:{rvPort}/metrics" : "Local process"; public string ExecutableDisplay => Definition.Location == ServerLocation.Remote ? Endpoint : Definition.Executable; public int PrimaryPort => ServerValidator.GetPorts(Definition).FirstOrDefault(x => x.HasValue) ?? 0; public string Ports => string.Join(", ", ServerValidator.GetPorts(Definition).Where(x => x.HasValue)); public string PortsTooltip => Definition.ServerType == ServerType.Nats ? $"Client: {Definition.Nats.ClientPort}; Monitoring: {(Definition.Nats.UseTls ? "HTTPS" : "HTTP")} {Definition.Nats.MonitoringPort}" : $"Listen: {Definition.TibRv.ListenPort}; HTTP administration: {Definition.TibRv.HttpAdministrationPort}"; public string LogFile => Definition.Location == ServerLocation.Remote ? "Remote telemetry" : Definition.LogFilePath ?? "—"; public bool AutoStart => Definition.StartWithApplication; public bool HasTelemetry => Definition.ServerType == ServerType.Nats && Definition.Nats.MonitoringPort is not null || Definition.ServerType == ServerType.TibcoRendezvous && Definition.TibRv.HttpAdministrationPort is not null; public string RowActionTooltip => HasTelemetry ? "Inspect server telemetry" : "Open server log"; public string RowActionGlyph => HasTelemetry ? "\uE9D9" : "▤";
    public string WorkingDirectoryDisplay => string.IsNullOrWhiteSpace(Definition.WorkingDirectory) ? "Not configured" : Definition.WorkingDirectory; public string ConfigFileDisplay => string.IsNullOrWhiteSpace(Definition.ConfigFilePath) ? "Not configured" : Definition.ConfigFilePath; public string ArgumentsDisplay => string.IsNullOrWhiteSpace(Definition.AdditionalArguments) ? "Not configured" : Definition.AdditionalArguments; public string LaunchModeDisplay => Definition.LaunchMode switch { LaunchMode.ManagedOptions => "Managed options", LaunchMode.ConfigFile => "Configuration file", LaunchMode.CustomArguments => "Custom arguments", _ => Definition.LaunchMode.ToString() };
    public ServerStatus Status { get => status; set { if (Set(ref status, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); Raise(nameof(StatusTooltip)); Raise(nameof(CanStart)); Raise(nameof(CanStop)); } } }
    public string StatusText => Status.ToString(); public string StatusTooltip => $"{StatusText} — {Health}"; public Brush StatusBrush => Status switch { ServerStatus.Running => Brushes.LimeGreen, ServerStatus.Starting or ServerStatus.Stopping or ServerStatus.Restarting => Brushes.Goldenrod, ServerStatus.Failed => Brushes.IndianRed, ServerStatus.Invalid => Brushes.DarkOrange, ServerStatus.Stopped => Brushes.SlateGray, ServerStatus.Disabled => Brushes.LightGray, _ => Brushes.DarkGray };
    public int? Pid { get => pid; set { if (Set(ref pid, value)) { Raise(nameof(CanStart)); Raise(nameof(CanStop)); } } }
    public TimeSpan Uptime { get => uptime; set { if (Set(ref uptime, value)) Raise(nameof(UptimeText)); } }
    public string UptimeText => Definition.Location == ServerLocation.Remote || Pid is not null ? FormatDuration(Uptime) : "—"; public double Cpu { get => cpu; set { if (Set(ref cpu, value)) Raise(nameof(CpuText)); } }
    public string CpuText => $"{Cpu:F1}%"; public long Memory { get => memory; set { if (Set(ref memory, value)) Raise(nameof(MemoryText)); } }
    public string MemoryText => $"{Memory / 1048576d:F1} MB"; public string Health { get => health; set { if (Set(ref health, value)) Raise(nameof(StatusTooltip)); } }
    public string? LastError { get => error; set => Set(ref error, value); }
    public int? LastExitCode { get => exit; set { if (Set(ref exit, value)) Raise(nameof(LastExitCodeText)); } }
    public string LastExitCodeText => exit is null or < 0 ? "—" : exit.Value.ToString();
    public DateTime? Started { get => started; set => Set(ref started, value); }
    public DateTime? LastChecked { get => lastChecked; set => Set(ref lastChecked, value); }
    public DateTime? LastExitTime { get => lastExitTime; set => Set(ref lastExitTime, value); }
    public int RestartCount { get => restartCount; set => Set(ref restartCount, value); }
    public bool CanStart => Definition.Location == ServerLocation.Local && Definition.Enabled && Pid is null && Status is ServerStatus.Stopped or ServerStatus.Failed; public bool CanStop => Definition.Location == ServerLocation.Local && Pid is not null && Status is ServerStatus.Running or ServerStatus.Starting or ServerStatus.Failed; public bool CanOpenLog => Definition.Location == ServerLocation.Local && !string.IsNullOrWhiteSpace(Definition.LogFilePath); public bool HasRawTelemetry => !string.IsNullOrWhiteSpace(rawTelemetry); public string RawTelemetry => rawTelemetry; public int ConnectionCount => connections; public double MessageRateTotal => inMessageRate + outMessageRate; public string ConnectionsText => !HasRawTelemetry ? "—" : maximumConnections > 0 ? $"{Compact(connections)}/{Compact(maximumConnections)}" : Compact(connections); public string InMessageRateText => hasRateSample ? CompactRate(inMessageRate) : "—"; public string OutMessageRateText => hasRateSample ? CompactRate(outMessageRate) : "—"; public string MessageRateText => hasRateSample ? $"↓ {InMessageRateText}  ↑ {OutMessageRateText}" : "—"; public string MessageRateLineText => hasRateSample ? $"In {InMessageRateText}/s • Out {OutMessageRateText}/s" : "—"; public string MessageRateTooltip => hasRateSample ? $"Inbound: {inMessageRate:N1} messages/sec\nOutbound: {outMessageRate:N1} messages/sec" : "Waiting for a second telemetry sample to calculate rate."; public string SparklineTooltip => metricHistory.Count == 0 ? "Waiting for telemetry history" : $"{metricHistory.Count} sample(s) over the recent telemetry window"; public PointCollection ConnectionsSparkline => BuildSparkline(x => x.Connections); public PointCollection MessageRateSparkline => BuildSparkline(x => x.MessageRate); public PointCollection CpuSparkline => BuildSparkline(x => x.Cpu); public PointCollection MemorySparkline => BuildSparkline(x => x.MemoryMegabytes); public string ActivityTooltip => !HasRawTelemetry ? "Waiting for telemetry" : $"Connections: {connections:N0}{(maximumConnections > 0 ? $" / {maximumConnections:N0}" : "")}\nMessages/sec inbound: {inMessageRate:N1}\nMessages/sec outbound: {outMessageRate:N1}\nBytes/sec inbound: {FormatRate(inByteRate)}\nBytes/sec outbound: {FormatRate(outByteRate)}\nSubscriptions: {subscriptions:N0}\n{(Definition.ServerType == ServerType.Nats ? $"Slow consumers: {slowConsumers:N0}" : $"Packets missed: {slowConsumers:N0}; data loss in/out: {routes:N0}/{remotes:N0}")}"; public string RemoteMetricsSummary => Definition.ServerType == ServerType.TibcoRendezvous ? TibRvMetricsSummary : $"IDENTITY\nServer name: {Value(serverName)}\nServer ID: {Value(serverId)}\nVersion: {Value(serverVersion)}\nCluster: {Value(clusterName)}\nTags: {Value(serverTags)}\nMetadata: {Value(serverMetadata)}\nEndpoint: {Endpoint}\nHealth endpoint: {(healthEndpointHealthy ? "Healthy" : "Unavailable")}\n\nLOAD\nConnections: {connections:N0} / {(maximumConnections > 0 ? maximumConnections.ToString("N0") : "unlimited")}\nTotal connections since start: {totalConnections:N0}\nSubscriptions: {subscriptions:N0}\nCPU: {CpuText}\nMemory: {MemoryText}\n\nTRAFFIC\nMessages/sec in / out: {inMessageRate:N1} / {outMessageRate:N1}\nBytes/sec in / out: {FormatRate(inByteRate)} / {FormatRate(outByteRate)}\nLifetime messages in / out: {inMessages:N0} / {outMessages:N0}\nLifetime bytes in / out: {FormatBytes(inBytes)} / {FormatBytes(outBytes)}\n\nTOPOLOGY\nRoutes: {routes:N0}\nRemotes: {remotes:N0}\nLeaf nodes: {leafNodes:N0}\n\nWARNINGS\nSlow consumers: {slowConsumers:N0}"; string TibRvMetricsSummary => $"IDENTITY\nComponent: {Value(serverName)}\nVersion: {Value(serverVersion)}\nHost: {Value(serverId)}\nService: {Value(clusterName)}\nNetwork: {Value(serverTags)}\nEndpoint: {Endpoint}\n\nLOAD\nClient connections: {connections:N0}\nSubscriptions: {subscriptions:N0}\nUptime: {UptimeText}\n\nTRAFFIC\nMessages/sec in / out: {inMessageRate:N1} / {outMessageRate:N1}\nBytes/sec in / out: {FormatRate(inByteRate)} / {FormatRate(outByteRate)}\nLifetime messages in / out: {inMessages:N0} / {outMessages:N0}\nLifetime bytes in / out: {FormatBytes(inBytes)} / {FormatBytes(outBytes)}\n\nDELIVERY WARNINGS\nInbound / outbound data loss: {routes:N0} / {remotes:N0}\nPackets retransmitted: {leafNodes:N0}\nPackets missed: {slowConsumers:N0}"; public void ApplyTelemetry(RemoteServerTelemetry value) { ApplyRates(value.SampleTime, value.InMessages, value.OutMessages, value.InBytes, value.OutBytes); Cpu = value.CpuPercent; Memory = value.MemoryBytes; serverId = value.ServerId; serverName = value.ServerName; serverVersion = value.Version; connections = value.Connections; subscriptions = value.Subscriptions; inMessages = value.InMessages; outMessages = value.OutMessages; inBytes = value.InBytes; outBytes = value.OutBytes; slowConsumers = value.SlowConsumers; maximumConnections = value.MaximumConnections; totalConnections = value.TotalConnections; routes = value.Routes; remotes = value.Remotes; leafNodes = value.LeafNodes; clusterName = value.ClusterName; serverTags = value.ServerTags; serverMetadata = value.ServerMetadata; healthEndpointHealthy = value.HealthEndpointHealthy; rawTelemetry = value.RawJson; FinishTelemetry(value.Uptime); }
    public void ApplyTelemetry(TibRvTelemetry value) { ApplyRates(value.SampleTime, value.InMessages, value.OutMessages, value.InBytes, value.OutBytes); serverName = value.Component; serverVersion = value.Version; serverId = value.Host; clusterName = value.Service; serverTags = value.Network; connections = value.ClientConnections; subscriptions = value.Subscriptions; inMessages = value.InMessages; outMessages = value.OutMessages; inBytes = value.InBytes; outBytes = value.OutBytes; routes = (int)Math.Min(int.MaxValue, value.InDataLoss); remotes = (int)Math.Min(int.MaxValue, value.OutDataLoss); leafNodes = (int)Math.Min(int.MaxValue, value.RetransmittedPackets); slowConsumers = (int)Math.Min(int.MaxValue, value.MissedPackets); rawTelemetry = value.RawMetrics; FinishTelemetry(value.Uptime); }
    void ApplyRates(DateTimeOffset sample, long newInMessages, long newOutMessages, long newInBytes, long newOutBytes) { if (previousSample.HasValue) { var seconds = (sample - previousSample.Value).TotalSeconds; if (seconds > 0) { inMessageRate = Math.Max(0, (newInMessages - previousInMessages) / seconds); outMessageRate = Math.Max(0, (newOutMessages - previousOutMessages) / seconds); inByteRate = Math.Max(0, (newInBytes - previousInBytes) / seconds); outByteRate = Math.Max(0, (newOutBytes - previousOutBytes) / seconds); hasRateSample = true; } } previousSample = sample; previousInMessages = newInMessages; previousOutMessages = newOutMessages; previousInBytes = newInBytes; previousOutBytes = newOutBytes; }
    void FinishTelemetry(TimeSpan value) { Uptime = value; Started = DateTime.Now - value; Raise(nameof(RemoteMetricsSummary)); Raise(nameof(RawTelemetry)); Raise(nameof(HasRawTelemetry)); Raise(nameof(ConnectionCount)); Raise(nameof(MessageRateTotal)); Raise(nameof(ConnectionsText)); Raise(nameof(InMessageRateText)); Raise(nameof(OutMessageRateText)); Raise(nameof(MessageRateText)); Raise(nameof(MessageRateLineText)); Raise(nameof(MessageRateTooltip)); Raise(nameof(ActivityTooltip)); }
    public void RecordMetricSample(DateTimeOffset sampleTime, int historyMinutes) { var minutes = Math.Clamp(historyMinutes, 1, 240); metricHistory.Add(new(sampleTime, connections, hasRateSample ? inMessageRate + outMessageRate : 0, Cpu, Memory / 1048576d)); PruneMetricHistory(sampleTime, minutes); RaiseSparklineProperties(); }
    public void PruneMetricHistory(DateTimeOffset now, int historyMinutes) { var cutoff = now.AddMinutes(-Math.Clamp(historyMinutes, 1, 240)); metricHistory.RemoveAll(x => x.SampleTime < cutoff); RaiseSparklineProperties(); }
    public void ClearMetricHistory() { if (metricHistory.Count == 0) return; metricHistory.Clear(); RaiseSparklineProperties(); }
    void RaiseSparklineProperties() { Raise(nameof(SparklineTooltip)); Raise(nameof(ConnectionsSparkline)); Raise(nameof(MessageRateSparkline)); Raise(nameof(CpuSparkline)); Raise(nameof(MemorySparkline)); }
    PointCollection BuildSparkline(Func<MetricHistoryPoint, double> selector) { const double width = 120, height = 28, pad = 2; var points = new PointCollection(); if (metricHistory.Count == 0) return points; if (metricHistory.Count == 1) { points.Add(new(0, height / 2)); points.Add(new(width, height / 2)); return points; } var min = metricHistory.Min(selector); var max = metricHistory.Max(selector); var start = metricHistory[0].SampleTime; var span = Math.Max(1, (metricHistory[^1].SampleTime - start).TotalMilliseconds); foreach (var sample in metricHistory) { var x = (sample.SampleTime - start).TotalMilliseconds / span * width; var normalized = Math.Abs(max - min) < 0.0001 ? 0.5 : (selector(sample) - min) / (max - min); var y = height - pad - normalized * (height - pad * 2); points.Add(new(Math.Clamp(x, 0, width), Math.Clamp(y, pad, height - pad))); } return points; }
    static string FormatDuration(TimeSpan value) => value.TotalDays >= 1 ? $"{(int)value.TotalDays}d {value.Hours}h" : value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes}m {value.Seconds}s" : $"{Math.Max(0, (int)value.TotalSeconds)}s"; static string FormatBytes(long value) => value >= 1073741824 ? $"{value / 1073741824d:F1} GB" : value >= 1048576 ? $"{value / 1048576d:F1} MB" : value >= 1024 ? $"{value / 1024d:F1} KB" : $"{value} B"; static string FormatRate(double value) => FormatBytes((long)value) + "/s"; static string Compact(long value) => value >= 1000000 ? $"{value / 1000000d:0.#}m" : value >= 1000 ? $"{value / 1000d:0.#}k" : value.ToString("N0"); static string CompactRate(double value) => value >= 1000000 ? $"{value / 1000000d:0.#}m" : value >= 1000 ? $"{value / 1000d:0.#}k" : $"{value:0.#}"; static string Value(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value; public void RefreshDefinition() { Raise(nameof(Name)); Raise(nameof(TypeText)); Raise(nameof(LocationText)); Raise(nameof(Endpoint)); Raise(nameof(ExecutableDisplay)); Raise(nameof(PrimaryPort)); Raise(nameof(Ports)); Raise(nameof(PortsTooltip)); Raise(nameof(LogFile)); Raise(nameof(AutoStart)); Raise(nameof(CanOpenLog)); Raise(nameof(HasTelemetry)); Raise(nameof(RowActionTooltip)); Raise(nameof(RowActionGlyph)); Raise(nameof(RemoteMetricsSummary)); }
    public bool IsTelemetryStale => telemetryStale; public DateTimeOffset? LastTelemetrySuccess => lastTelemetrySuccess; public string TelemetryFreshnessText => !HasRawTelemetry ? "No telemetry collected" : telemetryStale ? $"Stale — last successful sample {lastTelemetrySuccess?.LocalDateTime:G}" : $"Current — sampled {lastTelemetrySuccess?.LocalDateTime:G}"; public void MarkTelemetryAvailable() { telemetryStale = false; lastTelemetrySuccess = DateTimeOffset.Now; Raise(nameof(IsTelemetryStale)); Raise(nameof(LastTelemetrySuccess)); Raise(nameof(TelemetryFreshnessText)); Raise(nameof(ActivityTooltip)); }
    public void MarkTelemetryUnavailable() { if (!HasRawTelemetry) return; telemetryStale = true; hasRateSample = false; previousSample = null; inMessageRate = outMessageRate = inByteRate = outByteRate = 0; Raise(nameof(IsTelemetryStale)); Raise(nameof(TelemetryFreshnessText)); Raise(nameof(InMessageRateText)); Raise(nameof(OutMessageRateText)); Raise(nameof(MessageRateText)); Raise(nameof(MessageRateLineText)); Raise(nameof(MessageRateTooltip)); Raise(nameof(MessageRateTotal)); Raise(nameof(ActivityTooltip)); }
    public void ReconcileDefinitionState() { if (Pid is null) Status = Definition.Enabled ? ServerStatus.Stopped : ServerStatus.Disabled; Raise(nameof(CanStart)); Raise(nameof(CanStop)); Raise(nameof(StatusTooltip)); }
    public void RefreshDisplayValues() { Raise(nameof(WorkingDirectoryDisplay)); Raise(nameof(ConfigFileDisplay)); Raise(nameof(ArgumentsDisplay)); Raise(nameof(LaunchModeDisplay)); }
}
public sealed class MainViewModel : ObservableObject, IDisposable
{
    public async Task ShutdownAsync()
    {
        if (disposed) return;
        disposed = true;
        relativeTimeTimer.Stop();
        Exception? failure = null;
        try { await SaveRuntimeAsync(); } catch (Exception ex) { failure = ex; }
        shutdown.Cancel();
        monitoringIntervalChanged.Cancel();
        try
        {
            try { await monitorTask; } catch (OperationCanceledException) { } catch (Exception ex) { failure ??= ex; }
            Task[] pending; lock (backgroundGate) pending = backgroundTasks.ToArray();
            try { await Task.WhenAll(pending); } catch (OperationCanceledException) { } catch (Exception ex) { failure ??= ex.GetBaseException(); }
            try { await SaveRuntimeAsync(); } catch (Exception ex) { failure ??= ex; }
        }
        finally
        {
            processes.Dispose();
            monitoringIntervalChanged.Dispose();
            shutdown.Dispose();
        }
        if (failure is not null) AsyncCommandErrors.Report(new IOException("The application closed safely, but runtime state could not be fully saved.", failure));
    }
    readonly RotatingTextLog monitoringLog = new(); readonly object backgroundGate = new(); readonly HashSet<Task> backgroundTasks = []; Task monitorTask = Task.CompletedTask; bool disposed;
    readonly PathResolver paths; readonly IConfigurationStore store; readonly ConfigurationTransferService transfer; readonly Dictionary<ServerType, IServerAdapter> adapters; readonly ProcessManager processes; readonly LogTailReader logs = new(); readonly CancellationTokenSource shutdown = new(); CancellationTokenSource monitoringIntervalChanged = new(); readonly System.Windows.Threading.DispatcherTimer relativeTimeTimer = new() { Interval = TimeSpan.FromSeconds(1) }; readonly HashSet<Guid> intentionalExits = []; readonly Dictionary<Guid, string> telemetryErrors = []; ServerRowViewModel? selected; string logText = "Select a server to view its log."; bool paused; DateTime lastUpdated; int selectedDetailTabIndex;
    public string ServerSummaryText => $"{Servers.Count(x => x.Status == ServerStatus.Running)} running  •  {Servers.Count(x => x.Status == ServerStatus.Stopped)} stopped  •  {Servers.Count(x => x.Status == ServerStatus.Disabled)} disabled  •  {Servers.Count} total";
    public ObservableCollection<ServerRowViewModel> Servers { get; } = []; public GlobalSettings Settings { get; }
    public ServerRowViewModel? Selected { get => selected; set { if (Set(ref selected, value)) { Raise(nameof(SelectedEffectiveArguments)); Raise(nameof(SelectedConfigurationSummary)); Raise(nameof(SelectedRemoteMetrics)); Raise(nameof(SelectedRawTelemetry)); RaiseCommands(); _ = RefreshLogAsync(); } } }
    public int SelectedDetailTabIndex { get => selectedDetailTabIndex; set => Set(ref selectedDetailTabIndex, value); }
    public string LogText { get => logText; set => Set(ref logText, value); }
    public bool IsLogPaused { get => paused; set => Set(ref paused, value); }
    public DateTime LastUpdated { get => lastUpdated; set { if (Set(ref lastUpdated, value)) Raise(nameof(LastUpdatedText)); } }
    public string LastUpdatedText { get { if (LastUpdated == default) return "Waiting for first refresh"; var age = DateTime.Now - LastUpdated; var relative = age.TotalSeconds < 2 ? "just now" : age.TotalSeconds < 60 ? $"{(int)age.TotalSeconds}s ago" : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago" : $"{(int)age.TotalHours}h ago"; return $"Updated {relative} • every {Settings.MonitoringIntervalSeconds}s"; } }
    public string SelectedEffectiveArguments { get { if (Selected is null) return ""; if (Selected.Definition.Location == ServerLocation.Remote) return Selected.Endpoint; try { return adapters[Selected.Definition.ServerType].BuildStartInfo(Selected.Definition, Settings).Arguments; } catch (Exception ex) { return "Unavailable: " + ex.Message; } } }
    public string SelectedRemoteMetrics => Selected?.RemoteMetricsSummary ?? "Select a server."; public string SelectedRawTelemetry => Selected?.RawTelemetry ?? "No telemetry has been collected."; public string SelectedConfigurationSummary => Selected is null ? "Select a server." : $"Name: {Selected.Name}\nType: {Selected.TypeText}\nLocation: {Selected.LocationText}\nEnabled: {Selected.Definition.Enabled}\nLaunch mode: {Selected.Definition.LaunchMode}\nExecutable / endpoint: {Selected.ExecutableDisplay}\nWorking directory: {Selected.Definition.WorkingDirectory}\nConfig file: {Selected.Definition.ConfigFilePath}\nLog file: {Selected.Definition.LogFilePath}\nPorts: {Selected.Ports}\nTLS: {Selected.Definition.Nats.UseTls}\nAuto start: {Selected.Definition.StartWithApplication}\nAuto restart: {Selected.Definition.AutoRestart}";
    public ICommand AddServerCommand { get; }
    public ICommand EditServerCommand { get; }
    public ICommand DeleteServerCommand { get; }
    public ICommand ImportConfigurationCommand { get; }
    public ICommand ExportConfigurationCommand { get; }
    public ICommand StartServerCommand { get; }
    public ICommand StopServerCommand { get; }
    public ICommand RestartServerCommand { get; }
    public ICommand InspectServerCommand { get; }
    public ICommand StartAllCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand RestartAllCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RefreshSelectedTelemetryCommand { get; }
    public ICommand CopyRawTelemetryCommand { get; }
    public ICommand OpenRawTelemetryCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand PauseLogCommand { get; }
    public ICommand ResumeLogCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand SettingsCommand { get; }
    public MainViewModel(PathResolver p, IConfigurationStore s, ConfigurationTransferService configurationTransfer, IEnumerable<IServerAdapter> a, GlobalSettings settings) { paths = p; store = s; transfer = configurationTransfer; adapters = a.ToDictionary(x => x.ServerType); Settings = settings; Servers.CollectionChanged += OnServersChanged; processes = new(a, settings); processes.ProcessExited += OnExited; relativeTimeTimer.Tick += (_, _) => Raise(nameof(LastUpdatedText)); relativeTimeTimer.Start(); AddServerCommand = new AsyncRelayCommand(AddAsync); EditServerCommand = new AsyncRelayCommand(EditAsync, () => Selected is not null && !Selected.CanStop); DeleteServerCommand = new AsyncRelayCommand(DeleteAsync, () => Selected is not null && !Selected.CanStop); ImportConfigurationCommand = new AsyncRelayCommand(ImportConfigurationAsync, () => !Servers.Any(x => x.CanStop)); ExportConfigurationCommand = new AsyncRelayCommand(ExportConfigurationAsync); StartServerCommand = new AsyncRelayCommand<ServerRowViewModel>(async r => { if (r is not null) await StartAsync(r); }, r => r?.CanStart == true); StopServerCommand = new AsyncRelayCommand<ServerRowViewModel>(async r => { if (r is not null) await StopAsync(r); }, r => r?.CanStop == true); RestartServerCommand = new AsyncRelayCommand<ServerRowViewModel>(async r => { if (r is not null) await RestartAsync(r); }, r => r?.CanStop == true); InspectServerCommand = new RelayCommand<ServerRowViewModel>(InspectServer, r => r is not null && (r.HasTelemetry || r.CanOpenLog)); StartAllCommand = new AsyncRelayCommand(() => ForEachAsync(x => x.CanStart, StartAsync), () => Servers.Any(x => x.CanStart)); StopAllCommand = new AsyncRelayCommand(() => ForEachAsync(x => x.CanStop, StopAsync), () => Servers.Any(x => x.CanStop)); RestartAllCommand = new AsyncRelayCommand(() => ForEachAsync(x => x.CanStop, RestartAsync), () => Servers.Any(x => x.CanStop)); RefreshCommand = new AsyncRelayCommand(RefreshAllAsync); RefreshSelectedTelemetryCommand = new AsyncRelayCommand(RefreshSelectedTelemetryAsync, () => Selected?.HasTelemetry == true); CopyRawTelemetryCommand = new RelayCommand(() => Clipboard.SetText(SelectedRawTelemetry), () => Selected?.HasRawTelemetry == true); OpenRawTelemetryCommand = new RelayCommand(() => OpenPath(Selected!.Endpoint, false), () => Selected?.HasTelemetry == true); OpenLogCommand = new RelayCommand(() => OpenPath(LogPath(Selected!), false), () => Selected?.CanOpenLog == true); OpenLogFolderCommand = new RelayCommand(() => OpenPath(LogPath(Selected!), true), () => Selected?.CanOpenLog == true); PauseLogCommand = new RelayCommand(PauseLog, () => !IsLogPaused); ResumeLogCommand = new RelayCommand(ResumeLog, () => IsLogPaused); ClearLogCommand = new RelayCommand(() => LogText = ""); SettingsCommand = new AsyncRelayCommand(EditSettingsAsync); }
    public async Task InitializeAsync() { Directory.CreateDirectory(paths.Resolve(Settings.LoggingRootDirectory)); Directory.CreateDirectory(paths.Resolve("sample-config")); await EnsureSamplesAsync(); ConfigurationEnvelope<List<ServerDefinition>> envelope; List<ServerDefinition> defs; var migrate = false; try { envelope = await store.LoadAsync("servers.json", new ConfigurationEnvelope<List<ServerDefinition>> { Data = null! }); defs = envelope.Data ?? Defaults(); migrate = envelope.Data is null; } catch (ConfigurationLoadException) { var legacy = await store.LoadAsync<List<ServerDefinition>?>("servers.json", null); defs = legacy ?? throw new ConfigurationLoadException("The server configuration is unreadable in both current and legacy formats."); envelope = new() { Data = defs }; migrate = true; } var identityErrors = ServerValidator.ValidateIdentities(defs); if (identityErrors.Count > 0) throw new ConfigurationLoadException("Server configuration contains invalid process identities:" + Environment.NewLine + string.Join(Environment.NewLine, identityErrors)); foreach (var d in defs) Servers.Add(new(d)); if (migrate) await SaveAsync(); ConfigurationEnvelope<List<RuntimeProcessState>> runtime; try { runtime = await store.LoadAsync("runtime.json", new ConfigurationEnvelope<List<RuntimeProcessState>> { Data = [] }); } catch (ConfigurationLoadException ex) { Debug.WriteLine(ex.Message); QuarantineCorruptRuntimeFiles(); runtime = new() { Data = [] }; } foreach (var state in runtime.Data) { var row = Servers.FirstOrDefault(x => x.Definition.Id == state.ServerId); if (row is null) continue; row.LastExitCode = state.LastExitCode; row.LastExitTime = state.LastExitTimeUtc?.ToLocalTime(); row.RestartCount = state.RestartCount; if (row.Definition.Enabled && row.Definition.Location == ServerLocation.Local && state.ProcessId > 0 && processes.TryRecover(row.Definition, state)) await RefreshAsync(row); } Selected = Servers.FirstOrDefault(); if (Settings.AutoStartEnabledServers) foreach (var r in Servers.Where(x => x.CanStart && x.Definition.StartWithApplication && processes.Get(x.Definition.Id) is null)) await StartAsync(r); await RefreshAllAsync(); await SaveRuntimeAsync(); _ = MonitorLoopAsync(shutdown.Token); }
    async Task EnsureSamplesAsync() { var n = paths.Resolve("sample-config/nats-server.conf"); if (!File.Exists(n)) await File.WriteAllTextAsync(n, "server_name: local-nats\nport: 4222\nhttp: 8222\nlog_file: 'logs/nats/local-nats.log'\ndebug: false\ntrace: false\n"); var r = paths.Resolve("sample-config/rvdaemon.args.txt"); if (!File.Exists(r)) await File.WriteAllTextAsync(r, "# Verify flags against your installed TIBCO RV version\n-listen localhost:7500 -reliability 60 -http localhost:7580\n"); }
    void QuarantineCorruptRuntimeFiles() { var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"); foreach (var source in new[] { paths.Resolve("runtime.json"), paths.Resolve("runtime.json.bak") }) { if (!File.Exists(source)) continue; try { File.Move(source, source + ".corrupt-" + stamp, true); } catch (Exception ex) { Debug.WriteLine($"Could not quarantine {source}: {ex.Message}"); } } }
    static List<ServerDefinition> Defaults() =>
    [
     new(){Name="Local NATS",Enabled=false,Executable="nats-server.exe",LaunchMode=LaunchMode.ConfigFile,ConfigFilePath="sample-config/nats-server.conf",LogFilePath="nats/local-nats.log",Nats=new(){ServerName="local-nats",ClientPort=4222,MonitoringPort=8222}},
  new(){Name="Remote NATS (Local Sample)",Location=ServerLocation.Remote,Enabled=true,HealthCheckHost="localhost",Nats=new(){ServerName="local-nats-remote-view",ClientPort=4222,MonitoringPort=8222}},
  new(){Name="Local NATS TLS",Enabled=false,Executable="nats-server.exe",LaunchMode=LaunchMode.ManagedOptions,LogFilePath="nats/local-nats-tls.log",Nats=new(){ServerName="local-nats-tls",ClientPort=4223,MonitoringPort=8223,UseTls=true,TlsCertificatePath=TestCertificatePath("nats-server.pem"),TlsPrivateKeyPath=TestCertificatePath("nats-server.key"),TlsCaCertificatePath=TestCertificatePath("ca.pem")}},
  new(){Name="Local RV Daemon",ServerType=ServerType.TibcoRendezvous,Enabled=false,Executable="rvdaemon.exe",LaunchMode=LaunchMode.ManagedOptions,LogFilePath="tibrv/local-rvdaemon.log",TibRv=new(){ListenPort=7500,HttpAdministrationPort=7580}}
    ];
    static string TestCertificatePath(string fileName) => Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "test-certificates", fileName));
    void OnServersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (ServerRowViewModel row in e.OldItems) row.PropertyChanged -= OnServerRowChanged;
        if (e.NewItems is not null) foreach (ServerRowViewModel row in e.NewItems) row.PropertyChanged += OnServerRowChanged;
        Raise(nameof(ServerSummaryText)); RaiseCommands();
    }
    void OnServerRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerRowViewModel.Status)) Raise(nameof(ServerSummaryText));
        if (e.PropertyName is nameof(ServerRowViewModel.Status) or nameof(ServerRowViewModel.CanStart) or nameof(ServerRowViewModel.CanStop) or nameof(ServerRowViewModel.HasRawTelemetry) or nameof(ServerRowViewModel.HasTelemetry)) RaiseCommands();
    }
    Task MonitorLoopAsync(CancellationToken ct) => monitorTask = MonitorLoopCoreAsync(ct); async Task MonitorLoopCoreAsync(CancellationToken ct) { while (!ct.IsCancellationRequested) { try { var changed = monitoringIntervalChanged.Token; using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, changed); try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, Settings.MonitoringIntervalSeconds)), wait.Token); } catch (OperationCanceledException) when (changed.IsCancellationRequested && !ct.IsCancellationRequested) { continue; } await RefreshAllAsync(); if (!IsLogPaused) await RefreshLogAsync(); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; } catch (Exception ex) { Debug.WriteLine($"Monitoring cycle failed: {ex}"); } } }
    void ApplyMonitoringIntervalImmediately() { var previous = Interlocked.Exchange(ref monitoringIntervalChanged, new CancellationTokenSource()); previous.Cancel(); previous.Dispose(); Raise(nameof(LastUpdatedText)); }
    void ApplySparklineSettings() { if (!Settings.ShowMetricSparklines) foreach (var row in Servers) row.ClearMetricHistory(); else foreach (var row in Servers) row.PruneMetricHistory(DateTimeOffset.Now, Settings.MetricSparklineMinutes); Raise(nameof(Settings)); }
    void UpdateMetricHistory(ServerRowViewModel row) { if (!Settings.ShowMetricSparklines || !row.Definition.Enabled || row.Pid is null && !row.HasRawTelemetry) { row.ClearMetricHistory(); return; } row.RecordMetricSample(DateTimeOffset.Now, Settings.MetricSparklineMinutes); }
    internal async Task RefreshAllAsync() { foreach (var r in Servers) { try { await RefreshAsync(r); UpdateMetricHistory(r); } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { throw; } catch (Exception ex) { r.LastError = ex.GetBaseException().Message; r.Status = ServerStatus.Failed; r.Health = "Refresh failed: " + r.LastError; UpdateMetricHistory(r); try { await LogTelemetryFailureAsync(r, ex); } catch (Exception logException) { Debug.WriteLine($"Could not log refresh failure: {logException.Message}"); } } } LastUpdated = DateTime.Now; Raise(nameof(ServerSummaryText)); }
    async Task RefreshAsync(ServerRowViewModel r)
    {
        r.LastChecked = DateTime.Now; if (!r.Definition.Enabled) { r.Pid = null; r.Cpu = 0; r.Memory = 0; r.Status = ServerStatus.Disabled; r.Health = "Disabled"; return; }
        if (r.Definition.Location == ServerLocation.Remote) { await RefreshRemoteAsync(r); return; }
        var m = processes.Get(r.Definition.Id); if (m is null) { r.Pid = null; r.Cpu = 0; r.Memory = 0; var validation = ServerValidator.Validate(r.Definition, Servers.Select(x => x.Definition), paths.Resolve); r.Status = validation.IsValid ? ServerStatus.Stopped : ServerStatus.Invalid; r.Health = validation.IsValid ? "Process is stopped." : string.Join(" ", validation.Errors); return; }
        try { var p = m.Process; var now = DateTime.UtcNow; var started = p.StartTime.ToUniversalTime(); var cpu = p.TotalProcessorTime; r.Cpu = CpuUsageCalculator.Calculate(cpu - m.PreviousCpu, now - m.PreviousSampleUtc, Environment.ProcessorCount); m.PreviousCpu = cpu; m.PreviousSampleUtc = now; r.Pid = p.Id; r.Started = started.ToLocalTime(); r.Uptime = now - started; r.Memory = p.WorkingSet64; var h = await adapters[r.Definition.ServerType].CheckHealthAsync(r.Definition, new(p.Id, started, r.Definition.Executable), shutdown.Token); var telemetryAvailable = await RefreshProductTelemetryAsync(r); r.Health = telemetryAvailable ? $"{r.TypeText} telemetry healthy" : $"{h.Message} Telemetry unavailable."; if (h.IsHealthy) { r.Status = ServerStatus.Running; if (telemetryAvailable) r.LastError = null; } else if (r.Uptime < TimeSpan.FromSeconds(Math.Max(0, r.Definition.HealthCheckGracePeriodSeconds))) r.Status = ServerStatus.Starting; else { r.Status = ServerStatus.Failed; r.LastError = h.Message; } } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { } catch (Exception ex) { r.LastError = ex.Message; r.Status = ServerStatus.Failed; }
    }
    async Task RefreshRemoteAsync(ServerRowViewModel r)
    {
        r.Pid = null; var validation = ServerValidator.Validate(r.Definition, Servers.Select(x => x.Definition), paths.Resolve); if (!validation.IsValid) { r.Status = ServerStatus.Invalid; r.Health = string.Join(" ", validation.Errors); return; }
        if (await RefreshProductTelemetryAsync(r)) { r.Status = ServerStatus.Running; r.Health = "Remote telemetry healthy"; r.LastError = null; return; }
        var reachability = await adapters[r.Definition.ServerType].CheckHealthAsync(r.Definition, null, shutdown.Token); if (reachability.IsHealthy) { r.Status = ServerStatus.Running; r.Health = "Client port reachable; telemetry unavailable."; } else { r.Status = ServerStatus.Failed; r.Health = "Server unreachable"; r.LastError = reachability.Message; }
    }
    async Task<bool> RefreshProductTelemetryAsync(ServerRowViewModel r)
    {
        if (!r.HasTelemetry) return false; try { switch (adapters[r.Definition.ServerType]) { case IRemoteServerMonitor nats: r.ApplyTelemetry(await nats.GetTelemetryAsync(r.Definition, shutdown.Token)); break; case ITibRvMonitor rv: r.ApplyTelemetry(await rv.GetTelemetryAsync(r.Definition, shutdown.Token)); break; default: return false; } r.MarkTelemetryAvailable(); telemetryErrors.Remove(r.Definition.Id); if (Selected == r) { Raise(nameof(SelectedRemoteMetrics)); Raise(nameof(SelectedRawTelemetry)); } RaiseCommands(); return true; } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return false; } catch (Exception ex) { r.MarkTelemetryUnavailable(); r.LastError = ex.Message; try { await LogTelemetryFailureAsync(r, ex); } catch (Exception logException) { Debug.WriteLine($"Could not write telemetry failure log: {logException.Message}"); } return false; }
    }
    async Task LogTelemetryFailureAsync(ServerRowViewModel r, Exception ex) { var message = ex.GetBaseException().Message; if (telemetryErrors.GetValueOrDefault(r.Definition.Id) == message) return; var path = paths.Resolve(Path.Combine(Settings.LoggingRootDirectory, "monitoring.log")); await monitoringLog.AppendLineAsync(path, $"{DateTimeOffset.Now:O} [{r.Name}] {r.Endpoint} — {message}", Settings.MonitoringLogMaximumBytes, Settings.MonitoringLogRetainedFiles, shutdown.Token); telemetryErrors[r.Definition.Id] = message; }
    async Task RefreshSelectedTelemetryAsync() { if (Selected is not null) { await RefreshAsync(Selected); UpdateMetricHistory(Selected); } }
    void InspectServer(ServerRowViewModel? row) { if (row is null) return; Selected = row; if (row.HasTelemetry) SelectedDetailTabIndex = 1; else if (row.CanOpenLog) OpenPath(LogPath(row), false); }
    async Task StartAsync(ServerRowViewModel r) { var v = ServerValidator.Validate(r.Definition, Servers.Select(x => x.Definition), paths.Resolve); if (!v.IsValid) { MessageBox.Show(string.Join("\n", v.Errors), "Cannot start", MessageBoxButton.OK, MessageBoxImage.Warning); return; } try { r.Status = ServerStatus.Starting; await processes.StartAsync(r.Definition, shutdown.Token); await RefreshAsync(r); UpdateMetricHistory(r); await SaveRuntimeAsync(); } catch (Exception ex) { r.LastError = ex.Message; r.Status = ServerStatus.Failed; } RaiseCommands(); }
    async Task StopAsync(ServerRowViewModel r) { if (Settings.ConfirmForceKill && r.Definition.ForceKillAfterTimeout && MessageBox.Show("If graceful shutdown times out, the process tree will be force-killed. Continue?", "Stop server", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; intentionalExits.Add(r.Definition.Id); r.Status = ServerStatus.Stopping; var x = await processes.StopAsync(r.Definition, shutdown.Token); if (!x.Stopped) { intentionalExits.Remove(r.Definition.Id); r.LastError = x.Error; r.Status = ServerStatus.Failed; } else r.Status = ServerStatus.Stopped; await SaveRuntimeAsync(); RaiseCommands(); }
    async Task RestartAsync(ServerRowViewModel r) { intentionalExits.Add(r.Definition.Id); r.Status = ServerStatus.Restarting; try { await processes.RestartAsync(r.Definition, shutdown.Token); r.RestartCount++; await RefreshAsync(r); UpdateMetricHistory(r); await SaveRuntimeAsync(); } catch (Exception ex) { intentionalExits.Remove(r.Definition.Id); r.LastError = ex.Message; r.Status = ServerStatus.Failed; } RaiseCommands(); }
    async Task ForEachAsync(Func<ServerRowViewModel, bool> f, Func<ServerRowViewModel, Task> a) { foreach (var r in Servers.Where(f).ToList()) await a(r); }
    async Task AddAsync() { var d = new ServerDefinition { GracefulStopTimeoutSeconds = Settings.DefaultGracefulStopTimeoutSeconds, ForceKillAfterTimeout = Settings.DefaultForceKillPolicy, ManageConfigFile = true }; d.ConfigFilePath = $"generated-config/nats-{d.Id:N}.conf"; var w = new ServerEditorWindow(d, Servers.Select(x => x.Definition).ToList(), paths); if (w.ShowDialog() == true) { var previous = Selected; var row = new ServerRowViewModel(d); Servers.Add(row); Selected = row; if (!await SaveWithErrorAsync(SaveAsync)) { Servers.Remove(row); Selected = previous; } } }
    async Task EditAsync() { if (Selected is null) return; var row = Selected; var original = row.Definition.Clone(); var c = row.Definition.Clone(); var w = new ServerEditorWindow(c, Servers.Select(x => x.Definition).ToList(), paths); if (w.ShowDialog() == true) { Copy(c, row.Definition); RefreshEditedRow(row); if (!await SaveWithErrorAsync(SaveAsync)) { Copy(original, row.Definition); RefreshEditedRow(row); } } }
    async Task DeleteAsync() { if (Selected is null) return; if (Selected.CanStop) { MessageBox.Show("Stop the server before deleting it.", "Server is running", MessageBoxButton.OK, MessageBoxImage.Warning); return; } if (MessageBox.Show($"Delete '{Selected.Name}'?", "Confirm deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; var removed = Selected; var index = Servers.IndexOf(removed); Servers.RemoveAt(index); Selected = Servers.FirstOrDefault(); if (!await SaveWithErrorAsync(SaveAsync)) { Servers.Insert(index, removed); Selected = removed; } }
    void RefreshEditedRow(ServerRowViewModel row) { row.RefreshDefinition(); row.RefreshDisplayValues(); row.ReconcileDefinitionState(); Raise(nameof(SelectedEffectiveArguments)); Raise(nameof(SelectedConfigurationSummary)); RaiseCommands(); }
    async Task ExportConfigurationAsync() { var dialog = new SaveFileDialog { Title = "Export Messaging Server Manager configuration", Filter = "Messaging Server Manager configuration (*.msmconfig.json)|*.msmconfig.json|JSON files (*.json)|*.json", FileName = $"MessagingServerManager-{DateTime.Now:yyyyMMdd-HHmmss}.msmconfig.json", AddExtension = true, DefaultExt = ".json" }; if (dialog.ShowDialog() != true) return; try { await transfer.ExportAsync(dialog.FileName, new PortableConfigurationBundle { Settings = Settings, Servers = Servers.Select(x => x.Definition).ToList() }); MessageBox.Show("Configuration exported successfully.", "Export complete", MessageBoxButton.OK, MessageBoxImage.Information); } catch (Exception ex) { MessageBox.Show("Configuration could not be exported: " + ex.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error); } }
    async Task ImportConfigurationAsync()
    {
        if (Servers.Any(x => x.CanStop)) { MessageBox.Show("Stop all managed servers before importing configuration.", "Import unavailable", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var dialog = new OpenFileDialog { Title = "Load Messaging Server Manager configuration", Filter = "Messaging Server Manager configuration (*.msmconfig.json)|*.msmconfig.json|JSON files (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var bundle = await transfer.ImportAsync(dialog.FileName);
            var errors = ValidateBundle(bundle);
            if (errors.Count > 0) { MessageBox.Show(string.Join(Environment.NewLine, errors), "Invalid configuration", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (MessageBox.Show($"Replace the current configuration with {bundle.Servers.Count} imported server definition(s)?", "Confirm import", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await PersistImportedConfigurationAsync(bundle);
            CopySettings(bundle.Settings, Settings);
            ApplyMonitoringIntervalImmediately();
            ApplySparklineSettings();
            Servers.Clear();
            foreach (var definition in bundle.Servers) Servers.Add(new ServerRowViewModel(definition));
            Selected = Servers.FirstOrDefault();
            await RefreshAllAsync();
            MessageBox.Show("Configuration loaded successfully.", "Import complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { MessageBox.Show("Configuration could not be loaded: " + ex.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    internal async Task PersistImportedConfigurationAsync(PortableConfigurationBundle bundle)
    {
        var previousSettings = Clone(Settings);
        var previousServers = Servers.Select(row => row.Definition.Clone()).ToList();
        var generatedFiles = CaptureGeneratedConfigurationFiles(bundle.Servers);
        var durableWritesStarted = false;
        try
        {
            foreach (var definition in bundle.Servers.Where(x => x.Location == ServerLocation.Local))
                await adapters[definition.ServerType].PrepareAsync(definition, bundle.Settings, shutdown.Token);
            durableWritesStarted = true;
            await store.SaveAsync("servers.json", new ConfigurationEnvelope<List<ServerDefinition>> { Data = bundle.Servers });
            await store.SaveAsync("settings.json", bundle.Settings);
            await store.SaveAsync("runtime.json", new ConfigurationEnvelope<List<RuntimeProcessState>> { Data = bundle.Servers.Select(x => new RuntimeProcessState { ServerId = x.Id, Executable = x.Executable }).ToList() });
        }
        catch (Exception importFailure)
        {
            Exception? rollbackFailure = null;
            try { RestoreGeneratedConfigurationFiles(generatedFiles); } catch (Exception ex) { rollbackFailure = ex; }
            try
            {
                if (durableWritesStarted)
                {
                    await store.SaveAsync("servers.json", new ConfigurationEnvelope<List<ServerDefinition>> { Data = previousServers });
                    await store.SaveAsync("settings.json", previousSettings);
                }
            }
            catch (Exception ex)
            {
                rollbackFailure = rollbackFailure is null ? ex : new AggregateException(rollbackFailure, ex);
            }
            if (rollbackFailure is not null) throw new AggregateException("Configuration import failed and the previous configuration could not be fully restored.", importFailure, rollbackFailure);
            throw;
        }
    }
    Dictionary<string, byte[]?> CaptureGeneratedConfigurationFiles(IEnumerable<ServerDefinition> definitions)
    {
        var snapshots = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions.Where(x => x.Location == ServerLocation.Local && x.ServerType == ServerType.Nats && x.LaunchMode == LaunchMode.ConfigFile && x.ManageConfigFile && !string.IsNullOrWhiteSpace(x.ConfigFilePath)))
        {
            var path = paths.Resolve(definition.ConfigFilePath!);
            snapshots.TryAdd(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
        }
        return snapshots;
    }
    static void RestoreGeneratedConfigurationFiles(IReadOnlyDictionary<string, byte[]?> snapshots)
    {
        foreach (var (path, content) in snapshots)
        {
            if (content is null) { if (File.Exists(path)) File.Delete(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
    }
    List<string> ValidateBundle(PortableConfigurationBundle bundle) { var errors = ServerValidator.ValidateIdentities(bundle.Servers).ToList(); if (string.IsNullOrWhiteSpace(bundle.Settings.LoggingRootDirectory)) errors.Add("Logging root directory is required."); if (bundle.Settings.MonitoringIntervalSeconds < 1) errors.Add("Monitoring interval must be at least one second."); if (bundle.Settings.MaximumLogLines < 1) errors.Add("Maximum log lines must be at least one."); if (bundle.Settings.MetricSparklineMinutes < 1 || bundle.Settings.MetricSparklineMinutes > 240) errors.Add("Metric sparkline history must be between 1 and 240 minutes."); foreach (var server in bundle.Servers) foreach (var error in ServerValidator.Validate(server, bundle.Servers, paths.Resolve).Errors) errors.Add($"{server.Name}: {error}"); return errors.Distinct().ToList(); }
    static void Copy(ServerDefinition s, ServerDefinition d) { var c = System.Text.Json.JsonSerializer.Deserialize<ServerDefinition>(System.Text.Json.JsonSerializer.Serialize(s))!; foreach (var p in typeof(ServerDefinition).GetProperties().Where(x => x.CanWrite)) p.SetValue(d, p.GetValue(c)); }
    async Task SaveAsync() { foreach (var definition in Servers.Select(x => x.Definition).Where(x => x.Location == ServerLocation.Local)) await adapters[definition.ServerType].PrepareAsync(definition, Settings, shutdown.Token); await store.SaveAsync("servers.json", new ConfigurationEnvelope<List<ServerDefinition>> { Data = Servers.Select(x => x.Definition).ToList() }); }
    Task SaveRuntimeAsync() { var running = processes.GetRuntimeStates().ToDictionary(x => x.ServerId); var states = Servers.Select(row => { var state = running.GetValueOrDefault(row.Definition.Id) ?? new RuntimeProcessState { ServerId = row.Definition.Id, Executable = row.Definition.Executable }; state.LastExitCode = row.LastExitCode; state.LastExitTimeUtc = row.LastExitTime?.ToUniversalTime(); state.RestartCount = row.RestartCount; return state; }).ToList(); return store.SaveAsync("runtime.json", new ConfigurationEnvelope<List<RuntimeProcessState>> { Data = states }); }
    async Task EditSettingsAsync() { var copy = Clone(Settings); var previous = Clone(Settings); var w = new SettingsWindow(copy); if (w.ShowDialog() == true) { CopySettings(copy, Settings); ApplyMonitoringIntervalImmediately(); ApplySparklineSettings(); if (!await SaveWithErrorAsync(() => store.SaveAsync("settings.json", Settings))) { CopySettings(previous, Settings); ApplyMonitoringIntervalImmediately(); ApplySparklineSettings(); } } }
    static void CopySettings(GlobalSettings source, GlobalSettings target) { foreach (var p in typeof(GlobalSettings).GetProperties().Where(x => x.CanWrite)) p.SetValue(target, p.GetValue(source)); }
    static GlobalSettings Clone(GlobalSettings value) => System.Text.Json.JsonSerializer.Deserialize<GlobalSettings>(System.Text.Json.JsonSerializer.Serialize(value))!;
    static async Task<bool> SaveWithErrorAsync(Func<Task> save) { try { await save(); return true; } catch (Exception ex) { MessageBox.Show("Configuration could not be saved: " + ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); return false; } }
    string LogPath(ServerRowViewModel r) { var p = r.Definition.LogFilePath!; return Path.IsPathRooted(Environment.ExpandEnvironmentVariables(p)) ? paths.Resolve(p) : paths.Resolve(Path.Combine(Settings.LoggingRootDirectory, p)); }
    async Task RefreshLogAsync() { if (Selected?.Definition.Location == ServerLocation.Remote) { LogText = "Remote monitoring does not provide access to the server log file."; return; } if (Selected is null || string.IsNullOrWhiteSpace(Selected.Definition.LogFilePath)) { LogText = "No log file is configured."; return; } try { LogText = string.Join(Environment.NewLine, await logs.ReadTailAsync(LogPath(Selected), Settings.MaximumLogLines, shutdown.Token)); } catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { } catch (Exception ex) { LogText = "Unable to read log: " + ex.Message; } }
    void PauseLog() { IsLogPaused = true; RaiseCommands(); }
    void ResumeLog() { IsLogPaused = false; RaiseCommands(); TrackBackground(RefreshLogAsync()); }
    static void OpenPath(string p, bool folder) { var t = folder ? (Directory.Exists(p) ? p : Path.GetDirectoryName(p)!) : p; if (folder) Directory.CreateDirectory(t); try { Process.Start(new ProcessStartInfo(t) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message, "Open failed"); } }
    void OnExited(Guid id, int processId, int? code) => Application.Current.Dispatcher.BeginInvoke(() => TrackBackground(HandleExitedAsync(id, processId, code)));
    async Task HandleExitedAsync(Guid id, int processId, int? code)
    {
        var intentional = intentionalExits.Remove(id);
        var current = processes.Get(id);
        if (current is not null && current.Process.Id != processId) return;
        var r = Servers.FirstOrDefault(x => x.Definition.Id == id);
        if (r is null) return;
        r.LastExitCode = code; r.LastExitTime = DateTime.Now; r.Status = intentional ? ServerStatus.Stopped : ServerStatus.Failed; r.Pid = null;
        if (!intentional) r.LastError = $"Process exited unexpectedly with code {(code?.ToString() ?? "unknown")}.";
        await SaveRuntimeAsync(); RaiseCommands();
        if (!intentional && !shutdown.IsCancellationRequested && r.Definition.Enabled && r.Definition.AutoRestart)
        {
            r.RestartCount++;
            await Task.Delay(1000, shutdown.Token);
            if (!shutdown.IsCancellationRequested && r.Definition.Enabled && Servers.Contains(r) && processes.Get(id) is null) await StartAsync(r);
        }
    }
    void TrackBackground(Task task) { lock (backgroundGate) backgroundTasks.Add(task); _ = task.ContinueWith(completed => { lock (backgroundGate) backgroundTasks.Remove(completed); if (completed.IsFaulted && !shutdown.IsCancellationRequested) AsyncCommandErrors.Report(completed.Exception!.GetBaseException()); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); }
    void RaiseCommands() { if (EditServerCommand is AsyncRelayCommand edit) edit.Raise(); if (DeleteServerCommand is AsyncRelayCommand delete) delete.Raise(); if (ImportConfigurationCommand is AsyncRelayCommand import) import.Raise(); foreach (var c in new[] { StartAllCommand, StopAllCommand, RestartAllCommand, RefreshSelectedTelemetryCommand }) if (c is AsyncRelayCommand bulk) bulk.Raise(); foreach (var c in new[] { OpenLogCommand, OpenLogFolderCommand, PauseLogCommand, ResumeLogCommand, CopyRawTelemetryCommand, OpenRawTelemetryCommand }) if (c is RelayCommand r) r.Raise(); if (StartServerCommand is AsyncRelayCommand<ServerRowViewModel> start) start.Raise(); if (StopServerCommand is AsyncRelayCommand<ServerRowViewModel> stop) stop.Raise(); if (RestartServerCommand is AsyncRelayCommand<ServerRowViewModel> restart) restart.Raise(); if (InspectServerCommand is RelayCommand<ServerRowViewModel> inspect) inspect.Raise(); }
    public void Dispose() { if (disposed) return; disposed = true; relativeTimeTimer.Stop(); Servers.CollectionChanged -= OnServersChanged; foreach (var row in Servers) row.PropertyChanged -= OnServerRowChanged; shutdown.Cancel(); monitoringIntervalChanged.Cancel(); processes.Dispose(); monitoringIntervalChanged.Dispose(); shutdown.Dispose(); }
}
