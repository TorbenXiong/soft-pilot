# SoftPilot 实施计划

状态：执行中
计划版本：1.0
最后更新：2026-08-14
下一任务：M2-01，定义声明式 recipe、artifact/source 完整性与安装计划核心模型

本计划只记录当前唯一维护主线。M0 的详细技术决策与平台验证已归档至 [`archive/m0-plugin-platform-spike-2026-08.md`](archive/m0-plugin-platform-spike-2026-08.md)，自包含发布物结果归档至 [`archive/m0-release-spike-2026-08.md`](archive/m0-release-spike-2026-08.md)。

## 1. 初版 Windows-first 策略

- 初版核心的实现、人工验收和阶段 Gate 以 Windows x64 为唯一强制目标平台。
- M2 至 M5 的新增能力只要求 Windows 原生实现、本地自动化验证和人工验收；当前不建设或运行 CI/CD，也不新增或扩展 macOS、Linux 专用代码。
- 现有 macOS ARM64/x64、Linux x64 代码、host triple 和发布脚本暂时保留；GitHub Actions workflow 已移除，也不据此承诺后续新增能力持续保持跨平台可用。
- M0 已通过的三平台结果只作为当时版本的历史基线；没有对应原生环境复验的后续版本不得标记为 macOS 或 Linux 已验证。
- 核心模型、插件 ABI、工作区 host 隔离和依赖方向继续保持平台中立；Windows 平台差异放在入口或平台适配层，避免将来恢复其他平台时重写事务与插件核心。
- macOS 与 Linux 的实现补齐、原生 CI、人工验收和发布要求统一后置到 M8，待明确重新启用后执行。
- M6 之前仅执行本地验证，不运行自动 CI、Artifact 或部署任务；到达生产加固阶段后，先单独评估成本、Runner、必需检查和发布权限，再决定是否恢复 Windows CI/CD。

## 2. 当前基线

