# SoftPilot M0 平台基础实施记录

状态：已归档
计划版本：0.6
最后更新：2026-08-14
归档范围：M0 规格、插件安全边界、Windows 平台验证与三平台 CI 实现
后续入口：[`../implementation-plan.md`](../implementation-plan.md)

归档状态：2026-08-14 结束维护。本文档保留 M0 平台基础的决策、实现和验证记录；后续工作以 [`../implementation-plan.md`](../implementation-plan.md) 为准，产品边界见 [`../architecture.md`](../architecture.md)，技术决策见 [`ADR 0001`](../adr/0001-rust-wasm-plugin-platform.md)。

## 1. 台账规则

状态标记：

- `[x]`：交付物和验收均完成。
- `[~]`：已完成部分实现，但未通过阶段验收。
- `[ ]`：尚未开始。
- `[!]`：存在阻塞或需要用户决策。

执行规则：

1. 按任务编号推进；同一时间只保留一个“下一任务”。
2. 每个任务完成后，更新状态、验证结果和必要的决策记录。
3. 任务涉及新增或变更依赖、下载安装工具、外部服务、系统环境、发布或破坏兼容性时，执行前单独确认。
4. 先完成 CLI 和宿主用例，再接 GUI；GUI 不直接访问数据库或平台 API。
5. 每个阶段必须通过本阶段 Gate 才能标记完成。允许为验证接口提前实现后续阶段的最小纵切，但不因此跳过 Gate。
6. Windows、macOS、Linux 的“已验证”只接受对应原生环境运行结果，不以交叉编译代替。
7. 默认不提交、推送或发布；Git 操作遵循仓库确认规则。

## 2. 当前基线

| 编号 | 状态 | 已有交付物 | 已验证内容 |
| --- | --- | --- | --- |
| BASE-01 | [x] | Rust 1.97.1 workspace、精确依赖、`Cargo.lock` | MSVC 默认特性编译、Clippy、测试 |
| BASE-02 | [x] | `plugin.json` JSON Schema 与 Rust 类型 | 必填字段、版本、target、权限、HTTPS、路径和重复项 |
| BASE-03 | [x] | `softpilot:plugin@0.1.0` WIT 与编译期 bindings | WIT 可由 Wasmtime bindgen 解析 |
| BASE-04 | [x] | `.softpilot-plugin` ZIP 只读检查 | 路径穿越、重复项、特殊项、数量和解压尺寸限制 |
| BASE-05 | [x] | Component 类型检查 | 结构合法、零 imports、必须实现 lifecycle 接口；检查阶段不实例化 |
| BASE-06 | [x] | `spt plugin inspect <package> [--json]` | recipe 样例包 CLI 冒烟验证 |
| BASE-07 | [x] | Windows C++ 构建环境 | Visual Studio Build Tools 2026、MSVC、Windows 11 SDK |
| BASE-08 | [x] | 零 imports 的真实 lifecycle Component fixture | 静态校验、无能力实例化、`descriptor` 调用专项测试 |
| BASE-09 | [x] | 恶意 Component fixture 变体和调用边界 | 额外 imports、trap、fuel 耗尽和内存上限专项测试 |
| BASE-10 | [x] | Slint 1.17.1 Windows GUI 与平台 spike | 自有目录选择、窗口、文件锁、junction、子进程和 Component 调用 |
| BASE-11 | [~] | 四 runner 三平台 GitHub Actions 矩阵 | workflow 与通用探针已完成；macOS/Linux 原生运行待首次 CI |

当前 Windows 验证基线：

- `cargo fmt --all -- --check`：通过。
- `cargo clippy --workspace --all-targets --locked -- -D warnings`：通过。
- `cargo test --workspace --all-targets --locked`：10 项通过，5 项 fixture 专项测试按设计忽略。
- `eng/test-lifecycle-component.ps1`：真实/恶意 Component 专项测试 5 项通过。
- `eng/test-windows-spike.ps1`：窗口、子进程、跨进程锁、junction 和 Component 调用通过。
- 默认 Wasmtime 特性的 `spt plugin inspect`：通过。

