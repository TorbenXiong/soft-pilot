# SoftPilot

SoftPilot 是跨平台的软件与工具插件化管理平台。用户首次运行时选择工作区，后续通过统一界面和 `spt` CLI 发现、安装、升级、配置、启停、卸载、恢复和诊断软件；升级宿主时只替换当前平台的主发布物，工作区、插件和已管理软件保持独立。

当前仓库已经完成 Rust + Slint + Wasmtime Component Model 的平台基础验证，尚未交付正式的软件安装、卸载、联网仓库或生产发布能力。

## 当前能力

- `softpilot-core`：插件 ID、跨平台 target、工作区路径、布局版本和 host triple 值对象。
- `softpilot-engine`：工作区初始化、元数据、定位顺序、跨进程锁和插件安装用例。
- `softpilot-storage`：静态链接 SQLite 的 host 状态库、schema 迁移、插件包状态与事务边界。
- `softpilot-plugin-api`：`plugin.json` 校验、插件 ZIP 安全检查、Wasm Component 结构与 WIT 契约验证、受限实例化和 descriptor 调用。
- `spt plugin inspect <package>`：只读检查本地 `.softpilot-plugin`，支持 `--json`。
- `spt plugin install <package>`：在工作区锁内验证、staging 并原子提交不可变插件包；新增权限必须通过 `--accept-permissions` 显式确认。
- `spt plugin list|enable|disable`：列出已安装版本，以可重复的状态事务启用指定版本或最高语义版本，并在不删除包体的情况下停用。
- `spt plugin uninstall|trash|restore`：把已停用的精确版本移动到可恢复 trash、列出 trash，并恢复原不可变路径；插件数据目录不随包体卸载。
- `spt plugin recover`：在工作区锁内检查持久文件操作 journal，安全完成已发生的目录重命名，或取消尚未发生的安装提交。
- `spt workspace init|show`：初始化、记住并读取工作区，支持 `--json`。
- `softpilot-gui`：Slint 工作区目录选择界面和 Windows/macOS/Linux 平台能力探针。
- `fixtures/recipe-plugin`：最小声明式插件样例。
- `fixtures/lifecycle-component`：真实 lifecycle Component 及安全边界测试变体。

`.softpilot-plugin` 当前是以 `plugin.json` 为根入口的 ZIP 容器。只读检查不会提取文件或执行插件代码。
包检查会拒绝路径穿越、大小写折叠重复、文件/目录前缀冲突、特殊文件、重复项、超量内容和缺失声明项；Component 还必须满足零 imports 与版本化 lifecycle WIT 类型契约。
检查结果包含整个包文件的字节数与小写 SHA-256。安装前兼容性检查分别验证精确 plugin API、可选 host semver range 和当前 host triple；权限升级按 canonical HTTPS origin、进程、Shell 与 OS grant 生成稳定的新增/移除差异。

工作区核心值对象当前要求根路径为非文件系统根的绝对路径，拒绝 `..` 并执行不访问磁盘的词法规范化。布局版本从 `1` 开始，未知版本必须显式迁移；host triple 使用 `x86_64-pc-windows-msvc`、`aarch64-apple-darwin`、`x86_64-unknown-linux-gnu` 等稳定 Rust target-triple 名称。

初始化和后续定位：

```powershell
spt --workspace D:\SoftPilotWorkspace workspace init
spt workspace show
spt workspace show --json
spt --workspace D:\SoftPilotWorkspace plugin install .\plugin.softpilot-plugin
spt --workspace D:\SoftPilotWorkspace plugin install .\plugin.softpilot-plugin --accept-permissions --json
spt --workspace D:\SoftPilotWorkspace plugin list --json
spt --workspace D:\SoftPilotWorkspace plugin enable dev.example.plugin --version 1.2.3
spt --workspace D:\SoftPilotWorkspace plugin disable dev.example.plugin
spt --workspace D:\SoftPilotWorkspace plugin uninstall dev.example.plugin --version 1.2.3
spt --workspace D:\SoftPilotWorkspace plugin trash --json
spt --workspace D:\SoftPilotWorkspace plugin restore dev.example.plugin --version 1.2.3
spt --workspace D:\SoftPilotWorkspace plugin recover --json
```

所有 `--json` 命令使用统一 envelope。成功为 `{"ok":true,"data":...}`；失败写入 stderr，格式为 `{"ok":false,"error":{"code":"...","stage":"...","message":"..."}}`。稳定退出码分类为：`10--12` 工作区错误、`20` 包体/输入无效、`21` 不兼容、`22` 权限待确认、`23` 生命周期状态冲突、`24` 不安全或歧义状态、`30` 工作区锁争用、`40` 内部 I/O/状态失败、`50` 输出序列化失败。Clap 参数错误保留退出码 `2`。

