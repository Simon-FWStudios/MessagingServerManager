# Messaging Server Manager

A Windows desktop dashboard for configuring, starting, stopping, restarting, monitoring, and inspecting local NATS Server and TIBCO Rendezvous daemon processes. The application uses .NET 8, WPF, and MVVM, with no proprietary UI framework.

## Prerequisites and build

- Windows 10/11
- .NET 8 SDK
- NATS Server and/or TIBCO Rendezvous installed separately when you want to launch those products

```powershell
dotnet restore .
dotnet build .
dotnet test .
dotnet run --project src/MessagingServerManager.App
```

Real NATS integration tests are in `tests/MessagingServerManager.Nats.IntegrationTests`. They use `NATS_SERVER_PATH` when set, otherwise they discover an ignored local copy under `tools/nats-server`. Tests are dynamically skipped when no executable is available:

```powershell
$env:NATS_SERVER_PATH = "C:\path\to\nats-server.exe"
dotnet test tests/MessagingServerManager.Nats.IntegrationTests
```

VS Code tasks for restore, build, test, and run are included. C# Dev Kit and C# are recommended.

## Configuration

Persistent data is stored under `%LOCALAPPDATA%\MessagingServerManager`:

- `servers.json`: server definitions
- `settings.json`: global settings
- `runtime.json`: validated runtime process identity, exit history, and restart counters
- `logs`: default log root
- `sample-config`: first-run samples

Files use versioned, indented JSON. Schema v1 files are migrated to the current v2 model when loaded. Saves are serialized per destination, atomic, retain the previous valid file as a `.bak`, and recover from that backup when the primary JSON is invalid. Environment variables are expanded; relative paths resolve against the configuration directory. Generated runtime JSON, logs, credentials, private keys, and product configuration containing secrets should never be committed.

The toolbar **Load** and **Save / Export** actions transfer a portable, versioned JSON bundle containing global settings and server definitions. Exported bundles deliberately exclude live process state and log contents. Import validates the complete bundle, requires all managed servers to be stopped, asks before replacing the current configuration, and persists the imported settings and definitions only after validation succeeds.

## Adding servers

Select **Add Server**, choose the product and launch mode, and complete the common and product-specific fields. NATS supports config-file, managed-options, and custom-arguments modes; telemetry uses `/varz` and readiness uses `/healthz` on the configured HTTP or HTTPS monitoring port. TIBCO RV managed options include listen host, listen port, reliability, HTTP administration host/port, and optional network; when an HTTP administration port is configured, the application parses its Prometheus-format `/metrics` endpoint.

Each definition has a **Local** or **Remote** location. Local servers are owned as processes and expose lifecycle and local-log actions. Remote definitions are monitor-only: NATS reads HTTP or HTTPS `/varz`, while TIBCO RV reads the configured HTTP `/metrics` endpoint and selects the configured service/network label set. PID and local lifecycle/log controls remain unavailable. HTTPS uses the system trust store, or an optional PEM CA certificate configured on the server definition.

For local NATS managed-options mode, enabling TLS switches monitoring from `--http_port` to `--https_port` and emits the NATS `--tls`, `--tlscert`, `--tlskey`, optional `--tlscacert`, and optional `--tlsverify` arguments. Config-file mode can either preserve an externally maintained file or create/update a managed file from the editor settings. New definitions default to managed-file ownership when config-file mode is selected. Generated files use an absolute `log_file`, create its parent directory, and include `max_payload: 67108864` (64 MiB) by default. Externally maintained configs are never rewritten; relative `log_file` entries resolve against the config directory and their parent directories are created before launch. Certificate and private-key files can contain sensitive material and must not be committed.

For a local TLS test environment on client port 4223 and HTTPS monitoring port
8223, run `scripts\generate-nats-test-certificates.bat` and follow
[`docs/NATS-TLS-TESTING.md`](docs/NATS-TLS-TESTING.md) for the exact server and
client certificate mapping.

`nats-server.exe` and `rvdaemon.exe` may be resolved through `PATH`, or set an explicit executable path. Managed TIBCO RV startup follows `rvdaemon.exe -listen localhost:7500 -reliability 60 -http localhost:7580`, with each value configurable; the generated first-run sample is `sample-config/rvdaemon.args.txt`. Exact RV flags can vary by installed TIBCO version; verify the sample arguments against your locally installed documentation.

## Monitoring and logs

The monitor refreshes every three seconds by default, sampling process identity, PID, start time, uptime, working set, CPU delta, health, exit code, errors, and last-check time. Telemetry availability is independent from process or client-port reachability: a live local process remains Running when metrics are blocked, and a remote server remains Running when its independently configured port is reachable. Retained telemetry is explicitly marked stale and rate calculations restart after recovery. NATS `/varz` is retried once and `/healthz` is treated as independent supporting evidence, so a health-endpoint failure does not discard valid metrics. Changed telemetry failures are written without per-refresh duplication to a size-rotated `logs/monitoring.log`. Stored PIDs are never trusted alone: start time and executable identity must also match. Network and process checks are asynchronous.

The contextual action on each row opens the **Server Metrics** tab when telemetry is configured. It presents a formatted summary and a raw `/varz` JSON or TIBCO Prometheus view, with refresh, copy, and open-endpoint actions. Local log-file and folder actions remain in Server Details.

The Live Log tab reads only a bounded tail with shared file access, displays a useful missing-file message, refreshes as the file grows or is recreated, and supports pause/resume, clear, external open, and containing-folder open.

## Security and limitations

The first version manages local processes only. Elevated processes may require running the application as administrator. NATS and TIBCO RV remain separately installed dependencies; no TIBCO libraries are required to compile. Graceful console shutdown is product/host dependent—particularly on Windows—so the configured timeout can fall back to terminating the process tree. Protect product configuration files because they may contain credentials.

Endpoint authentication and client-certificate selection are intentionally deferred until the expected corporate certificate/trust model is confirmed. Current roadmap items include remote host agents, richer per-product graceful shutdown, Windows service integration, credential storage, log rotation notifications, and historical metrics.

## Packaging and CI

`.github/workflows/build.yml` builds and tests the solution on Windows and uploads a self-contained `win-x64` artifact. To publish locally:

```powershell
dotnet publish src/MessagingServerManager.App -c Release -r win-x64 --self-contained true -p:PublishProfile=win-x64
```

The generated executable is versioned but unsigned. Code signing should be added once an organizational signing certificate and release policy are available.
