# SoftPilot 插件平台架构

状态：设计基线

## 1. 产品目标

SoftPilot 是跨平台软件生命周期宿主，而不是某一种语言运行时的专用管理器。
在用户界面中，每种软件表现为一个可安装的模块；在实现中，需要区分：

- **管理插件**：描述如何发现、安装、配置和诊断某种软件。
- **软件实例**：管理插件安装或接管的具体软件版本。
- **宿主能力**：由 SoftPilot 实现并受权限控制的网络、文件、进程、Shell 和系统集成操作。

首版目标平台为 Windows、macOS 和主流 glibc Linux，目标架构为 x86-64 和 ARM64。
第一批功能聚焦工作区内的便携软件和开发运行时。

## 2. 产品边界

### 2.1 首版包含

- 首次运行选择工作区，后续自动定位。
- 插件的发现、安装、权限确认、升级、禁用、卸载和恢复。
- 软件目录发现、精确版本解析、安装、升级、配置、激活、卸载和恢复。
- 工作区跨进程锁、崩溃恢复、任务进度、诊断和操作日志。
- 显式 Shell 集成、shim、环境变量和当前版本切换。
- 上游 TLS、哈希、签名和健康检查失败即终止。
- GUI 与 `spt` CLI 的功能一致性及机器可读输出。

### 2.2 首版不包含

- 驱动、内核扩展和需要重启的系统组件。
- 任意第三方原生动态库插件。
- 插件直接执行任意 Shell 脚本。
- 常驻后台服务、无人值守系统级提权和远程设备管理。
- 对所有 Linux 发行版和 libc 实现作出兼容承诺。

### 2.3 长期产品边界

SoftPilot 的最终目标是让用户把各类软件和工具都作为插件化条目统一发现、安装、卸载、升级、配置、启动和诊断。统一的是入口、状态、权限、审计和交互模型，不虚构所有软件都具备相同的可管理性：

- 便携软件、开发工具和运行时优先实现工作区级完整托管。
- MSI/EXE、pkg/dmg、deb/rpm 等原生安装器按实际静默安装、升级、卸载和回滚能力提供部分托管。
- 系统包管理器、应用商店、容器和远程服务通过受控桥接插件呈现，实际生命周期仍由对应平台承担。
- 驱动、内核扩展、需要重启或交互授权的软件，以及无法可靠卸载的既有软件，必须降级为辅助安装或只读发现，不得宣称完整托管。

每个插件和软件实例都必须声明第 9 节的管理等级及逐项能力。GUI 与 CLI 只展示宿主已经验证的能力，不根据软件类别猜测，也不把“可调用上游卸载器”等同于“可原子回滚”。

## 3. 逻辑架构

```text
SoftPilot GUI ─┐
               ├─> Host API ─> Use Cases ─> Transaction Engine
spt CLI ───────┘       │              │              │
                       │              │              ├─ Workspace / State
                       │              │              ├─ Download / Verify
                       │              │              ├─ Archive / Process
                       │              │              └─ Shell / OS Integration
                       │              │
                       │              └─ Plugin Runtime ─> Recipe / Wasm Component
                       │
                       └─ Read Models / Progress / Diagnostics
```

宿主核心不引用具体软件名称。Node.js、Temurin、Python 等均通过首方插件实现，并使用与第三方插件相同的公开 ABI。

## 4. 建议的 Rust workspace

```text
soft-pilot/
├─ Cargo.toml
├─ crates/
│  ├─ softpilot-core/          # 领域模型、用例和策略
│  ├─ softpilot-plugin-api/    # WIT 生成类型和 ABI 兼容层
│  ├─ softpilot-plugin-host/   # Wasmtime、权限和资源限制
│  ├─ softpilot-engine/        # 事务、任务、锁和崩溃恢复
│  ├─ softpilot-storage/       # SQLite 和迁移
│  ├─ softpilot-platform/      # 平台能力 trait
│  ├─ softpilot-platform-win/
│  ├─ softpilot-platform-macos/
│  └─ softpilot-platform-linux/
├─ apps/
│  ├─ softpilot-gui/           # Slint GUI
│  ├─ spt/                     # CLI
│  └─ softpilot-shim/          # 多调用名命令转发器
├─ plugins/
│  ├─ node/
│  ├─ temurin/
│  └─ python/
└─ specs/
   ├─ wit/
   └─ schemas/
```

`softpilot-core`、`softpilot-engine` 和插件 API 不依赖 Slint 或具体操作系统。
GUI 只消费用例、只读模型和进度事件，不直接访问 SQLite、Wasmtime 或平台 API。

