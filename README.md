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

Files use versioned, indented JSON. Saves are atomic, retain the previous valid file as a `.bak`, and recover from that backup when the primary JSON is invalid. Environment variables are expanded; relative paths resolve against the configuration directory. Generated runtime JSON, logs, credentials, private keys, and product configuration containing secrets should never be committed.

The toolbar **Load** and **Save / Export** actions transfer a portable, versioned JSON bundle containing global settings and server definitions. Exported bundles deliberately exclude live process state and log contents. Import validates the complete bundle, requires all managed servers to be stopped, asks before replacing the current configuration, and persists the imported settings and definitions only after validation succeeds.

## Adding servers

Select **Add Server**, choose the product and launch mode, and complete the common and product-specific fields. NATS supports config-file, managed-options, and custom-arguments modes; monitoring uses `/varz` on the configured HTTP monitoring port, falling back to the client TCP port. TIBCO RV managed options include service, network, daemon address, HTTP administration port, and listen port; health uses the optional administration/TCP port or process existence.

Each definition has a **Local** or **Remote** location. Local servers are owned as processes and expose lifecycle and local-log actions. Remote NATS definitions are monitor-only: the application reads HTTP or HTTPS `/varz` telemetry for port, uptime, CPU, memory, connections, subscriptions, traffic counters, and slow consumers; PID and local lifecycle/log controls remain unavailable. HTTPS uses the system trust store, or an optional PEM CA certificate configured on the server definition.

For local NATS managed-options mode, enabling TLS switches monitoring from `--http_port` to `--https_port` and emits the NATS `--tls`, `--tlscert`, `--tlskey`, optional `--tlscacert`, and optional `--tlsverify` arguments. Config-file mode expects equivalent TLS settings in the selected NATS configuration file. Certificate and private-key files can contain sensitive material and must not be committed.

`nats-server.exe` and `rvdaemon.exe` may be resolved through `PATH`, or set an explicit executable path. Exact RV flags vary by installed TIBCO version; verify the sample arguments against your locally installed documentation.

## Monitoring and logs

The monitor refreshes every three seconds by default, sampling process identity, PID, start time, uptime, working set, CPU delta, health, exit code, errors, and last-check time. Health failures transition from Starting to Failed after the configured grace period. Stored PIDs are never trusted alone: start time and executable identity must also match. Network and process checks are asynchronous.

The Live Log tab reads only a bounded tail with shared file access, displays a useful missing-file message, refreshes as the file grows or is recreated, and supports pause/resume, clear, external open, and containing-folder open.

## Security and limitations

The first version manages local processes only. Elevated processes may require running the application as administrator. NATS and TIBCO RV remain separately installed dependencies; no TIBCO libraries are required to compile. Graceful console shutdown is product/host dependent—particularly on Windows—so the configured timeout can fall back to terminating the process tree. Protect product configuration files because they may contain credentials.

Current roadmap items include remote host agents, richer per-product graceful shutdown, Windows service integration, credential storage, log rotation notifications, and historical metrics.
