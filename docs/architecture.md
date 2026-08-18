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

Node.js uses the signed official checksum manifest. Temurin uses Adoptium hashes and signatures. Python uses the official python.org catalog and Install Manager. A user-installed Python Install Manager is preserved; when absent, SoftPilot verifies, temporarily registers, and then removes the official package.

## Download sources

Node.js and Temurin probe the built-in official and TUNA archive sources concurrently, reading at most 64 KiB with a four-second timeout per source. Network failures can fall back; integrity failures abort without fallback. Catalogs, integrity data, and Python remain official-only, and custom sources are not accepted.

Module visibility and order update immediately and are saved serially without a separate action.

## Terminal default

Selecting the first terminal-default runtime snapshots user `PATH` and `JAVA_HOME`, then configures SoftPilot shims, Node.js `current`, and Java `JAVA_HOME`; `PYTHONHOME` is never set. Clearing the last selection restores the snapshot. Switching replaces only `current\<kind>`, verifies the actual version, and rolls back on failure. Changes apply to newly opened terminals.