## 3. 全局交付顺序

```text
M0 规格与三平台技术验证
 └─ M1 工作区与插件生命周期
     └─ M2 软件安装事务与声明式配方
         └─ M3 Wasm 插件运行时
             └─ M4 Shell、配置与平台适配
                 └─ M5 Slint GUI 与插件中心
                     └─ M6 仓库、发布与生产加固
```

跨阶段约束：

- M1 的状态模型和事务边界必须先稳定，M2 才能写入软件实例。
- M2 的宿主计划执行器必须先稳定，M3 插件才能返回可执行计划。
- M4 的平台抽象必须通过 CLI 验证，M5 GUI 才能接入。
- M6 之前不得把开发构建描述为可信生产发布。

## 4. M0：规格与三平台技术验证

目标：证明选型、插件边界和无运行时发布方式在三平台可行。

- [x] `M0-01` 记录 Rust + Slint + Wasmtime 技术决策。
- [x] `M0-02` 定义 Manifest Schema、插件包入口和权限声明。
- [x] `M0-03` 定义 WIT lifecycle，并在宿主侧生成编译期 bindings。
- [x] `M0-04` 构建真实 WIT Component 测试插件；静态验证后实例化并调用 `descriptor`。
- [x] `M0-05` 编写恶意/错误 Component 测试集：畸形二进制、错误接口、额外 imports、trap、超时和超量内存。
- [x] `M0-06` Windows spike：Slint 窗口、目录选择、文件锁、junction/symlink、子进程、Component 调用。
- [~] `M0-07` macOS spike：窗口、目录选择、文件锁、symlink、子进程、同一 Component 调用；实现完成，ARM64/x64 原生运行待 CI。
- [~] `M0-08` Linux spike：窗口、目录选择、文件锁、symlink、子进程、同一 Component 调用；实现完成，Ubuntu 原生运行待 CI。
- [~] `M0-09` 建立三平台 CI，只执行锁定依赖的格式、编译、Clippy 和测试；workflow 已建立，待首次远端运行。
- [ ] `M0-10` 生成三平台自包含 spike 发布物并验证无需预装语言运行时。

执行前决策：

- `M0-D1`：选择 Component 测试插件构建方式及开发依赖。
- `M0-D2`：确认 Slint 精确版本、渲染后端和平台 feature 集。
- `M0-D3`：确认 macOS/Linux 原生 runner 来源和最低系统版本。

M0 Gate：

- 同一个 Component 在三个平台由宿主加载并返回一致 descriptor。
- 未授权插件无法获得文件、网络或进程能力。
- 错误插件只导致插件调用失败，不破坏宿主状态。
- 三个平台的 spike 发布物均可在干净环境启动。

## 5. M1：工作区与插件管理纵向切片

目标：完成本地插件从检查到安装、启停、卸载和恢复的完整生命周期。

- [~] `M1-01` 建立 Rust workspace 和 core/plugin-api/CLI 边界；当前仅有最小 crate。
- [ ] `M1-02` 定义工作区路径值对象、布局版本和 host triple 检测。
- [ ] `M1-03` 实现 `spt workspace init|show`、首次选择和工作区元数据。
- [ ] `M1-04` 实现跨进程工作区锁、锁持有者诊断和超时行为。
- [ ] `M1-05` 选择 SQLite 库，建立 schema、迁移器、事务接口和测试数据库。
- [~] `M1-06` 完成插件包读取、Manifest 校验和 Component 类型校验；尚缺包摘要与 host 兼容范围。
- [ ] `M1-07` 实现插件包 SHA-256、宿主/API/target 兼容检查和权限差异计算。
- [ ] `M1-08` 实现插件安装：cache、staging、验证、原子提交和状态写入。
- [ ] `M1-09` 实现 `plugin list|enable|disable`，保持当前激活版本单一且可回滚。
- [ ] `M1-10` 实现 `plugin uninstall|restore`、trash 保留策略和清理策略。
- [ ] `M1-11` 实现事务日志、启动恢复和阶段故障注入测试。
- [~] `M1-12` 统一 CLI 人类输出和 `--json` envelope；当前仅 `plugin inspect`。
- [ ] `M1-13` 补齐插件生命周期帮助文本、错误阶段和退出码规范。

