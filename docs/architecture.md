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
   ├─ app\
   │  ├─ <kind>\<version>\
   │  └─ git\
   ├─ current\<kind>
   ├─ tools\shims\
   ├─ data\
   │  ├─ redis\<version>\redis.conf and database files
   │  └─ mysql\<major.minor>\data, my.ini, DPAPI credentials, and bootstrap marker
   ├─ cache\downloads\
   ├─ staging\
   └─ logs\
```

The root EXE is independently replaceable. Managed runtimes, tools, and user data stay under `SoftPilotData`; IDEs can reference concrete versions under `app`. The root is stored in `HKCU\Software\SoftPilot\Root`.

## Lifecycle

- First launch validates the selected local NTFS path, copies and hashes the EXE, atomically replaces the target, restarts there, and removes only the verified source EXE.
- Startup verifies and atomically deploys embedded CLI and shim files when their manifest changes.
- Runtime installation follows `cache → staging → health check → app → SQLite`. Any official metadata, TLS, hash, signature, or health-check failure aborts the transaction.
- All modules share `cache\downloads`. On every startup, the unified cache service removes files older than 30 days and empty directories; module uninstall operations never clean cache independently. `spt cache clean` remains available for immediate full cleanup.
- Git installation and upgrade follow `official latest release → cache → SHA-256 verification → staging → version health check → app\git`. Upgrade swaps the single managed directory with rollback, and uninstall never touches the unified download cache or another Git installation. The Git page reads and writes global `user.name` and `user.email` through the managed Git only after the user explicitly saves the form. Uninstall preserves global Git configuration, including `user.name` and `user.email`, as well as SSH keys, credentials, and repositories.
- Uninstallation moves the runtime to staging first, deletes its state, then removes its files. Failures restore both directory and state.
- GUI and CLI mutations share a cross-process workspace lock.

Node.js uses the signed official checksum manifest. Temurin uses Adoptium hashes and signatures. Python uses the official python.org catalog and Install Manager. Redis versions must exist in the official Redis release catalog; Windows archives come from `redis-windows/redis-windows` GitHub Releases and require the GitHub asset SHA-256 digest. MySQL accepts only supported Windows x64 ZIP archives from Oracle's `cdn.mysql.com` and verifies each detached `.asc` signature against pinned primary-key fingerprints loaded from `repo.mysql.com`. MySQL installation also checks the x64 v14 Runtime in HKLM. When it is absent or older than `14.29.30157`, SoftPilot downloads only Microsoft's `https://aka.ms/vc14/vc_redist.x64.exe`, requires a valid Authenticode signature from Microsoft Corporation, and installs it through UAC. Git accepts only the latest x64 PortableGit self-extracting archive from the official `git-for-windows/git` release and requires its GitHub asset SHA-256 digest. A user-installed Python Install Manager is preserved; when absent, SoftPilot verifies, temporarily registers, and then removes the official package.

## Download sources

Node.js and Temurin probe the built-in official and TUNA archive sources concurrently, reading at most 64 KiB with a four-second timeout per source. Network failures can fall back; integrity failures abort without fallback. Python remains official-only. Redis has one fixed community Windows archive source, cross-checks each version against official Redis metadata, and does not accept custom sources. The MySQL catalog currently contains 8.4 LTS and the final 5.7.44 compatibility release; it is Oracle-only with no mirror or custom-source option. Git is official-only and has no source or version selector.

## Redis service

Redis is managed as one local foreground process that survives GUI or CLI exit. Starting a version creates version-specific configuration, data, and log paths, then requires a successful `redis-cli PING`, the expected Redis version, and a Windows TCP listener PID matching the process SoftPilot started. This avoids comparing the MSYS2 POSIX PID reported by Redis with the Windows PID. Stopping sends `SHUTDOWN` only when the listener PID matches; fallback termination is allowed only after PID, executable path, and process start time all match the saved SoftPilot state. SoftPilot does not install a Windows Service or configure automatic startup. Redis data and logs are preserved by default when a runtime version is uninstalled; the GUI confirmation and CLI `--delete-data` option can explicitly include them in the same rollback-capable uninstall transaction.

## MySQL service

MySQL isolates data, configuration, DPAPI credentials, logs, process state, and ports by `major.minor` line, allowing 5.7 and 8.4 to run concurrently. 8.4 defaults to port `3306` and 5.7 to `3307`; stopped instances may use another port, but instances cannot share one. SoftPilot registers neither a Windows Service nor automatic startup.

First start creates the offline data directory with `--initialize-insecure`, then sets a random root password through a temporary process with TCP disabled and Windows shared memory enabled. Regular startup requires `SELECT VERSION()` to match the target and the listener PID to belong to the new process. Plaintext passwords exist only briefly in memory and a temporary client file, never in client command-line arguments. Stop prefers `mysqladmin shutdown`; fallback termination still requires the target version's PID, executable path, and start time. Uninstall preserves release-line data unless `--delete-data` explicitly includes data, configuration, credentials, and logs in the rollback-capable transaction.

The Visual C++ Runtime is a shared system component outside MySQL staging rollback and uninstall. If its installer requires a restart, the MySQL installation stops and asks the user to retry afterward.

Module visibility and order update immediately and are saved serially without a separate action.

## Terminal default

Selecting the first terminal-default runtime snapshots user `PATH` and `JAVA_HOME`, then configures SoftPilot shims, Node.js `current`, and Java `JAVA_HOME`; `PYTHONHOME` is never set. Redis uses `redis-server` and `redis-cli` shims through `current\redis`; MySQL uses `mysqld`, `mysql`, and `mysqladmin` shims through `current\mysql`. Selecting either service runtime does not start it. Clearing the last selection restores the snapshot. Switching replaces only `current\<kind>`, verifies the actual version, and rolls back on failure. Changes apply to newly opened terminals.
