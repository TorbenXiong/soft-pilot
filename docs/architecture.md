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
   │  ├─ git\
   │  ├─ cocos\
   │  └─ cocos-creator\<version>\
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
- Same-line upgrades for Node.js, Java, Python, Redis, and MySQL reuse the complete installation transaction and move the terminal default only after the new version commits successfully. The previous runtime, cache, and any Redis/MySQL service data remain until the user explicitly uninstalls them.
- All modules share `cache\downloads`. Uninstall transactions include archives, signatures, and helper cache entries that can be safely attributed to the target version or single-copy module. The unified cache service removes other files older than 30 days and empty directories during startup; `spt cache clean` remains available for immediate full cleanup.
- Git installation and upgrade follow `official latest release → cache → SHA-256 verification → staging → version health check → app\git`. Upgrade swaps the single managed directory with rollback, and uninstall removes both that directory and its PortableGit archive without touching another Git installation. The Git page reads and writes global `user.name` and `user.email` through the managed Git only after the user explicitly saves the form. Uninstall preserves global Git configuration, SSH keys, credentials, and repositories.
- Cocos Dashboard follows Git's single-managed-copy model. SoftPilot discovers the latest Windows installer from the official Cocos download page, requires it in the built-in SHA-256 catalog, downloads it into the unified cache, and verifies its hash, official HTTPS origin, and Xiamen Yaji Software Authenticode publisher. It then creates an MSI administrative image in isolated staging, verifies the actual `CocosDashboard.exe` version and signature, and atomically replaces `app\cocos`. Failed upgrades restore the old directory. Uninstall transactionally removes the managed copy and installer cache and can remove `%USERPROFILE%\.Cocos` Dashboard data only when explicitly selected.
- Cocos Creator uses a managed multi-version model. Official stable ZIPs are cached by version under `cache\downloads\cocos-creator\<version>`. Cocos currently publishes no SHA-256 for these archives, so the downloader calculates SHA-256 during HTTPS transfer, records it with the version cache, and rechecks it before extraction. Archives enter isolated staging through path-traversal-safe ZIP extraction. SoftPilot requires the exact expected file version and Xiamen Yaji Software Authenticode signature on `CocosCreator.exe` before atomically committing `app\cocos-creator\<version>`. Upgrade reuses that transaction and preserves the previous editor. Uninstall removes only the selected editor and version cache while always preserving `.CocosCreator`, extensions, and projects. Neither Cocos flow writes a system installation directory or HKLM, requires UAC, or changes PATH.
- Uninstallation moves the runtime to staging first, deletes its state, then removes its files. Failures restore both directory and state.
- GUI and CLI mutations share a cross-process workspace lock.

Node.js uses the signed official checksum manifest. Temurin uses Adoptium hashes and signatures. Python uses the official python.org catalog and Install Manager. Redis versions must exist in the official Redis release catalog; Windows archives come from `redis-windows/redis-windows` GitHub Releases and require the GitHub asset SHA-256 digest. MySQL accepts only supported Windows x64 ZIP archives from Oracle's `cdn.mysql.com` and verifies each detached `.asc` signature against pinned primary-key fingerprints loaded from `repo.mysql.com`. MySQL installation also checks the x64 v14 Runtime in HKLM. When it is absent or older than `14.29.30157`, SoftPilot downloads only Microsoft's `https://aka.ms/vc14/vc_redist.x64.exe`, requires a valid Authenticode signature from Microsoft Corporation, and installs it through UAC. Git accepts only the latest x64 PortableGit self-extracting archive from the official `git-for-windows/git` release and requires its GitHub asset SHA-256 digest. Cocos Dashboard accepts only version-matched installers under `download.cocos.com/CocosDashboard` and requires an exact built-in SHA-256 match. Creator accepts only stable Windows ZIPs with matching directory and asset versions under `download.cocos.com/CocosCreator`. Both require an actual-version health check and a valid launcher Authenticode signature from Xiamen Yaji Software Co., Ltd. A user-installed Python Install Manager is preserved; when absent, SoftPilot verifies, temporarily registers, and then removes the official package.