执行前决策：

- `M1-D1`：SQLite 驱动、静态链接策略和精确版本。
- `M1-D2`：跨平台原子替换、文件锁和目录链接采用的 crate/系统 API。
- `M1-D3`：插件 trash 默认保留周期及用户可配置边界。

M1 Gate：

- 本地 recipe 插件可安装、禁用、启用、卸载和恢复。
- 无效 manifest、摘要、权限、API 或 target 不产生持久变更。
- 两个 CLI 进程竞争同一工作区时由锁串行化。
- 在每个事务阶段强制终止后，下次启动恢复到一致状态。

## 6. M2：软件安装事务与声明式配方

目标：让声明式插件安全地管理工作区内便携软件和多版本实例。

- [ ] `M2-01` 定义 recipe JSON Schema、版本目录模型和精确版本选择规则。
- [ ] `M2-02` 实现目录请求、宿主受控 HTTPS 客户端、origin allowlist 和响应上限。
- [ ] `M2-03` 实现内容寻址下载缓存、并发去重、断点策略和 SHA-256。
- [ ] `M2-04` 定义上游签名验证接口、密钥来源和失败策略。
- [ ] `M2-05` 实现 ZIP、tar.gz、tar.xz 安全解包及跨平台路径规则。
- [ ] `M2-06` 实现安装计划验证器，确保步骤与已授予权限一致。
- [ ] `M2-07` 实现 staging、健康检查、不可变实例和原子提交。
- [ ] `M2-08` 实现当前版本、shim、卸载、trash 和恢复事务。
- [ ] `M2-09` 实现 `software available|install|list|use|uninstall|restore`。
- [ ] `M2-10` 建立受控本地测试服务和端到端故障矩阵。
- [ ] `M2-11` 实现第一个首方真实 recipe 插件。

执行前决策：

- `M2-D1`：HTTP、哈希、签名和归档 crate 的精确版本与供应链评估。
- `M2-D2`：第一个真实软件插件及其官方元数据/签名来源。
- `M2-D3`：shim 的跨平台实现和 Windows 可执行转发方式。

M2 Gate：

- 路径穿越、链接逃逸、摘要错误、签名错误和健康检查失败均回滚。
- 相同内容只保留一个缓存对象，并发下载不提交半成品。
- 别名在事务开始前解析为确定版本。
- 当前版本切换失败后恢复旧实例和激活状态。

## 7. M3：Wasm Component 插件运行时

目标：允许无宿主能力的 Component 处理复杂解析和计划生成，同时由宿主执行所有副作用。

- [ ] `M3-01` 固化 lifecycle WIT 0.1.0 和兼容性测试。
- [ ] `M3-02` 实现 descriptor、catalog、plan、config 和 health 调用适配器。
- [ ] `M3-03` 实现编译缓存、实例池边界和确定性输入输出。
- [ ] `M3-04` 实现 fuel、epoch deadline、内存、返回值和并发限制。
- [ ] `M3-05` 实现 trap/panic/超时隔离和可行动错误信息。
- [ ] `M3-06` 实现首次安装权限确认和升级权限差异确认。
- [ ] `M3-07` 实现 Node.js 首方 Wasm 插件。
- [ ] `M3-08` 建立恶意插件和模糊输入回归集。

M3 Gate：

- 无限循环、超量内存、畸形返回值和 panic 不影响宿主进程一致性。
- 插件无法直接读取工作区、联网或启动进程。
- 扩权升级在用户确认前继续使用旧插件。

