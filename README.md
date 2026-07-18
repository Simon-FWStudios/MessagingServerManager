# Messaging Server Manager

Messaging Server Manager is a Windows desktop dashboard for configuring, starting, stopping, restarting, monitoring, and inspecting local and remote NATS Server and TIBCO Rendezvous daemon instances.

The application is built with .NET 8 and WPF. It does not bundle NATS Server or TIBCO Rendezvous; those product binaries remain separately installed dependencies.

## What the application does

- Starts, stops, and restarts local NATS and TIBCO RV daemon processes.
- Monitors local and remote NATS instances through `/varz` and `/healthz`.
- Monitors TIBCO RV daemon metrics through its HTTP `/metrics` endpoint when available.
- Supports local NATS TLS startup and HTTPS monitoring.
- Generates and maintains managed NATS configuration files when requested.
- Shows process state, ports, uptime, CPU, memory, connection counts, message rates, byte rates, raw telemetry, logs, and event history.
- Imports and exports portable application configuration bundles.
- Copies or exports Markdown diagnostics for a selected server.
- Publishes locally as a self-contained Windows executable.

## Requirements

- Windows 10 or Windows 11.
- .NET 8 SDK for development and local builds.
- `nats-server.exe` available through `PATH` or configured with an explicit path when launching local NATS servers.
- `rvdaemon.exe` available through `PATH` or configured with an explicit path when launching local TIBCO RV daemons.
- OpenSSL if you want to run the bundled local TLS certificate generation script.

Remote monitoring does not require local product binaries because remote definitions are monitor-only.

## Build, test, and run

From the repository root:

```powershell
dotnet restore .
dotnet build .
dotnet test .
dotnet run --project src/MessagingServerManager.App
```

Real NATS integration tests are in `tests/MessagingServerManager.Nats.IntegrationTests`. They use `NATS_SERVER_PATH` when set; otherwise they try to discover an ignored local copy under `tools/nats-server`.

```powershell
$env:NATS_SERVER_PATH = "C:\path\to\nats-server.exe"
dotnet test tests/MessagingServerManager.Nats.IntegrationTests
```

Some TLS inspection environments can interfere with certificate-chain tests. The local test suite has an explicit opt-out flag for those machine-specific checks:

```powershell
$env:MSM_ALLOW_TLS_INSPECTION_TEST_BYPASS = "1"
dotnet test .
```

## Local self-contained publish

To create a local self-contained Windows x64 publish folder:

```powershell
dotnet publish src\MessagingServerManager.App\MessagingServerManager.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishReadyToRun=false
```

To create a single-file self-contained executable:

