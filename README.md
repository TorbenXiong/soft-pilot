# SoftPilot

**English** | [简体中文](README.zh-CN.md)

SoftPilot is a portable Windows application for managing Node.js, Java, Python, Redis for local development, and the latest portable Git for Windows.

## Get started

1. Download `SoftPilot.exe` and run it directly. No installation is required.
2. On first launch, choose a workspace. SoftPilot moves itself to that location and restarts automatically. You can also create a desktop shortcut.
3. Open a runtime's **Version management** tab to install the versions you need.
4. In the **Installed** tab, select a **Terminal default** version. Newly opened terminals will then use that version.

To upgrade, exit SoftPilot and replace the existing `SoftPilot.exe` with the new one. Installed runtimes and application data are preserved.

## Download sources

Node.js and Temurin archives automatically use the faster responsive source after small HTTPS probes against the official source and the built-in TUNA mirror. Python remains official-only. Redis versions are cross-checked with official Redis releases; Windows x64 archives come from the community `redis-windows/redis-windows` project and are accepted only with GitHub-provided SHA-256 digests. Git uses the latest x64 PortableGit asset from the official Git for Windows GitHub repository and also requires its GitHub-provided SHA-256 digest.

## Features

- Discover supported versions from official Node.js, Eclipse Temurin, Python, and Redis catalogs.
- Install and manage multiple runtime versions side by side.
- Choose a terminal-default version without reinstalling or removing other versions.
- Configure `node`, `npm`, `npx`, Java, Python, `redis-server`, and `redis-cli` command entries automatically for newly opened terminals.
- Manage the latest verified patch from each available Redis major line and run one local instance with version-specific data and logs on `127.0.0.1:6379`.
- Detect runtimes installed outside SoftPilot in read-only mode.
- Show download progress and operation results directly beside each version.
- Permanently uninstall versions that are no longer needed.
- Install, launch, upgrade, and uninstall a single managed copy of the latest portable Git for Windows without changing the user `PATH` or other Git installations; inspect SSH and Git LFS component health and explicitly edit global `user.name` and `user.email` from the Git page. Uninstall preserves the unified download cache and global Git configuration, including `user.name` and `user.email`.
- Apply and save module visibility and order changes immediately.
- Switch between English and Simplified Chinese. English is the default.

## Supported environment

- Windows 11 24H2 or later
- x64 systems
- Node.js Windows x64 releases
- Eclipse Temurin HotSpot JDK Windows x64 releases
- CPython Windows x64 releases
- Redis x64 community builds from `redis-windows/redis-windows`, for local development only
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