## 8. M4：Shell、配置与平台适配

目标：完成三平台激活、配置管理和必要的 OS 集成。

- [ ] `M4-01` 定义平台适配接口和能力探测。
- [ ] `M4-02` 实现 Windows 用户环境、junction/symlink 和环境广播。
- [ ] `M4-03` 实现 macOS/Linux symlink 与 bash/zsh/fish/PowerShell 初始化片段。
- [ ] `M4-04` 实现 Shell 集成快照、幂等启停和安全恢复。
- [ ] `M4-05` 实现配置 Schema、标准表单模型、差异、备份和恢复。
- [ ] `M4-06` 实现平台安全存储抽象，隔离密钥和 Token。
- [ ] `M4-07` 实现 Temurin 插件。
- [ ] `M4-08` 完成 Python 供应链 ADR 后实现 Python 插件。

M4 Gate：

- Shell 集成仅由用户显式启用，且只移除自身管理的内容。
- 切换版本不重写 Shell profile。
- 密钥和 Token 不进入 SQLite、普通配置、日志或诊断包。
- Windows、macOS、Linux 对相同用例返回一致状态和阶段化错误。

## 9. M5：Slint GUI 与插件中心

目标：在统一宿主用例之上交付跨平台桌面体验。

- [ ] `M5-01` 固化设计系统、导航、窗口行为和中英文资源结构。
- [ ] `M5-02` 实现首次运行、工作区选择和工作区错误恢复。
- [ ] `M5-03` 实现软件首页、插件中心和版本管理。
- [ ] `M5-04` 实现配置、任务、诊断和设置页面。
- [ ] `M5-05` 实现权限确认、管理等级、安全失败和恢复交互。
- [ ] `M5-06` 增加 GUI 自动化标识、键盘导航和屏幕阅读器语义。
- [ ] `M5-07` 完成三平台布局、缩放、主题和辅助功能验证。

M5 Gate：

- GUI 只调用宿主用例，不直接访问数据库或平台 API。
- GUI 与 CLI 对相同操作返回一致状态和错误阶段。
- 三平台完成布局、缩放、深浅主题、键盘和辅助功能检查。

## 10. M6：仓库、发布与生产加固

目标：建立可信插件分发和可替换主程序发布链。

- [ ] `M6-01` 设计插件仓库索引和 TUF 信任根。
- [ ] `M6-02` 实现发布者身份、撤销、过期、回滚和密钥轮换。
- [ ] `M6-03` 实现 Windows Authenticode 发布。
- [ ] `M6-04` 实现 macOS Developer ID、notarization 和 app bundle。
- [ ] `M6-05` 实现 Linux AppImage、哈希和签名发布。
- [ ] `M6-06` 实现主程序原子替换、回滚以及内嵌 `spt`/shim 刷新。
- [ ] `M6-07` 生成 SBOM、来源证明和依赖审计结果。
- [ ] `M6-08` 建立模糊测试、恶意插件测试和发布冒烟矩阵。

M6 Gate：

- 仓库回滚、冻结、过期元数据、撤销发布者和替换资产均被拒绝。
- 替换主发布物后继续使用原工作区、插件、配置和软件实例。
- 离线启动不依赖插件仓库可用性。
- 三平台发布物均经过对应签名、安装、升级、回滚和卸载验证。

## 11. M7：通用软件覆盖与生态扩展

目标：在核心宿主达到生产基线后，把桌面软件、CLI 工具、原生安装器、服务型软件和外部软件统一纳入插件化管理，同时如实呈现不同管理等级。