```powershell
dotnet publish src\MessagingServerManager.App\MessagingServerManager.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=false `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o .\publish\win-x64-single
```

The generated `MessagingServerManager.exe` includes the .NET runtime. Target machines still need any external server binaries you want the app to start, such as `nats-server.exe` or `rvdaemon.exe`.

The executable is currently unsigned. Add code signing before using it in a managed production estate.

## First launch and stored data

Application data is stored under:

```text
%LOCALAPPDATA%\MessagingServerManager
```

Important files and folders:

- `servers.json` — configured server definitions.
- `settings.json` — global application settings.
- `runtime.json` — recovery information for owned local processes.
- `history.json` — recent application/server event history.
- `logs\` — default log root.
- `servers\` — generated managed server configuration files.
- `data\` — default managed data/store folders.

Relative paths in configuration are resolved under `%LOCALAPPDATA%\MessagingServerManager` unless otherwise documented. Environment variables are expanded.

Saves are written atomically and previous valid JSON files are retained as `.bak` backups. Runtime cache corruption is quarantined and treated as disposable recovery state; primary configuration corruption requires user recovery rather than silently replacing the data with defaults.

Do not commit generated runtime state, logs, credentials, private keys, or product configuration files containing secrets.

## Main window overview

The application is split into five main areas:

1. Header and toolbar.
2. Server list.
3. Metric cards.
4. Selected server details.
5. Lower tabbed workspace.

### Header and toolbar

The header shows the application name and a compact count of running, stopped, disabled, and total configured servers.

Toolbar actions:

- Start all — starts all enabled local servers that are currently startable.
- Stop all — stops all running local servers after confirmation.
- Restart all — restarts all running local servers after confirmation.
- Add server — opens the server configuration editor.
- Edit — edits the selected server when it is not running.
- Delete — removes the selected server when it is not running.
- Load — imports a portable configuration bundle.
- Save / Export — exports a portable configuration bundle.
- Refresh — manually refreshes monitoring and logs.
- Open folder — opens the application data folder.
- Settings — opens global application settings.

Remote servers are monitor-only, so start/stop/restart actions are not shown for them.

### Server list

The server list is the primary monitoring table. Rows can be sorted by column.

Columns:

- Status — coloured state dot with tooltip.
  - Green: running.
  - Red: failed or unreachable.
  - Grey: stopped or disabled.
  - Amber/blue states may be used for transitional or degraded states.
- Name — configured display name.
- Type — NATS or TIBCO RV.
- Location — Local or Remote.
- PID — process ID for owned or recovered local processes.
- Port(s) — client and monitoring/admin ports.
- Uptime — process or telemetry uptime where available.
- Connections — current/maximum connection count where telemetry exposes it.
- Message Rate — compact inbound and outbound message rate, for example `In 10/s • Out 8/s`.
- Actions — state-driven row actions.

Row actions:

- Start — available for stopped, enabled local servers.
- Stop — available for running local servers.
- Restart — available for running local servers.
- Inspect telemetry — opens the metrics workspace for telemetry-enabled rows.

For remote rows, lifecycle controls are intentionally unavailable. If metrics or the monitoring port cannot be reached, the row is marked unreachable/stale where appropriate rather than pretending the server is healthy.

### Metric cards

The four cards below the server list provide a quick glance for the selected server:

- Connections — current and maximum connection count.
- Message Rate — inbound/outbound messages per second.
- CPU — current approximate CPU usage for local processes or telemetry-derived CPU where available.
- Memory — current memory usage for local processes or telemetry-derived memory where available.

When metric sparklines are enabled, each card shows a small recent-history line. The line is intentionally compact: it is for spotting movement and spikes, not deep analysis. Detailed charts are in the Server Metrics tab.

### Selected server details panel

The selected server details panel is the collapsible card between the metric cards and the lower tabs. Collapse it when you want more room for logs or charts.

Expanded details show:

- Server Details:
  - executable or remote URL
  - working directory
  - launch mode
  - config file
  - arguments
- Process Statistics:
  - status
  - PID
  - uptime
  - CPU
  - memory
- Health and Exit:
  - health summary
  - telemetry freshness
  - last exit code
  - last error summary

Buttons in this panel:

- Open log file — opens the selected server log file when available.
- Open log folder — opens the containing log folder.
- Copy diagnostics — copies Markdown diagnostics for the selected server to the clipboard.
- Export diagnostics — saves Markdown diagnostics for the selected server to a file.

Long errors are not expanded inline in this panel because they can distort the layout. Detailed errors are written to the Live Log and History tab.

## Lower tabs

### Live Log

The Live Log tab shows a bounded tail of the selected local server log file.

It supports:

- auto-scroll
- pause/resume
- clear
- open log file
- open log folder

The log reader tails from the end of the file rather than scanning the entire file every refresh. It handles missing files, file growth, truncation, and recreation. Application-level startup, stop, restart, and telemetry failures are also written into the live view as `[APP]` notices.

Remote rows do not have local log files. Use Server Metrics, Raw telemetry, History, or Diagnostics for remote investigation.

### Server Metrics

The Server Metrics tab is the main telemetry view for NATS `/varz` or TIBCO RV `/metrics`.

Toolbar buttons in this tab:

- Refresh telemetry — manually refreshes telemetry for the selected server.
- Copy raw telemetry — copies the raw telemetry payload.
- Open telemetry endpoint — opens the configured metrics endpoint in the browser.

Internal tabs:

#### Summary

Shows a formatted text summary of the most useful telemetry fields.

For NATS this includes:

- server identity
- version
- cluster/tags/metadata when available
- endpoint
- health endpoint status
- connections
- total connections
- subscriptions
- CPU
- memory
- inbound/outbound message rate
- inbound/outbound byte rate
- lifetime message counts
- lifetime byte counts
- routes/remotes/leaf nodes
- slow consumers

For TIBCO RV this includes parsed values from the Prometheus-style `/metrics` page where present:

- component/version/host
- service/network labels
- client connections
- subscriptions
- uptime
- message and byte rates
- lifetime message and byte counters
- delivery warning counters such as data loss, retransmits, and missed packets

If a metric is unavailable from the product endpoint, it is shown as zero or `—` rather than inferred.

#### Activity

Shows larger recent-history charts for the selected server.

The Activity tab is intended for spotting spikes and shape over the configured telemetry window. It contains:

- Message Rate — separate inbound and outbound lines.
- Data Rate — separate inbound and outbound byte-rate lines.
- Connections — supporting context chart.
- CPU — supporting context chart.
- Memory — supporting context chart.

The colour scheme is consistent:

- Green: inbound.
- Blue: outbound.

The value scale below each chart shows the min-to-max range for the retained history window. Tooltips show current, peak, average, minimum, and sample count.

Sparkline collection can be enabled/disabled and the history window adjusted in Settings.

#### Raw

Shows the raw telemetry payload:

- NATS: raw `/varz` JSON.
- TIBCO RV: raw Prometheus-style `/metrics` text.

Use this when troubleshooting parser coverage or product-specific telemetry fields.

### Process Details

The Process Details tab shows secondary runtime information:

- process start time
- last checked time
- last exit time
- last exit code
- restart count
- effective command-line arguments or remote telemetry endpoint

This tab is useful when validating exactly how a local server will be launched.

### Configuration Summary

The Configuration Summary tab shows a read-only text view of the selected server definition:

- name
- type
- location
- enabled flag
- launch mode
- executable or endpoint
- working directory
- config file
- log file
- ports
- TLS state
- auto-start
- auto-restart

Use this to quickly verify configuration without opening the editor.

### History

The History tab shows recent server and application events:

- starts
- stops
- restarts
- add/edit/delete actions
- settings changes
- import/export activity
- telemetry failures
- process failures
- diagnostics copy/export actions

History is persisted to `history.json` and capped to a bounded recent set so it remains lightweight.

## Adding and editing servers

Click Add server to create a definition, or select a stopped server and click Edit.

Common fields include:

- Name.
- Enabled.
- Server type.
- Location: Local or Remote.
- Executable or remote host/URL.
- Working directory.
- Launch mode.
- Log file.
- Auto-start.
- Auto-restart.
- Graceful stop timeout.
- Force-kill-after-timeout policy.

Edit and delete are disabled for running local servers. Stop the server first before changing or removing its configuration.

## Local versus remote servers

### Local

Local definitions are processes owned by the manager. The app can:

- start them
- stop them
- restart them
- read process PID/start time/CPU/memory
- recover previously owned processes after app restart using validated runtime identity
- open local logs and folders
- monitor product telemetry if the monitoring/admin endpoint is configured and reachable

The app does not automatically adopt arbitrary processes just because they happen to use the configured ports. Recovery requires persisted runtime evidence and process identity checks.

### Remote

Remote definitions are monitor-only. The app can:

- query NATS `/varz`
- query NATS `/healthz` as supporting evidence
- query TIBCO RV `/metrics`
- show telemetry and raw payloads
- mark telemetry stale or unreachable when blocked

The app cannot:

- start remote servers
- stop remote servers
- restart remote servers
- show a remote PID unless exposed through telemetry
- open remote local log files

## NATS configuration

NATS supports:

- Configuration file mode.
- Managed options mode.
- Custom arguments mode.

For local NATS config-file mode, the app can manage the configuration file for you. Managed config files are created under the application data `servers\` folder by default. The generated config includes:

- server name
- client port
- monitoring or HTTPS monitoring port
- absolute `log_file`
- store directory when configured
- TLS options when enabled
- `max_payload: 67108864` by default, equivalent to 64 MiB

If you choose to use an externally maintained config file, the app will not rewrite it. It will still ensure required log directories exist before launch where possible.

`nats-server.exe` can be configured as either:

- a full path, for example `C:\Tools\nats-server\nats-server.exe`
- a bare executable name, for example `nats-server.exe`, resolved through process/user/machine `PATH`

When a bare executable is used and no working directory is configured, the app resolves the executable from `PATH` and uses the executable’s folder as the process working directory. This avoids accidental startup under temporary or config folders.

## NATS TLS testing

For a local TLS test environment using client port `4223` and HTTPS monitoring port `8223`, run:

```powershell
scripts\generate-nats-test-certificates.bat
```

The generated files are written under `test-certificates\`.

See [docs/NATS-TLS-TESTING.md](docs/NATS-TLS-TESTING.md) for the certificate mapping, including which certificate is used by:

- NATS server TLS.
- NATS monitoring HTTPS.
- NATS client connection tests.
- Remote HTTPS monitoring.

TLS certificate and private-key files can contain sensitive material and must not be committed in real environments.

## TIBCO Rendezvous configuration

For a local RV daemon, managed startup follows this shape:

```powershell
rvdaemon.exe -listen localhost:7500 -reliability 60 -http localhost:7580
```

The app allows these values to be configured:

- listen host
- listen port
- reliability
- HTTP administration host
- HTTP administration port
- optional network

The app does not pass or store `reuse-port` because it is not required for the managed scenario.

If the HTTP administration endpoint is configured, the app queries:

```text
http://host:port/metrics
```

The TIBCO RV metrics endpoint is Prometheus-style text rather than JSON. The app parses known metric names and preserves label sets so it can select the relevant configured service/network where available.

Depending on RV version and runtime activity, some `/metrics` pages may initially show metric comments or headings before useful values appear. The app only shows values actually present in the metrics payload.

## Settings

Global settings include:

- monitoring interval
- default graceful stop timeout
- default force-kill policy
- auto-start behavior
- log root directory
- metric sparkline enable/disable
- metric sparkline history period
- stop/restart confirmation behavior

Monitoring interval changes apply immediately without requiring an application restart.

## Configuration import and export

Use Load to import a portable configuration bundle.

Use Save / Export to write one.

Bundles include:

- global settings
- server definitions

Bundles do not include:

- runtime process ownership
- logs
- telemetry history
- event history
- certificate/private-key contents
- generated data directories

Import is intentionally conservative. It validates the full bundle and requires local managed servers to be stopped before replacing the live configuration.

## Diagnostics

The selected server details panel can copy or export Markdown diagnostics.

Diagnostics include:

- generated timestamp
- selected server identity
- health and telemetry freshness
- process/configuration summary
- effective arguments or endpoint
- formatted telemetry summary
- raw telemetry payload
- recent relevant history
- recent local log tail when available

Diagnostics intentionally avoid embedding certificate/private-key file contents.

Markdown is used because it is readable as plain text, easy to attach to tickets, easy to diff, and still renders nicely in GitHub/Azure DevOps/Teams/Slack.

## Startup splash and icon

The app uses `Assets\AppIcon.ico` as the executable and window icon. The source PNG has a transparent outer background so it does not appear as a white square on the Windows taskbar.

The startup splash uses `Assets\Splash.png` with a small overlay showing:

- app name
- tagline
- version
- current startup step

Detailed startup errors are shown in a message box and then recorded in normal logs/history where possible once the main UI is available.

## Security notes

- Product configs may contain credentials; protect them.
- TLS private keys must not be committed.
- Remote HTTPS monitoring uses normal system trust unless a custom CA is configured.
- A configured custom CA must still validate a proper certificate chain and hostname.
- Endpoint authentication is intentionally deferred until the expected corporate certificate/trust model is confirmed.
- Stopping elevated processes may require running the app as administrator.

## Known limitations and roadmap

Current limitations:

- Remote servers are monitor-only.
- No Windows Service installation/control yet.
- No credential vault integration yet.
- No remote host agent yet.
- TIBCO RV graceful shutdown behavior can vary by installed version.
- Code signing is not yet configured.

Potential future improvements:

- Windows service management.
- Remote management agent.
- Credential/keyring integration once the corporate model is confirmed.
- Richer alert thresholds.
- Exportable historical metrics.
- Installer/MSIX packaging.