## Download sources

Node.js and Temurin probe the built-in official and TUNA archive sources concurrently, reading at most 64 KiB with a four-second timeout per source. Network failures can fall back; integrity failures abort without fallback. Python remains official-only. Redis has one fixed community Windows archive source, cross-checks each version against official Redis metadata, and does not accept custom sources. The MySQL catalog currently contains 8.4 LTS and the final 5.7.44 compatibility release; it is Oracle-only with no mirror or custom-source option. Git, Cocos Dashboard, and Cocos Creator are official-only. Git and Dashboard have no version selector; the Creator UI shows only the latest official stable release and locally installed versions. If the official Cocos Dashboard hostname returns 403, SoftPilot may connect through that hostname's published CNAME, while the request URL, Host header, and TLS SNI remain `download.cocos.com`.

## Redis service

Redis is managed as one local foreground process that survives GUI or CLI exit. Starting a version creates version-specific configuration, data, and log paths, then requires a successful `redis-cli PING`, the expected Redis version, and a Windows TCP listener PID matching the process SoftPilot started. This avoids comparing the MSYS2 POSIX PID reported by Redis with the Windows PID. Stopping sends `SHUTDOWN` only when the listener PID matches; fallback termination is allowed only after PID, executable path, and process start time all match the saved SoftPilot state. SoftPilot does not install a Windows Service or configure automatic startup. Redis data and logs are preserved by default when a runtime version is uninstalled; the GUI confirmation and CLI `--delete-data` option can explicitly include them in the same rollback-capable uninstall transaction.

## MySQL service

MySQL isolates data, configuration, DPAPI credentials, logs, process state, and ports by `major.minor` line, allowing 5.7 and 8.4 to run concurrently. 8.4 defaults to port `3306` and 5.7 to `3307`; stopped instances may use another port, but instances cannot share one. SoftPilot registers neither a Windows Service nor automatic startup.

First start creates the offline data directory with `--initialize-insecure`, then sets a random root password through a temporary process with TCP disabled and Windows shared memory enabled. Regular startup requires `SELECT VERSION()` to match the target and the listener PID to belong to the new process. Plaintext passwords exist only briefly in memory and a temporary client file, never in client command-line arguments. Stop prefers `mysqladmin shutdown`; fallback termination still requires the target version's PID, executable path, and start time. Uninstall preserves release-line data unless `--delete-data` explicitly includes data, configuration, credentials, and logs in the rollback-capable transaction.

The Visual C++ Runtime is a shared system component outside MySQL staging rollback and uninstall. If its installer requires a restart, the MySQL installation stops and asks the user to retry afterward.

## Toolbox

The toolbox is GUI-only. JSON is parsed locally with .NET; up to 50 history entries are saved by atomic replacement in `SoftPilotData\data\toolbox\json-history.json`. Environment variables are maintained per user or machine scope. `Path` is losslessly split and ordered on semicolons, preserving duplicates and empty entries while highlighting expanded directories that do not exist.

Denied machine-variable and Hosts writes relaunch the same application with a one-time staging request for elevation. Hosts preserves its encoding and is backed up to `SoftPilotData\data\toolbox\hosts-backups` before writing, retaining at most 20 copies.

Runtime, Git, Cocos, and toolbox module visibility and order update immediately and are saved serially.

## Terminal default

Selecting the first terminal-default runtime snapshots user `PATH` and `JAVA_HOME` and enables SoftPilot shims. `current\node` is added to `PATH` only while Node.js has a current version, and `JAVA_HOME` points to `current\java` only while Java has one; `PYTHONHOME` is never set. Redis uses `redis-server` and `redis-cli` shims through `current\redis`; MySQL uses `mysqld`, `mysql`, and `mysqladmin` shims through `current\mysql`. Selecting either service runtime does not start it. Clearing Node.js or Java immediately removes its environment reference, and clearing the last selection restores the full snapshot. Switching verifies the actual version and rolls back on failure. Changes apply to newly opened terminals.
