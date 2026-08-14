# SoftPilot 实施计划

状态：执行中
计划版本：1.0
最后更新：2026-08-14
下一任务：M1-02，定义工作区路径值对象、布局版本和 host triple 检测

本计划只记录当前唯一维护主线。M0 的详细技术决策与平台验证已归档至 [`archive/m0-plugin-platform-spike-2026-08.md`](archive/m0-plugin-platform-spike-2026-08.md)，自包含发布物结果归档至 [`archive/m0-release-spike-2026-08.md`](archive/m0-release-spike-2026-08.md)。

## 1. 当前基线

- [x] Rust 1.97.1 workspace、精确依赖和 `Cargo.lock`。
- [x] Manifest Schema、WIT ABI、插件 ZIP 安全检查和 Component 静态校验。
- [x] 真实 lifecycle Component 及 imports、trap、fuel、内存边界测试。
- [x] Slint 工作区选择 GUI 与 Windows 原生平台探针。
- [x] 仓库已只保留 Rust + Slint + Wasmtime 插件平台主线，Cargo workspace 位于根目录。
- [x] Windows、macOS ARM64/x64、Ubuntu x64 CI 已实现并通过 [首次完整原生验证](https://github.com/TorbenXiong/soft-pilot/actions/runs/31786758516)。

## 2. 当前阶段：平台基础收口

- [x] 在 GitHub 原生 runner 执行 format、check、Clippy、测试、Component 专项套件和平台探针。
- [x] 核对同一 Component 在 Windows、macOS ARM64/x64 和 Ubuntu x64 返回一致 descriptor。
- [x] 修复真实 runner 暴露的平台差异，并将三平台验证标记完成。
- [x] `M0-10`：生成 Windows x64 单文件、macOS ARM64/x64 app bundle 和 Linux x64 AppImage spike 发布物。
- [x] `M0-10` 验收：在四个干净 Runner 验证无需预装 Rust、Cargo 或其他语言运行时，并记录包体、动态依赖、启动和替换结果。

[x] 平台基础 Gate：三平台原生验证全部通过；同一 Component 行为一致；错误插件不破坏宿主；自包含发布物可在干净环境启动。

## 3. 后续阶段

### M1：工作区与插件生命周期

- 工作区路径、布局版本、host triple 和跨进程锁。
- SQLite schema、迁移器、事务日志与故障恢复。
- 插件摘要、兼容性、权限差异、安装、启停、卸载、trash 和恢复。
- `spt workspace` 与 `spt plugin` CLI、JSON envelope、错误阶段和退出码。

### M2：软件安装事务与声明式 recipe

- 受控 HTTPS、来源 allowlist、内容寻址缓存、哈希与签名。
- ZIP/tar 安全解包、staging、健康检查、不可变实例和原子提交。
- 软件发现、安装、多版本切换、卸载和恢复。
- 第一个首方真实软件插件及本地端到端故障矩阵。

### M3：Wasm 插件运行时

- 目录、版本、计划、配置和健康检查 WIT 接口。
- Wasmtime resource limiter、fuel、deadline、取消和调用级隔离。
- 权限授权、升级扩权确认、兼容矩阵和恶意插件回归集。

### M4：平台、Shell 与配置

- Windows、macOS、Linux 平台适配和 Shell 初始化。
- Shell 集成快照、幂等启停与安全恢复。
- 配置 Schema、差异、备份、恢复和平台安全存储。

### M5：完整 GUI

- 设计系统、首次运行、软件首页、插件中心和版本管理。
- 配置、任务、诊断、设置、权限确认和管理等级。
- 三平台布局、缩放、主题、键盘和辅助功能验证。

### M6：仓库、发布与生产加固

- TUF 信任根、发布者身份、撤销、过期、回滚和密钥轮换。
- Windows 签名、macOS 签名与公证、Linux AppImage 签名。
- 主程序原子替换、回滚、SBOM、来源证明和发布冒烟矩阵。

### M7：通用软件与工具生态

- 统一能力矩阵：发现、安装、升级、卸载、恢复、多版本、配置、启动、健康和数据管理。
- 便携软件、原生安装器、系统包管理器、服务型软件和外部软件适配。
- 插件中心展示管理等级、权限、数据位置、卸载残留和恢复保证。
- 新软件类型只增加插件或平台适配器，不修改 GUI、CLI 和事务核心业务分支。

## 4. 下一执行队列

1. 执行 `M1-02`：定义工作区路径值对象、布局版本和 host triple 检测。
2. 为路径规范化、布局兼容范围和三平台 host triple 增加单元测试。
3. 执行 `M1-03`：实现 `spt workspace init|show`、首次选择和工作区元数据。
4. 执行 `M1-04`：把已验证的跨进程锁探针收敛为工作区锁用例。

## 5. 决策队列

- M1-D1：SQLite 驱动、静态链接策略和精确版本。
- M1-D2：跨平台文件锁、原子替换和目录链接策略。
- M1-D3：trash 默认保留周期与可配置边界。
- M2-D1：HTTP、哈希、签名和归档 crate 的精确版本。
- M2-D2：第一个首方真实软件插件。
- M4-D1：Python 跨平台供应链方案。

上述事项涉及新依赖、下载、系统环境或外部服务时，实施前单独确认。