- [ ] `M7-01` 固化逐项能力矩阵：发现、安装、升级、卸载、恢复、多版本、配置、启动、健康和数据管理。
- [ ] `M7-02` 建立 ZIP/tar/raw/AppImage 等便携软件的通用 recipe 模板和跨平台回归套件。
- [ ] `M7-03` 建立 MSI/EXE、pkg/dmg、deb/rpm 原生安装器适配层，记录静默参数、退出码、重启和回滚能力。
- [ ] `M7-04` 建立 winget、Homebrew 和 Linux 系统包管理器的受控桥接模型及来源/版本核对。
- [ ] `M7-05` 扩展服务型软件生命周期：端口、服务启停、数据目录、备份恢复和升级前置检查。
- [ ] `M7-06` 实现外部软件只读发现、显式接管和解除接管，禁止误删非 SoftPilot 所有的数据。
- [ ] `M7-07` 实现交互式、许可受限、需重启或需提权软件的辅助安装流程和人工完成状态。
- [ ] `M7-08` 实现插件配置迁移、跨设备导入导出和平台安全存储引用，不导出真实密钥。
- [ ] `M7-09` 建立代表性生态验收集：桌面应用、CLI、开发运行时、数据库/服务、系统包和外部发现各至少一种。
- [ ] `M7-10` 在插件中心展示管理等级、支持能力、权限、数据位置、卸载后残留和恢复保证。

M7 Gate：

- 所有代表性软件都通过相同插件安装/卸载/配置入口管理，本体没有软件名称特判。
- 宿主对不支持的能力返回明确的“不支持”及原因，不模拟成功，不夸大回滚保证。
- 工作区托管软件可完整恢复；系统托管和外部软件只触碰明确授权且归属可证明的内容。
- 新增软件类型只需增加插件或平台适配器，不修改 GUI、CLI 与事务核心的业务分支。

## 12. 统一验证矩阵

每个 Rust 任务至少执行：

```powershell
cargo fmt --all -- --check
cargo check --workspace --all-targets --locked
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --all-targets --locked
```

按改动增加专项验证：

- Manifest/插件包：Schema 样例、畸形 JSON、路径、重复项、ZIP bomb 和特殊文件。
- Component：WIT 兼容、imports、trap、fuel、deadline、内存和返回值上限。
- 事务：阶段故障注入、并发、重复执行、回滚和启动恢复。
- Shell：幂等启停、用户已有内容保护和失败恢复。
- GUI：三平台截图、交互、键盘、辅助功能和错误状态。
- 发布：干净虚拟机安装、覆盖升级、主程序替换、回滚和卸载。

无法执行某项验证时，任务不得标记 `[x]`；必须记录未执行原因和风险。

## 13. 决策与授权队列

| 决策编号 | 最晚时点 | 内容 | 当前状态 |
| --- | --- | --- | --- |
| M0-D1 | `M0-04` 前 | Component 测试插件构建方式和依赖 | 已确认并实施 |
| M0-D2 | `M0-06` 前 | Slint 版本、后端和 features | 已确认并实施 |
| M0-D3 | `M0-07` 前 | macOS/Linux runner 和最低系统版本 | 已确认并实施 |
| M1-D1 | `M1-05` 前 | SQLite 驱动和链接策略 | 待确认 |
| M1-D2 | `M1-04` 前 | 文件锁、原子替换和链接实现 | 待确认 |
| M2-D1 | `M2-02` 前 | 网络、哈希、签名、归档依赖 | 待确认 |
| M2-D2 | `M2-11` 前 | 第一个真实软件插件 | 待确认 |
| M4-D1 | `M4-08` 前 | Python 跨平台供应链 | 待确认 |

### M0-D1 实施记录

目标：用真实 Rust guest 实现 `softpilot:plugin/software-plugin@0.1.0`，生成可由宿主静态检查、实例化并调用 `descriptor` 的 Component。

方案：

- 使用 Rust 1.97.1 原生 `wasm32-wasip2` target 直接生成 Component。
- fixture crate 仅新增精确版本 `wit-bindgen = 0.58.0`，从现有 WIT 生成 guest bindings。
- 不安装已进入弃用过程的 `cargo-component`。
- 不安装 `wasm-tools`；结构、imports 和接口由现有 Wasmtime 47.0.2 检查。
- Component 必须保持零 imports；验证发现 Rust 标准库会产生 WASI imports 后，fixture 改用 `no_std + alloc` 和仅用于测试的页分配器，宿主权限未放宽。
- Component fixture 构建为独立步骤，产物进入忽略的 `target`，不提交生成的 `.wasm`。
- 端到端调用测试标记为需要 fixture 的专项测试；统一脚本先构建 guest，再运行宿主测试，普通 host 测试不隐式递归调用 Cargo。

