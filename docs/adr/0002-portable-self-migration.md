# ADR 0002: Portable self-migrating distribution

**English** | [简体中文](0002-portable-self-migration.zh-CN.md)

Status: Accepted

## Decision

Distribute one self-contained x64 `SoftPilot.exe`. On first launch, the user selects a workspace; SoftPilot verifies and moves itself to `<SoftPilotRoot>\SoftPilot.exe`, then stores all managed content under `<SoftPilotRoot>\SoftPilotData`.

CLI and shims are verified embedded payloads deployed atomically into `SoftPilotData\tools`. Concrete runtimes remain under `SoftPilotData\app\<kind>\<version>` for direct IDE use.

## Rationale

- No installation or user-facing ZIP is required.
- Replacing the EXE does not touch runtimes or user data.
- Source cleanup deletes only an EXE proven identical to the target.
- Installer, uninstaller, Apps & Features, and Start menu maintenance are eliminated.

## Consequences

The optional shortcut is desktop-only. Removal and upgrades are file operations: exit SoftPilot, replace or delete the EXE, and preserve `SoftPilotData` unless a full cleanup is intended. The WinUI single-file runtime may extract native files into the user temporary directory. Production EXEs and embedded tools should be Authenticode-signed.