## 5. 插件包

插件包建议使用 `.softpilot-plugin` 扩展名。逻辑内容如下：

```text
plugin.json
recipe.json
component.wasm          # 可选
assets/icon.svg
locales/zh-CN.json
locales/en-US.json
licenses/
sbom.spdx.json
```

仓库分发时，包内容摘要和大小位于受信任元数据中；发布者签名作为独立验证材料保存，避免自引用签名。

插件标识采用反向域名格式，例如 `org.nodejs.node`。插件版本与插件 API 版本相互独立。

## 6. 权限模型

权限在安装插件时授予，并在插件升级扩大权限时重新确认。

首版权限集合：

| 权限 | 含义 |
|---|---|
| `network.catalog` | 访问清单中列出的目录域名 |
| `network.artifact` | 下载解析后仍满足域名策略的资产 |
| `process.staged` | 执行 staging 内由本事务产生的文件 |
| `process.installed` | 执行该插件管理的软件实例 |
| `shell.path` | 向显式启用的 Shell 集成贡献 PATH 项 |
| `shell.environment` | 管理声明过的环境变量 |
| `os.shortcut` | 创建或移除用户级快捷方式 |
| `os.elevation` | 请求一次性平台提权；首版第三方插件禁用 |

网络权限必须使用 HTTPS origin，而不是任意 URL 前缀。重定向后的 origin 也必须重新校验。

插件默认没有通用 WASI 文件系统、网络、环境变量或进程权限。宿主通过自定义 WIT 传入必要数据，
并对每次调用设置内存、fuel 和 deadline 限制。

## 7. 声明式计划与事务

插件输出逻辑计划，宿主完成解析和验证后才产生可执行计划。

```text
resolve exact version
    ↓
fetch catalog/artifacts into cache
    ↓
verify digest/signature/provenance
    ↓
prepare isolated staging directory
    ↓
extract or run constrained installer
    ↓
run health probe and verify actual version
    ↓
atomically commit immutable software instance
    ↓
update state and optional current link
    ↓
apply explicitly enabled shell integration
```

事务日志必须先于外部可见变更落盘。恢复逻辑根据阶段执行清理、回滚或继续提交，
不得仅依赖数据库事务掩盖已经发生的文件系统和系统环境修改。

配置修改也属于事务：读取、解析、生成差异、备份、原子写入、健康检查，失败时恢复备份。

## 8. 工作区

```text
<workspace>/
├─ workspace.json
├─ hosts/
│  └─ <host-triple>/
│     ├─ software/<plugin-id>/<version>/
│     ├─ current/<plugin-id>/
│     ├─ shims/
│     ├─ tools/
│     ├─ data/state.db
│     ├─ cache/catalog/
│     ├─ cache/artifacts/
│     ├─ staging/
│     ├─ trash/
│     └─ logs/
└─ plugins/
   ├─ packages/
   ├─ active/
   ├─ data/
   └─ trash/
```

数据库保存相对工作区路径。`workspace.json` 包含格式版本、随机 workspace ID 和创建时间。
不同 host triple 的软件实例和激活状态相互隔离；插件包和不含宿主二进制的插件数据可以共享。

## 9. 软件管理等级

| 等级 | 宿主保证 |
|---|---|
| Workspace managed | 完整 staging、原子提交、软删除和恢复 |
| User managed | 管理用户级文件、环境和快捷方式，按能力回滚 |
| System managed | 委托系统安装器，能力和回滚取决于上游 |
| External detected | 只读发现，除非用户显式选择接管 |

GUI 和 CLI 必须显示管理等级，不得把系统安装器描述成具备工作区级原子回滚。

## 10. 宿主发布和升级

宿主程序位于工作区之外。工作区定位顺序为：

1. CLI `--workspace`；
2. `SOFTPILOT_WORKSPACE`；
3. 发布物旁的便携定位文件；
4. 当前用户配置目录中的 bootstrap 文件；
5. 首次运行选择界面。

宿主升级不得隐式迁移到不可降级的工作区格式。数据库和工作区迁移必须具备版本前置检查、备份和恢复策略。

## 11. 兼容性规则

- 插件 API 使用语义化版本；主版本不兼容，次版本只能增加可选能力。
- 插件包声明宿主版本范围、支持的 OS、架构和 libc。
- 已安装插件升级失败时保留旧版本和旧权限。
- 缓存的 Wasm 预编译产物仅是可删除缓存，不得作为唯一插件副本。
- 未知权限、未知操作步骤或未知完整性算法必须拒绝，不能忽略后继续。