执行 M0-D1 已下载或修改：

- 下载 Rust 官方 `rust-std` 的 `wasm32-wasip2` target，安装到现有项目本地 `.tools/rust`。
- 从 crates.io 下载 `wit-bindgen 0.58.0` 及其锁定传递依赖。
- 修改 `Cargo.toml`、`Cargo.lock` 和 `rust-toolchain.toml`。
- 新增 `fixtures/lifecycle-component` fixture crate 和 Component 专项验证脚本。
- M0-04 已完成宿主 Component 调用适配器及专项测试；未修改系统环境或永久 `PATH`。

### M0-05 实施记录

- 复用 lifecycle fixture 的 Cargo features 生成 WASI imports、主动 trap、无限循环和 16 MiB 分配四种恶意变体，不提交生成的 Wasm。
- 宿主 `descriptor` 调用默认分配 1,000,000 fuel，并把单个 Component 线性内存限制为 8 MiB；内存增长越界强制 trap。
- 普通 Component、未授权 imports、主动 trap、fuel 耗尽和内存增长失败均由同一专项脚本验证；错误只终止插件调用。

### M0-D2/M0-06 实施记录

目标：用不依赖 Qt、GTK、WebView 或预装语言运行时的桌面 GUI 完成 Windows 原生 spike，并让同一代码进入后续 macOS/Linux 验证。

方案：

- 精确锁定 `slint = 1.17.1` 与 `slint-build = 1.17.1`；该版本要求 Rust 1.92，当前锁定的 Rust 1.97.1 满足要求。
- `slint` 关闭默认 features，只启用 `std`、`compat-1-2`、`backend-winit`、`renderer-femtovg`、`renderer-software` 和 `accessibility`。
- Winit 负责 Windows、macOS、X11 和 Wayland 窗口/事件；FemtoVG 为首选渲染器，软件渲染器作为无可用图形加速时的备用路径。
- 不启用 Qt、Skia、WGPU、system tray、live preview、MCP 或测试服务器等非 spike 必需能力。
- 不引入 `rfd`：Linux 的 XDG Portal/Zenity 会增加外部运行时前提；首次工作区目录选择改为宿主自有的 Slint 目录选择页和路径输入，保持发布物自包含。
- 按 Slint 官方 Windows 建议，为 GUI target 设置 8 MiB 主线程栈，避免 debug 构建的 MSVC 默认栈不足。
- 许可证暂按 Slint Royalty-free Desktop License 设计：在 GUI 的顶层“关于”入口提供 Slint 归属信息，并在未来公开下载页展示归属标识；若不接受归属条件，则必须改用商业许可证或重新选型。

执行 M0-D2/M0-06 已下载或修改：

- 从 crates.io 下载上述两个精确版本及锁定的传递依赖，缓存仍位于项目本地 `.tools/rust/cargo`。
- 修改 `Cargo.toml`、`Cargo.lock` 和 Windows target 构建配置。
- 新增 `apps/softpilot-gui`、`build.rs`、最小 `.slint` UI、目录选择 spike 和平台能力验证代码。
- 不安装系统软件，不修改永久 `PATH`，不要求安装 Qt、GTK 或 WebView。
- `softpilot-gui` 已交付自有目录选择页、可调整窗口、用户目录默认定位、Slint 归属入口和 accessibility 语义。
- Windows 原生探针已验证当前可执行文件子进程、`File::lock` 跨进程互斥、目录 junction 和同一真实 Component 调用。
- GUI 生成代码需要 Slint 宏内部的 `unsafe`，因此只对 GUI 入口 crate 放宽 `unsafe_code` lint；手写代码以及 core/plugin-api 仍禁止 `unsafe`。
- 使用 `computer-use` 对实际窗口完成视觉检查；目录列表、中文布局、关于入口、编辑框、按钮和窗口缩放状态正常。

