# Messaging Server Manager

NATS and TIBCO RV monitoring for Windows.

Version: {{Version}}

## Overview

Messaging Server Manager helps operators and developers run and inspect local messaging daemons while also monitoring remote telemetry endpoints.

It supports local lifecycle control, remote telemetry views, live logs, historical events, Markdown diagnostics, TLS-enabled NATS monitoring, and portable configuration import/export.

## Current highlights

- Local NATS and TIBCO RV process start, stop, and restart.
- Remote NATS `/varz` and TIBCO RV `/metrics` monitoring.
- HTTPS monitoring support for NATS TLS configurations.
- Configurable metric cards and activity charts.
- Live log tailing with pause, clear, open file, and open folder actions.
- Server history/event audit tab.
- Markdown diagnostics copy/export.
- Self-contained Windows executable publishing.

## Version history

### {{Version}} — Current development build

- Added branded application icon and splash screen.
- Added telemetry Activity charts with consistent inbound/outbound colour semantics.
- Added diagnostics copy/export support.
- Added event history tab.
- Improved server details layout, toolbar glyphs, row actions, and metric cards.
- Hardened local executable resolution, generated NATS config paths, and startup logging.

## Notes

NATS Server and TIBCO Rendezvous are not bundled. Configure their executable paths explicitly or ensure they are available through `PATH`.

Remote definitions are monitor-only. Lifecycle buttons are available only for local managed processes.
