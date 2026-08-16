# SoftPilot

**English** | [简体中文](README.zh-CN.md)

SoftPilot is a portable Windows application for installing and managing multiple versions of Node.js, Java, and Python.

## Get started

1. Download `SoftPilot.exe` and run it directly. No installation is required.
2. On first launch, choose a workspace. SoftPilot moves itself to that location and restarts automatically. You can also create a desktop shortcut.
3. Open a runtime's **Version management** tab to install the versions you need.
4. In the **Installed** tab, select a **Terminal default** version. Newly opened terminals will then use that version.

To upgrade, exit SoftPilot and replace the existing `SoftPilot.exe` with the new one. Installed runtimes and application data are preserved.

## Features

- Discover supported versions from official Node.js, Eclipse Temurin, and Python sources.
- Install and manage multiple runtime versions side by side.
- Choose a terminal-default version without reinstalling or removing other versions.
- Configure `node`, `npm`, `npx`, Java, and Python command entries automatically for newly opened terminals.
- Detect runtimes installed outside SoftPilot in read-only mode.
- Show download progress and operation results directly beside each version.
- Permanently uninstall versions that are no longer needed.
- Switch between English and Simplified Chinese. English is the default.

## Supported environment

- Windows 11 24H2 or later
- x64 systems
- Node.js Windows x64 releases
- Eclipse Temurin HotSpot JDK Windows x64 releases
- CPython Windows x64 releases