### M0-D3/M0-07/M0-08/M0-09 实施记录

目标：在三类原生系统上验证同一 Rust 宿主、Slint GUI 和 Wasm Component 插件 ABI，避免用交叉编译代替运行时验证。

首轮原生 runner 固定为：

| 平台 | GitHub-hosted runner | 架构 | 首轮最低支持基线 |
| --- | --- | --- | --- |
| Windows | `windows-2025` | x64 | Windows 10 22H2 x64；Windows 11 ARM64 作为产品支持目标但首轮无官方 ARM64 runner |
| macOS | `macos-15` | ARM64 | macOS 15 ARM64 |
| macOS | `macos-15-intel` | x64 | macOS 15 x64 |
| Linux | `ubuntu-24.04` | x64 | Ubuntu 24.04 / glibc x64 |

CI 方案：

- 新增 `.github/workflows/ci.yml`，只授予 `contents: read`，使用固定提交的 `actions/checkout`。
- 直接使用 rustup 官方命令安装精确工具链 `1.97.1`、`rustfmt`、`clippy` 和 `wasm32-wasip2`；不引入第三方 Rust toolchain Action。
- 三平台执行 format、check、clippy、test、真实 lifecycle Component 构建与调用、平台能力探针和 GUI window smoke。
- Linux 在 Xvfb 中使用 Slint software renderer 执行 GUI smoke，并安装 `libxkbcommon-dev`、`libwayland-dev`、`libx11-dev`、`libxcursor-dev`、`libxi-dev`、`libxrandr-dev`、`libgl1-mesa-dev`、`xvfb`。
- 新增跨平台 spike 脚本；不下载或安装本机软件，不修改本机系统配置。
- workflow 只有在未来推送到 GitHub 后才会触发远端 runner 和下载；本阶段不提交、不推送、不创建 PR。

用户已确认 workflow、远端 Rust/Cargo 下载和 Ubuntu 系统依赖范围。已实施：

- 新增 `.github/workflows/ci.yml`，建立 Windows 2025 x64、macOS 15 ARM64、macOS 15 Intel 和 Ubuntu 24.04 x64 四 runner 矩阵。
- `actions/checkout` 固定到 v6.0.3 的不可变完整提交 SHA，关闭凭据持久化；workflow 仅有 `contents: read` 权限。
- 新增 `eng/test-platform-spike.ps1` 作为三平台统一入口；原 Windows 脚本保留并转调统一入口。
- Linux runner 仅安装已确认的 X11、Wayland、Mesa 和 Xvfb 依赖，启动隔离 Xvfb 并强制 Slint software renderer 完成窗口 smoke。
- Windows 本机离线回归已通过 format、check、Clippy、10 项普通测试、5 项 Component 专项测试和统一平台探针。
- workflow 尚未提交或推送，因此未发生远端下载、未消耗 Actions 配额；macOS/Linux 不得在首次原生 CI 成功前标记 `[x]`。

## 14. 下一执行队列

严格按以下顺序继续：

1. 在取得单次 Git 提交与推送授权后运行 M0 CI，核对 macOS ARM64/x64、Ubuntu x64 和 Windows x64 的原生结果。
2. 若任一 runner 失败，只修复可复现的平台差异并重新运行，全部通过后将 `M0-07`、`M0-08`、`M0-09` 标记 `[x]`。
3. `M0-10`：生成三平台自包含 spike 发布物并执行干净环境验证。
4. M0 Gate 通过后，从 `M1-02` 开始工作区实现。
5. M1-M6 达到生产基线后执行 M7，扩展到普通桌面软件、原生安装器、服务型软件和外部软件。