- [x] Rust 1.97.1 workspace、精确依赖和 `Cargo.lock`。
- [x] Manifest Schema、WIT ABI、插件 ZIP 安全检查和 Component 静态校验。
- [x] 真实 lifecycle Component 及 imports、trap、fuel、内存边界测试。
- [x] Slint 工作区选择 GUI 与 Windows 原生平台探针。
- [x] 仓库已只保留 Rust + Slint + Wasmtime 插件平台主线，Cargo workspace 位于根目录。
- [x] Windows、macOS ARM64/x64、Ubuntu x64 CI 已实现并通过 [M0 首次完整原生验证](https://github.com/TorbenXiong/soft-pilot/actions/runs/31786758516)；该结果为历史基线，不代表后续阶段持续跨平台验收。

## 3. 当前阶段：M1 工作区与插件生命周期

- [x] `M1-02`：定义工作区绝对路径值对象、布局版本兼容检查和六种受支持 host triple。
- [x] `M1-02` 验收：路径规范化与危险值拒绝、布局版本拒绝隐式迁移、目标映射和当前宿主检测测试通过。
- [x] `M1-03`：实现 `spt workspace init|show`、首次选择、自动定位和工作区元数据。
- [x] `M1-03` 验收：UUID v4、v1 元数据/指针、隔离 staging 初始化、非空目录保护、定位优先级、CLI JSON、GUI 初始化与原工作区复用测试通过。
- [x] `M1-04`：实现跨进程工作区锁、锁持有者诊断和超时行为。
- [x] `M1-04` 验收：独占锁、单调超时、结构化持有者诊断、RAII 释放、锁文件类型校验、初始化竞争收敛和真实 CLI 跨进程争用测试通过。
- [x] `M1-05`：采用 rusqlite 0.40.2 bundled SQLite 3.53.2，建立 host state schema、迁移器和事务接口。
- [x] `M1-05` 验收：application/schema/迁移历史/identity 校验、新建和 v0→v1、并发打开、外来及未来版本拒绝、迁移失败和业务事务回滚测试通过。
- [x] `M1-06`：完成插件包读取、Manifest 语义校验和 Component lifecycle WIT 类型校验。
- [x] `M1-06` 验收：有界 ZIP 读取、路径穿越、大小写碰撞、文件前缀冲突、特殊项、缺失声明、零 imports、descriptor、trap、fuel 和内存限制测试通过。
- [x] `M1-07`：实现完整插件包 SHA-256、host/plugin API/target 兼容检查和权限差异。
- [x] `M1-07` 验收：标准 SHA-256 向量、同句柄包体摘要、三类兼容失败、canonical origin、首次授权及升级新增/移除权限测试通过。
- [x] `M1-08`：实现插件包校验、权限确认、隔离 staging、原子目录提交与状态写入。
- [x] `M1-08` 验收：同 ID/version 不可变、重复安装幂等、staging 复检、权限拒绝零持久变更、状态失败目录回滚及 CLI 安装入口测试通过。
- [x] `M1-09`：实现已安装插件列表、精确或最高语义版本启用及无损停用。
- [x] `M1-09` 验收：schema v2→v3、激活外键、幂等启停、版本切换、启用前包体复检、未知版本拒绝和 CLI 入口测试通过。
- [x] `M1-10`：实现精确版本软卸载、recoverable trash 列表与原路径恢复。
- [x] `M1-10` 验收：schema v3→v4、活动版本保护、目录双向移动、数据库原子状态转换、重复卸载/恢复幂等、包体复检及插件数据保留测试通过。
- [x] `M1-11`：实现持久文件操作 journal、自动/显式中断恢复与安全歧义拒绝。
- [x] `M1-11` 验收：schema v4→v5、journal 原子完成/取消、install rename 前后、trash/restore rename 后故障注入、双路径歧义和包体复检测试通过。
- [x] `M1-12`：统一 CLI JSON success/error envelope、错误阶段、稳定错误码与退出码。
- [x] `M1-12` 验收：成功/失败 envelope、真实 CLI 子进程、工作区失败退出码、锁争用分类和人类输出回归测试通过。

- [x] M1 阶段 Gate：插件生命周期完整；无效输入不产生持久变更；并发操作由工作区锁串行化；事务中断后可恢复一致状态。

## 4. 后续阶段

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

- Windows 平台适配和 PowerShell、CMD Shell 初始化。
- Shell 集成快照、幂等启停与安全恢复。
- 配置 Schema、差异、备份、恢复和 Windows 安全存储。
- 保留跨平台接口边界；macOS、Linux 的平台适配与 Shell 集成后置到 M8。

### M5：完整 GUI

- 设计系统、首次运行、软件首页、插件中心和版本管理。
- 配置、任务、诊断、设置、权限确认和管理等级。
- Windows 布局、缩放、主题、键盘和辅助功能验证；macOS、Linux GUI 验收后置到 M8。

### M6：仓库、发布与生产加固

- TUF 信任根、发布者身份、撤销、过期、回滚和密钥轮换。
- 重新评估 Windows CI/CD 的触发范围、Runner 成本、必需检查、Artifact 保留期和发布权限；取得明确确认后再建立自动化 workflow。
- Windows 签名与发布物验证。
- Windows 主程序原子替换、回滚、SBOM、来源证明和发布冒烟矩阵。
- macOS 签名与公证、Linux AppImage 签名及对应发布矩阵后置到 M8。

### M7：通用软件与工具生态

- 统一能力矩阵：发现、安装、升级、卸载、恢复、多版本、配置、启动、健康和数据管理。
- 便携软件、原生安装器、系统包管理器、服务型软件和外部软件适配。
- 插件中心展示管理等级、权限、数据位置、卸载残留和恢复保证。
- 新软件类型只增加插件或平台适配器，不修改 GUI、CLI 和事务核心业务分支。

### M8：macOS 与 Linux 平台完善

- 审计 M2 至 M7 的平台差异，补齐 macOS ARM64/x64 与 Linux x64 原生实现，不以交叉编译替代原生验证。
- 恢复并扩展 macOS、Linux 原生 CI，将核心、插件、工作区、事务、GUI 和发布验证重新纳入平台 Gate。
- 完成 macOS Shell 与安全存储、Linux Shell 与安全存储，以及两平台配置、目录链接、文件锁、子进程和恢复语义适配。
- 完成 macOS 签名与公证、Linux AppImage 签名、主发布物替换和跨版本原工作区复用验证。
- 在具备对应人工验收条件后，执行布局、缩放、主题、键盘、辅助功能和安装升级验收，再恢复对外跨平台支持承诺。

## 5. 下一执行队列

1. 按 Windows-first 策略执行 `M2-01`，先定义不执行任意代码的 recipe、来源、artifact 完整性和安装计划值对象。
2. 在新增 HTTP、归档或签名依赖前完成 M2-D1 精确版本决策并单独确认。
3. M2 至 M5 不建设 CI/CD；验证在 Windows 本地执行，同时保持核心平台中立，并将发现的非 Windows 平台差异登记到 M8。

## 6. 决策队列

- [x] M1-D1：rusqlite 0.40.2，关闭默认 feature，仅启用 bundled；静态编译 SQLite 3.53.2。
- [x] M1-D2：工作区写操作采用跨平台独占文件锁；初始化采用同级 staging 后目录重命名；目录链接保留为后续软件激活策略决策。
- M1-D3：trash 默认保留周期与可配置边界。
- M2-D1：HTTP、哈希、签名和归档 crate 的精确版本。
- M2-D2：第一个首方真实软件插件。
- M4-D1：Python Windows 供应链方案；macOS、Linux 供应链后置到 M8。
- M6-D1：恢复 Windows CI/CD 的成熟度标准、触发范围、Runner 成本预算、必需检查和 Artifact 保留策略。
- M8-D1：恢复 macOS、Linux 支持时的目标版本、Runner、人工验收环境和 CI 必需检查范围。

上述事项涉及新依赖、下载、系统环境或外部服务时，实施前单独确认。
