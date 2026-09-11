# HiveMQ and Modbus Sensor Setup

## What is implemented

`Modbus Simulator (slave) → ModbusMqttBridge (master/publisher) → HiveMQ Docker → MQTT sensors → SQLite / charts / alerts`.

The bridge reads one unsigned 16-bit holding register using function 03. The application subscribes to MQTT; it does not directly poll Modbus. Multi-register floats, signed registers, RTU, and multiple mappings per bridge process remain future extensions. Run separate bridge instances with unique client/device IDs and topics for additional single-register sources.

## Start HiveMQ

Requirements: Docker with Linux containers, Windows for the UI, and .NET 10 SDK. From the repository root:

```powershell
docker compose -f infra/hivemq/compose.yaml up -d
docker compose -f infra/hivemq/compose.yaml logs --tail 30
```

The Compose file pins HiveMQ CE 2026.5 by digest, binds MQTT to `127.0.0.1:1883`, and stores broker data in the named `websocket-telemetry_hivemq-data` volume. Wait for the TCP listener startup log. A publish/subscribe integration check below verifies readiness beyond an open port. `infra/hivemq/.env.example` documents the configurable host port; copy it to `.env` in that directory if needed and use the same port in both clients.

This is a local development broker using the image's allow-all extension. No credentials or TLS are configured on it. Remote deployments require a HiveMQ security extension and TLS configuration; the UI/bridge TLS option alone does not configure the broker. The UI is a Windows application, not HiveMQ's Enterprise Control Center. MQTT-over-WebSocket port 8000 is not published.

Stop without deleting stored broker data:

```powershell
docker compose -f infra/hivemq/compose.yaml stop
```

## Configure the Modbus Slave

The exact third-party simulator distribution/version has not been identified or installed by this change. Use the detailed **Modbus Slave Setup Record** in [PLAN.md](../PLAN.md) to record actual settings once available.

1. Select **slave/server**, **Modbus TCP**, loopback host, port `502`, unit ID `1`.
2. Allocate holding register reference `40001`: **wire address `0`**, quantity `1`. Check whether the simulator UI accepts `0`, `1`, or `40001` for its display convention.
3. Select unsigned 16-bit decimal representation and enter raw value `10132`.
4. Start the listener. If using port `1502`, change `modbusPort` in a copy of the bridge configuration.
5. Save/export the simulator configuration and record its actual path/version in PLAN.md.

The Windows-host bridge uses `127.0.0.1` for both services. A containerized bridge needs a reachable Windows host address for Modbus and the Compose service name for MQTT; container loopback is a different host.

## Start the Bridge and Monitor

```powershell
dotnet build BinanceStream.slnx -c Release
dotnet run --project src/ModbusMqttBridge -c Release -- samples/telemetry/modbus-map.json
```

The sample configuration polls address `0` once per second, with a 2-second read deadline. Conversion is `raw × 0.1 + 0`; `10132` becomes `1013.2 hPa`. It publishes JSON to `emulator/sensor-01/telemetry`, QoS 1, retain false. Use Ctrl+C to stop. `maximumReadings` optionally stops after a finite number of successful publications; omitted or zero means continuous operation.

Start the UI in another terminal:

```powershell
dotnet run --project src/BinanceMonitor -c Release
```

Click **MQTT sensors**. An active Binance session is stopped before the sensor window opens. Use host `127.0.0.1`, port `1883`, topic `emulator/+/telemetry`, source `hivemq-local`, and TLS unchecked for this local broker. Click **Connect** and select the arriving series.

**Replay pressure** works without Docker or a simulator. It sends six fixtures through the same storage/display path with new IDs and current acquisition timestamps. Replay source is `replay`, so its rules and history are separate from live MQTT. The flat-pressure checkbox is only for publishers sending `{"pressure":1013.2}`; the supplied bridge uses schema version 1 and does not need it.

**Save profile** persists settings in `%LOCALAPPDATA%\BinanceMonitor\mqtt-profile.json`. Passwords are encrypted with Windows DPAPI for the current user. Profiles include a stable subscriber client ID; avoid running two instances with the same profile on the same broker. For the bridge, optional `username` and `tls` are configuration fields and the password is read from `MQTT_PASSWORD`; never commit a password.