`workspace init` 只接受不存在、空目录或已经包含兼容 `workspace.json` 的路径，不会接管含有未知用户数据的非空目录。成功后写入当前用户 bootstrap；后续按 `--workspace`、`SOFTPILOT_WORKSPACE`、发布物旁便携定位文件、用户 bootstrap 的顺序定位。

工作区写操作使用根目录下稳定的 `workspace.lock` 跨进程串行化。锁持有期间，`workspace.lock.owner.json` 提供 owner ID、PID、操作名和获取时间等诊断信息；等待超时会报告最后一次可读取的持有者，而不会绕过锁继续写入。

每个 `hosts/<host-triple>/data/state.db` 使用 rusqlite 0.40.2 与内嵌 SQLite 3.53.2，不依赖系统预装 SQLite。数据库通过 application ID、schema 版本、迁移历史及 workspace/host identity 校验归属；未知数据库和未来版本会在写入前拒绝。schema v2 记录已原子提交插件包的摘要、大小、相对路径、Manifest 与 Component 校验结果；schema v3 记录每个插件当前启用的已安装版本；schema v4 记录可恢复 trash 中的包体及其原始位置；schema v5 在目录重命名前持久化 install/trash/restore journal。

插件安装不会解压或执行包内代码。源文件在取得工作区锁前完成首次校验，复制到 `plugins/staging/install-<uuid>` 后再次校验相同摘要、大小、Manifest、兼容性和 Component 结果，再以目录重命名提交到 `plugins/packages/<host-triple>/<plugin-id>/<version>/package.softpilot-plugin`。同一 host 下相同 ID/version 只能对应相同内容；状态写入失败时会回滚刚提交的目录。host triple 隔离避免一个平台卸载仍被另一平台状态库引用的包体。

## 目录

- `apps/softpilot-gui`：Slint GUI 与平台探针。
- `apps/spt`：CLI。
- `crates/softpilot-core`：核心值对象。
- `crates/softpilot-engine`：共享宿主用例、工作区与事务协调。
- `crates/softpilot-storage`：SQLite schema、迁移器与事务接口。
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

当前仓库不配置 GitHub Actions 或其他自动 CI/CD，开发阶段由维护者在 Windows 本地按需执行上述检查。跨平台探针入口继续保留，供未来恢复对应平台验收时使用：

```powershell
pwsh -NoProfile -File ./eng/test-platform-spike.ps1
```

完整 Windows 构建需要 MSVC Build Tools。自动 CI、Artifact 和发布流程将在项目进入生产加固阶段后重新评估，恢复前不得将未执行的远端检查标记为通过。

### 自包含发布 spike

`eng/package-release.ps1` 必须在目标平台原生执行，并使用锁定依赖构建：

```powershell
./eng/package-release.ps1 -PlatformId windows-x64
./eng/package-release.ps1 -PlatformId macos-arm64
./eng/package-release.ps1 -PlatformId macos-x64
./eng/package-release.ps1 -PlatformId linux-x64 -LinuxDeployPath <verified-linuxdeploy-path>
```

脚本分别生成 Windows 单文件 `SoftPilot.exe`、包含 `SoftPilot.app` 的 macOS ZIP 传输包和 Linux 单文件 AppImage，并同时写出 `SHA256SUMS.txt` 与动态依赖元数据。Component fixture 和验证脚本位于单独的验证载荷中，不进入主发布物。

打包和发布物验证脚本继续作为本地工具保留；自动构建、CI Artifact 和干净 Runner workflow 当前已停用。当前发布物仅用于 M0 技术验证，尚未签名、公证或作为正式 Release 发布。

四个平台的实际包体、SHA-256、动态系统依赖和干净 Runner 验证结果见 [M0 自包含发布物验证记录](docs/archive/m0-release-spike-2026-08.md)。

## 文档

- [插件平台架构](docs/architecture.md)
- [当前实施计划](docs/implementation-plan.md)
- [M0 平台基础归档](docs/archive/m0-plugin-platform-spike-2026-08.md)
- [ADR 0001：Rust 宿主与 Wasm 插件平台](docs/adr/0001-rust-wasm-plugin-platform.md)
- [`plugin.json` JSON Schema](specs/plugin/plugin-manifest.schema.json)
- [WIT 插件契约](specs/plugin/wit/softpilot-plugin.wit)
