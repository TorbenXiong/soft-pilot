# SoftPilot 项目开发约定

## 1. 项目定位与当前范围

SoftPilot 是跨平台的软件与工具插件化管理平台。唯一维护主线采用 Rust、Slint 和 Wasmtime Component Model，目标平台为 Windows、macOS 和主流 glibc Linux，目标架构为 x86-64 与 ARM64。

当前阶段聚焦平台核心：插件规范、安全边界、工作区、生命周期事务和三平台可发布性。正式实现前按任务需要阅读：

- `README.md`：当前能力、目录与验证入口。
- `docs/architecture.md`：产品边界、分层、权限、事务和工作区设计。
- `docs/implementation-plan.md`：当前任务、Gate 和决策队列。
- `docs/adr/0001-rust-wasm-plugin-platform.md`：技术栈与插件隔离决策。

## 2. 项目结构与依赖方向

- `apps/softpilot-gui`：Slint GUI 和平台探针。
- `apps/spt`：`spt` CLI。
- `crates/softpilot-core`：无 GUI、存储或平台实现依赖的核心值对象与用例模型。
- `crates/softpilot-plugin-api`：Manifest、插件包、WIT、Component 校验与受限调用。
- `fixtures`：测试插件，不是生产插件仓库。
- `specs/plugin`：公开 Manifest Schema 与 WIT ABI。
- `eng`：专项验证脚本。

依赖方向保持为入口/基础设施 → 用例与核心模型。GUI 只调用宿主用例，不直接访问 SQLite、Wasmtime 或平台 API。核心不得包含具体软件名称分支；Node.js、Temurin、Python 等均应通过与第三方相同的插件机制接入。

## 3. 插件与宿主安全边界

- 插件默认没有通用文件系统、网络、环境变量或进程权限。
- 声明式 recipe 不执行任意代码；Wasm 插件只通过版本化 WIT 调用宿主能力。
- 插件不得直接执行任意 Shell、修改 PATH/Shell profile、注册表、快捷方式或系统服务。
- 宿主只执行经过结构化校验且已明确授权的计划。
- TLS、来源、哈希、签名、健康检查或事务提交任一失败即终止，不得增加跳过安全校验的兼容开关。
- Component 调用必须保留 imports 校验、fuel、内存和 deadline 限制；插件失败不得破坏宿主状态。
- 原生适配器必须独立进程、单独签名并经过额外威胁模型；在协议稳定前不向第三方开放。

## 4. 工作区与数据保护

- 用户首次运行时显式选择工作区；后续自动定位，不擅自迁移。
- 主发布物与工作区解耦；覆盖升级不得清空插件、配置、软件实例或日志。
- 安装必须经过 cache、完整性验证、独立 staging、健康检查和原子提交。
- 安装、切换、配置、卸载和恢复必须使用跨进程工作区锁。
- 软件与插件删除默认进入 `trash`，保留期内可恢复；不得改成直接递归删除。
- 不得删除或修改 SoftPilot 无法证明归属的外部软件及数据。
- Shell 集成必须由用户显式启用，停用时只移除 SoftPilot 管理的内容。

## 5. 依赖、生成内容与兼容性

- Rust 工具链固定在 `rust-toolchain.toml`；依赖在根 `Cargo.toml` 精确约束并由 `Cargo.lock` 固定。
- 未经确认不得新增、升级、降级或重新解析依赖，也不得隐式触发工具链、target 或系统包下载。
- 依赖已缓存时优先使用 `--locked --offline` 验证。
- 不直接修改 `target` 或其他生成内容；Wasm fixture 产物不提交。
- 修改 Manifest、WIT、序列化、SQLite schema、工作区布局或公开 CLI JSON 前，先设计兼容与迁移方案。
- 修改插件 ABI 时同步检查 specs、Rust bindings、fixture、CLI、GUI 和兼容性测试。

## 6. 构建与验证

基础验证：

```powershell
cargo fmt --all -- --check
cargo check --workspace --all-targets --locked
cargo clippy --workspace --all-targets --locked -- -D warnings
cargo test --workspace --all-targets --locked
```

专项验证：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\test-lifecycle-component.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\test-windows-spike.ps1
pwsh -NoProfile -File ./eng/test-platform-spike.ps1
```

- 三个平台的“已验证”只接受对应原生环境运行结果，不以交叉编译代替。
- GUI 修改需要构建并检查受影响窗口、缩放、键盘、辅助功能与错误状态。
- 插件包修改需要覆盖路径穿越、重复项、特殊项、数量和解压尺寸边界。
- Component 修改需要覆盖畸形结构、错误接口、额外 imports、trap、fuel 耗尽和内存上限。
- 无法执行的验证必须记录原因和风险，不得声称通过。

## 7. 变更一致性

- 新增 CLI 能力时同步检查帮助文本、`--json`、GUI 入口、README 和实施计划。
- 修改工作区或安装布局时同步检查锁、事务恢复、trash、shim、Shell 和升级替换。
- 修复单一平台问题时检查其他平台是否存在同类缺陷，但不进行无关重构。
- 用户可见错误应包含失败阶段和可行动建议；安全失败不得降级为警告后继续。