## Charts, Rules, History, and Storage

- Charts retain 300 points per series, up to 100 series. The recent grid retains 500 rows and its display queue 1,000 entries. Display skips do not discard already committed readings. Samples are evenly spaced by arrival order, with event-time labels.
- Latest values become stale after five seconds without a displayed live reading. Retained snapshots are labeled stale/snapshot and do not trigger live alerts.
- In **Alerts**, save a rule for the selected live series, then refresh to inspect current rule states and the latest 500 transitions. Low/high comparisons are strict; hysteresis controls recovery. Rules apply to source/device/metric/unit together. Saving a rule increments its version and resets it to Normal. Disabling preserves its record and historical versions.
- Acknowledgement does not clear an active excursion. Cooldown marks repeated transitions as `Notify=false` while retaining the event history; there are no modal or external notifications.
- History filters use local UI dates converted to UTC, with an exclusive end time. Results page forward by ingestion ID, 200 rows per page; its chart shows the first series in that page. Filter source/device/metric/unit to isolate a series.
- CSV exports **all matching rows**, not just the current page, with a row-ID cutoff excluding newer inserts. Numbers use invariant formatting; timestamps are UTC; text is escaped and protected against spreadsheet formula interpretation. Cancel removes the temporary export.
- SQLite lives at `%LOCALAPPDATA%\BinanceMonitor\telemetry.db`. Use **Storage → Back up database** for an online backup. To restore, stop the app and replace the database from a verified backup, handling old WAL/SHM files only after all database connections are closed. Prefer restoring to a new empty directory and retaining the original files until validated.
- Retention is manual and confirmed in the Storage tab. It deletes readings in batches while preserving rule/alert history. Deleted space may be reused by SQLite; deletion need not shrink the file. There is no automatic scheduled purge.

## Delivery Guarantees and Limits

The subscriber uses MQTT 3.1.1 with a persistent session and QoS 1 subscription. It acknowledges after the reading and alert checkpoint commit in one SQLite transaction. Stable publisher IDs deduplicate within source/device/metric. Malformed/oversized payloads are counted and intentionally acknowledged. Storage failures stop ingestion instead of acknowledging uncommitted readings. Current transactions process one reading at a time; no bulk-write optimization or long-duration throughput guarantee is claimed.

Persistent sessions depend on client ID, broker configuration, storage, and publisher QoS. QoS 0 and absent publisher IDs do not offer the same delivery/deduplication guarantees. Retention removes old deduplication records, so sufficiently old redeliveries can reappear after deletion. The bridge retains one unconfirmed publication in memory, reusing its ID for retries; it pauses polling while that publication is pending. There is **no durable bridge spool**, and register changes between polls are not recorded. A bridge crash can lose the pending sample. IDs include a new run UUID to avoid reuse after restart.

Only readings within 30 seconds behind / 5 seconds ahead of receipt time can evaluate live alerts. Per-rule event timestamps must advance. Invalid or timed-out Modbus responses never become synthetic zeros. TLS validates certificates normally; custom trust bypasses are not provided.

## Verification

```powershell
dotnet run --project tests/BinanceStream.Tests -c Release
dotnet run --project tests/Telemetry.Tests -c Release
dotnet run --project tests/BinanceMonitor.Tests -c Release
dotnet run --project tests/Telemetry.Tests -c Release -- --broker 127.0.0.1 1883 --bridge src/ModbusMqttBridge/bin/Release/net10.0/ModbusMqttBridge.dll
```

The UI test briefly opens a test window, uses an isolated temporary database, and saves screenshots under the printed artifact directory. The bridge integration test uses a temporary Modbus TCP protocol fixture, not the uninstalled third-party simulator.

Optional, disruptive only to the specified Compose broker: append `--restart-broker infra/hivemq/compose.yaml` to the integration command to verify retained data survives a HiveMQ container restart. Do not run it against a broker serving unrelated work. Build the solution first so the bridge DLL exists. If `dotnet` is not on PATH, use the local SDK path in README.

Actual simulator compatibility, a 30-minute throughput soak, disk-full/locked-volume experiments, and TLS/authentication against a secured broker remain separate acceptance checks in PLAN.md.
