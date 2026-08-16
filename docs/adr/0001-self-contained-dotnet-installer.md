# ADR 0001: Custom .NET installer

**English** | [简体中文](0001-self-contained-dotnet-installer.zh-CN.md)

Status: Superseded by [ADR 0002](0002-portable-self-migration.md)

## Decision

V1 originally used self-contained WPF installer and uninstaller applications instead of a third-party installer framework.

## Rationale and consequence

This reused .NET path-validation code and supported custom upgrade semantics without another toolchain. It also required maintaining registration, shortcuts, rollback, signing, and self-removal. Portable distribution later removed that maintenance burden.
