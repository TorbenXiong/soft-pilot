# SoftPilot

SoftPilot 是跨平台的软件与工具插件化管理平台。用户首次运行时选择工作区，后续通过统一界面和 `spt` CLI 发现、安装、升级、配置、启停、卸载、恢复和诊断软件；升级宿主时只替换当前平台的主发布物，工作区、插件和已管理软件保持独立。

当前仓库已经完成 Rust + Slint + Wasmtime Component Model 的平台基础验证，尚未交付正式的软件安装、卸载、联网仓库或生产发布能力。

## 当前能力

- `softpilot-core`：插件 ID 和跨平台 target 值对象。
- `softpilot-plugin-api`：`plugin.json` 校验、插件 ZIP 安全检查、Wasm Component 结构与 WIT 契约验证、受限实例化和 descriptor 调用。
- `spt plugin inspect <package>`：只读检查本地 `.softpilot-plugin`，支持 `--json`。
- `softpilot-gui`：Slint 工作区目录选择界面和 Windows/macOS/Linux 平台能力探针。
- `fixtures/recipe-plugin`：最小声明式插件样例。
- `fixtures/lifecycle-component`：真实 lifecycle Component 及安全边界测试变体。

`.softpilot-plugin` 当前是以 `plugin.json` 为根入口的 ZIP 容器。只读检查不会提取文件或执行插件代码。

## 目录

- `apps/softpilot-gui`：Slint GUI 与平台探针。
- `apps/spt`：CLI。
- `crates/softpilot-core`：核心值对象。
- `crates/softpilot-plugin-api`：插件格式、WIT 和 Component 宿主边界。
- `fixtures`：声明式及 Wasm Component 测试插件。
- `specs/plugin`：Manifest Schema 与 WIT ABI。
- `eng`：专项验证脚本。

## 验证

需要 Rust 1.97.1。依赖由 `Cargo.toml` 精确约束并由 `Cargo.lock` 固定。

```powershell
cargo fmt --all -- --check
cargo check --workspace --all-targets --locked
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --all-targets --locked
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\test-lifecycle-component.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\test-windows-spike.ps1
```

跨平台原生 runner 使用统一探针入口：

```powershell
pwsh -NoProfile -File ./eng/test-platform-spike.ps1
```

Linux CI 启动隔离的 Xvfb 并使用 `SLINT_BACKEND=winit-software`。完整 Windows 构建需要 MSVC Build Tools；三平台矩阵定义在 `.github/workflows/ci.yml`。

## 文档

- [插件平台架构](docs/architecture.md)
- [当前实施计划](docs/implementation-plan.md)
- [M0 平台基础归档](docs/archive/m0-plugin-platform-spike-2026-08.md)
- [ADR 0001：Rust 宿主与 Wasm 插件平台](docs/adr/0001-rust-wasm-plugin-platform.md)
- [`plugin.json` JSON Schema](specs/plugin/plugin-manifest.schema.json)
- [WIT 插件契约](specs/plugin/wit/softpilot-plugin.wit)
