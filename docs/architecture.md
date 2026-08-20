# SoftPilot V1 architecture

**English** | [简体中文](architecture.zh-CN.md)

## Components

- `Domain`: runtime and operation models.
- `Application`: use cases and infrastructure abstractions.
- `Infrastructure`: providers, SQLite, downloads, Windows integration, and transactions.
- `Gui`: WinUI UI, first-launch setup, self-migration, and embedded-tool deployment.
- `Cli` and `Shim`: command-line management and forwarding to terminal-default runtimes.

Dependencies point toward `Application` and `Domain`; neither depends on an entry point or Windows implementation.

## Workspace

```text
<SoftPilotRoot>\
├─ SoftPilot.exe
└─ SoftPilotData\
   ├─ app\<kind>\<version>\
   ├─ current\<kind>
   ├─ tools\shims\
   ├─ data\
   │  └─ redis\<version>\redis.conf and database files
   ├─ cache\downloads\
   ├─ staging\
   └─ logs\
```

The root EXE is independently replaceable. Managed runtimes, tools, and user data stay under `SoftPilotData`; IDEs can reference concrete versions under `app`. The root is stored in `HKCU\Software\SoftPilot\Root`.

## Lifecycle

- First launch validates the selected local NTFS path, copies and hashes the EXE, atomically replaces the target, restarts there, and removes only the verified source EXE.
- Startup verifies and atomically deploys embedded CLI and shim files when their manifest changes.
- Runtime installation follows `cache → staging → health check → app → SQLite`. Any official metadata, TLS, hash, signature, or health-check failure aborts the transaction.
- Uninstallation moves the runtime to staging first, deletes its state, then removes its files. Failures restore both directory and state.
- GUI and CLI mutations share a cross-process workspace lock.

Node.js uses the signed official checksum manifest. Temurin uses Adoptium hashes and signatures. Python uses the official python.org catalog and Install Manager. Redis versions must exist in the official Redis release catalog; Windows archives come from `redis-windows/redis-windows` GitHub Releases and require the GitHub asset SHA-256 digest. A user-installed Python Install Manager is preserved; when absent, SoftPilot verifies, temporarily registers, and then removes the official package.

## Download sources

Node.js and Temurin probe the built-in official and TUNA archive sources concurrently, reading at most 64 KiB with a four-second timeout per source. Network failures can fall back; integrity failures abort without fallback. Python remains official-only. Redis has one fixed community Windows archive source, cross-checks each version against official Redis metadata, and does not accept custom sources.

## Redis service

Redis is managed as one local foreground process that survives GUI or CLI exit. Starting a version creates version-specific configuration, data, and log paths, then requires a successful `redis-cli PING`, the expected Redis version, and a Windows TCP listener PID matching the process SoftPilot started. This avoids comparing the MSYS2 POSIX PID reported by Redis with the Windows PID. Stopping sends `SHUTDOWN` only when the listener PID matches; fallback termination is allowed only after PID, executable path, and process start time all match the saved SoftPilot state. SoftPilot does not install a Windows Service or configure automatic startup. Redis data and logs are preserved by default when a runtime version is uninstalled; the GUI confirmation and CLI `--delete-data` option can explicitly include them in the same rollback-capable uninstall transaction.

Module visibility and order update immediately and are saved serially without a separate action.

## Terminal default

Selecting the first terminal-default runtime snapshots user `PATH` and `JAVA_HOME`, then configures SoftPilot shims, Node.js `current`, and Java `JAVA_HOME`; `PYTHONHOME` is never set. Redis uses `redis-server` and `redis-cli` shims through `current\redis`; selecting a version does not start the service. Clearing the last selection restores the snapshot. Switching replaces only `current\<kind>`, verifies the actual version, and rolls back on failure. Changes apply to newly opened terminals.
