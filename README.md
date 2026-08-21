# SoftPilot

**English** | [简体中文](README.zh-CN.md)

SoftPilot is a portable Windows application for managing Node.js, Java, Python, Redis/MySQL for local development, and the latest portable Git for Windows.

## Get started

1. Download `SoftPilot.exe` and run it directly. No installation is required.
2. On first launch, choose a workspace. SoftPilot moves itself to that location and restarts automatically. You can also create a desktop shortcut.
3. Open a runtime's **Version management** tab to install the versions you need.
4. In the **Installed** tab, select a **Terminal default** version. Newly opened terminals will then use that version.

To upgrade, exit SoftPilot and replace the existing `SoftPilot.exe` with the new one. Installed runtimes and application data are preserved.

## Download sources

Node.js and Temurin archives automatically use the faster responsive source after small HTTPS probes against the official source and the built-in TUNA mirror. Python remains official-only. Redis versions are cross-checked with official Redis releases; Windows x64 archives come from the community `redis-windows/redis-windows` project and are accepted only with GitHub-provided SHA-256 digests. MySQL uses official Oracle Windows x64 ZIP archives with official detached OpenPGP signatures. If a compatible Microsoft Visual C++ x64 Runtime is missing, MySQL installation downloads it from Microsoft's official permalink, verifies its Authenticode signature, and requests administrator approval to install it. Git uses the latest x64 PortableGit asset from the official Git for Windows GitHub repository and also requires its GitHub-provided SHA-256 digest.

## Features

- Discover supported versions from trusted Node.js, Eclipse Temurin, Python, Redis, and MySQL catalogs.
- Install and manage multiple runtime versions side by side.
- Choose a terminal-default version without reinstalling or removing other versions.
- Configure `node`, `npm`, `npx`, Java, Python, Redis, and MySQL command entries automatically for newly opened terminals.
- Manage the latest verified patch from each available Redis major line and run one local instance with version-specific data and logs on `127.0.0.1:6379`.
- Manage MySQL 8.4 LTS and the 5.7.44 compatibility line as concurrently runnable local instances, with data, configuration, credentials, logs, state, and ports isolated by `major.minor` line.
- Detect runtimes installed outside SoftPilot in read-only mode.
- Show download progress and operation results directly beside each version.
- Permanently uninstall versions that are no longer needed.
- Install, launch, upgrade, and uninstall a single managed copy of the latest portable Git for Windows without changing the user `PATH` or other Git installations; inspect SSH and Git LFS component health and explicitly edit global `user.name` and `user.email` from the Git page. Uninstall preserves the unified download cache and global Git configuration, including `user.name` and `user.email`.
- Use the local toolbox to beautify, minify, and validate JSON with maintained history, environment variables, and Windows Hosts. `Path` supports row-based editing, ordering, and missing-path warnings; machine-variable and Hosts saves request elevation automatically, and Hosts is backed up before writing.
- Apply and save module visibility and order changes immediately.
- Switch between English and Simplified Chinese. English is the default.

## Supported environment

- Windows 11 24H2 or later
- x64 systems
- Node.js Windows x64 releases
- Eclipse Temurin HotSpot JDK Windows x64 releases
- CPython Windows x64 releases
- Redis x64 community builds from `redis-windows/redis-windows`, for local development only
- Oracle MySQL Community Server Windows x64 ZIP archives (8.4 LTS recommended; 5.7.44 for legacy compatibility only)
- Latest Git for Windows PortableGit x64 release

The unified download cache lives under `SoftPilotData\cache\downloads`. On every startup, SoftPilot automatically removes cache files older than 30 days and deletes empty directories; individual module uninstall operations do not manage cache. Use `spt cache clean` when an immediate full cleanup is needed.

## Redis CLI

```powershell
spt runtime install redis@8.2.9
spt use redis@8.2.9 --global
spt redis start
spt redis status --json
spt redis stop
spt runtime uninstall redis@8.2.9                 # Preserves Redis data and logs
spt runtime uninstall redis@8.2.9 --delete-data   # Permanently deletes them
```

SoftPilot does not register Redis as a Windows Service or enable automatic startup. The Windows build is not affiliated with or endorsed by Redis Ltd. and should not be used as a production deployment.

## MySQL CLI

```powershell
spt runtime install mysql@8.4.11
spt use mysql@8.4.11 --global
spt mysql start 8.4.11
spt mysql start 5.7.44
spt mysql status --json
spt mysql credentials 8.4.11  # Explicitly reveals this version's root credentials
spt mysql stop 8.4.11
spt runtime uninstall mysql@8.4.11                 # Preserves the 8.4 line's data, config, credentials, and logs
spt runtime uninstall mysql@8.4.11 --delete-data   # Permanently deletes them
```

MySQL 8.4 and 5.7 can run concurrently and default to ports `3306` and `3307`; each stopped version can use another non-conflicting port. Each row's password action copies only that version's root password, protected for the current Windows user with DPAPI. First start performs secure initialization with TCP disabled, and regular instances bind only to loopback; SoftPilot installs neither a Windows Service nor automatic startup. MySQL 5.7.44 is for legacy compatibility, while new projects should use 8.4 LTS. When a compatible Visual C++ x64 Runtime is absent, SoftPilot verifies Microsoft's signature and requests administrator approval to install it; this shared system component is not removed with MySQL.
