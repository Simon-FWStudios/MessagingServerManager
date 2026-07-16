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

VS Code tasks for restore, build, test, and run are included. C# Dev Kit and C# are recommended.

## Configuration

Persistent data is stored under `%LOCALAPPDATA%\MessagingServerManager`:

- `servers.json`: server definitions
- `settings.json`: global settings
- `runtime.json`: reserved runtime process identity state
- `logs`: default log root
- `sample-config`: first-run samples

Files use versioned, indented JSON. Saves are atomic and retain the previous valid file as a `.bak`. Environment variables are expanded; relative paths resolve against the configuration directory. Generated runtime JSON, logs, credentials, private keys, and product configuration containing secrets should never be committed.

## Adding servers

Select **Add Server**, choose the product and launch mode, and complete the common and product-specific fields. NATS supports config-file, managed-options, and custom-arguments modes; monitoring uses `/varz` on the configured HTTP monitoring port, falling back to the client TCP port. TIBCO RV managed options include service, network, daemon address, HTTP administration port, and listen port; health uses the optional administration/TCP port or process existence.

`nats-server.exe` and `rvdaemon.exe` may be resolved through `PATH`, or set an explicit executable path. Exact RV flags vary by installed TIBCO version; verify the sample arguments against your locally installed documentation.

## Monitoring and logs

The monitor refreshes every three seconds by default, sampling process identity, PID, start time, uptime, working set, CPU delta, health, exit code, errors, and last-check time. Stored PIDs are never trusted alone: start time and executable identity must also match. Network and process checks are asynchronous.

The Live Log tab reads only a bounded tail with shared file access, displays a useful missing-file message, refreshes as the file grows or is recreated, and supports pause/resume, clear, external open, and containing-folder open.

## Security and limitations

The first version manages local processes only. Elevated processes may require running the application as administrator. NATS and TIBCO RV remain separately installed dependencies; no TIBCO libraries are required to compile. Graceful console shutdown is product/host dependent, so the configured timeout can fall back to terminating the process tree. Protect product configuration files because they may contain credentials.

Current roadmap items include remote host agents, richer per-product graceful shutdown, Windows service integration, credential storage, log rotation notifications, and historical metrics.
