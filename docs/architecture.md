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

`softpilot-storage` 是基础设施层：依赖核心 workspace/host 值对象，但不依赖 GUI、CLI 或 Wasmtime。它使用精确锁定的 rusqlite 0.40.2 `bundled` feature，静态编译 SQLite 3.53.2，避免运行时依赖目标系统的 SQLite 版本或动态库。

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

本地包检查也必须从同一个锁定文件句柄流式计算完整文件 SHA-256，并在解析 ZIP 前固定包体大小；摘要采用 64 位小写十六进制。该本地摘要只能证明后续 staging/提交使用了相同字节，不能替代仓库元数据、发布者签名或来源证明。

ZIP 中的所有路径使用 ASCII 正斜杠相对格式，并按 ASCII 大小写折叠后保持唯一，以保证 Windows、默认 macOS 和 Linux 解包结果一致。普通文件不能同时作为另一项的目录前缀；符号链接、设备、FIFO、socket 等特殊项一律拒绝。Manifest 声明的 recipe、Component 和 assets 必须真实存在，检查过程只做有界读取，不提取或执行包内内容。

插件标识采用反向域名格式，例如 `org.nodejs.node`。插件版本与插件 API 版本相互独立。

## 6. 权限模型

权限在安装插件时授予，并在插件升级扩大权限时重新确认。
权限身份使用 canonical HTTPS origin 或枚举化的 process/Shell/OS grant。diff 同时返回新增与移除项；只有新增项扩大权限并要求明确确认，单纯移除权限不得被误报为扩权。

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

插件默认没有通用 WASI 文件系统、网络、环境变量或进程权限。Component 在实例化前必须通过版本化 lifecycle WIT 类型校验且 imports 为空。宿主通过自定义 WIT 传入必要数据，
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
├─ workspace.lock
├─ workspace.lock.owner.json   # 仅在锁持有期间存在的诊断 sidecar
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
   ├─ packages/<host-triple>/
   ├─ active/
   ├─ data/
   ├─ staging/
   └─ trash/
```

数据库保存相对工作区路径。`workspace.json` 包含格式版本、随机 workspace ID 和创建时间。
不同 host triple 的软件实例和激活状态相互隔离；插件包和不含宿主二进制的插件数据可以共享。

当前 `workspace.json` v1 契约为：

```json
{
  "layoutVersion": 1,
  "workspaceId": "00000000-0000-4000-8000-000000000000",
  "createdAtUnixSeconds": 0
}
```

`workspaceId` 在初始化时由操作系统随机源生成 UUID v4，之后保持不变；创建时间使用 Unix
秒避免绑定日期时间库和本地时区。元数据拒绝未知字段，`layoutVersion` 非当前版本时只读检查
也会失败，不进行隐式迁移。

`hosts/<host-triple>` 使用稳定的 Rust target-triple 名称，例如 `x86_64-pc-windows-msvc`、
`aarch64-apple-darwin` 和 `x86_64-unknown-linux-gnu`。当前工作区布局版本为 `1`；宿主读取到
其他版本时必须在写入前停止并要求显式迁移，不得把未知旧版或新版布局当作当前版本继续使用。

工作区根路径在进入用例前转换为核心值对象：必须是非文件系统根的绝对路径，拒绝 `..`，
并只进行不访问磁盘的词法规范化。目录存在性、链接解析、创建和权限检查由工作区用例负责，
避免核心值对象隐式访问文件系统或擅自改变用户选择的位置。

`workspace.lock` 是工作区生命周期内保持路径稳定的普通文件。所有会改变工作区状态的宿主用例必须先取得该文件的独占跨进程锁，并在单调时钟截止时间前按固定间隔重试。操作名限制为 1--128 个非控制 UTF-8 字节；锁持有者以 UUID、PID、操作名和 Unix 秒获取时间写入 `workspace.lock.owner.json`，供其他进程在超时时诊断。Windows 对已独占文件的共享读取约束不同，因此诊断元数据不写入锁文件本身。sidecar 不承担所有权判断，真实互斥只由操作系统文件锁保证。

新工作区先在目标同级的隔离 staging 目录创建完整布局，再以目录重命名提交；并发初始化者在目标已由另一个进程提交时重新打开并验证目标。已有工作区必须先验证 `workspace.json` 兼容性，再获取工作区锁，之后才能补齐当前 host 目录或执行其他写操作。锁超时、未知布局、非法锁文件和无效操作名均不得产生后续持久变更。

每个 host 的 `data/state.db` 独立绑定 `workspaceId`、host triple 和 workspace 创建时间。schema v1 包含只追加的 `schema_migrations` 历史和单例 `host_identity`；schema v2 增加 `plugin_packages`，以 plugin ID/version 为不可变主键，记录完整包 SHA-256、大小、工作区相对路径、已验证 Manifest、Component 校验结果和安装时间；schema v3 增加 `active_plugins`，通过外键保证一个插件只能指向一个实际已安装版本；schema v4 增加 `trashed_plugin_packages`，保留原始不可变记录、唯一 trash ID、trash 相对路径和软删除时间；schema v5 增加 `plugin_file_operations`，在 install/trash/restore 的目录重命名前持久化源、目标与不可变包元数据。SQLite header 同时记录 SoftPilot application ID 与 `PRAGMA user_version`。application ID 为空但已有用户对象、其他 application ID、未来 schema、迁移历史不匹配或 identity 不一致均直接拒绝。

迁移列表必须从 1 连续到当前版本。新建、逐版本 DDL、迁移历史和 identity 绑定在同一个 `BEGIN IMMEDIATE` 事务内提交；失败依赖 SQLite 事务整体回滚。成功验证后启用 WAL、`synchronous=FULL`、foreign keys 和 `trusted_schema=OFF`。工作区锁是所有状态写操作的外层跨进程边界，数据库事务是单次状态修改的内层回滚边界，二者不可互相替代。

插件安装先只读检查源包的 ZIP 边界、Manifest、兼容性、权限与可选 Component，再取得工作区锁。新增权限未显式确认时不得创建 staging 或改变状态。包体复制到 `plugins/staging/install-<uuid>/package.softpilot-plugin` 后刷新到磁盘并完整复检；只有两次摘要、大小、Manifest 与 Component 结果一致时，才把整个版本目录原子重命名到 `plugins/packages/<host-triple>/<plugin-id>/<version>/`。插件包状态位于对应 host 的数据库，因此包体目录也按相同 triple 隔离，避免跨平台复用工作区时一个 host 的卸载破坏其他 host 的引用。最终目录与数据库采用“文件系统先提交、状态后提交”；状态失败时把目录移回受控 staging 并清理。

插件启用状态是当前 host 数据库中的单一版本引用，不创建目录链接，也不改变不可变包目录。未指定版本时由宿主解析已验证 Manifest 并选择最高语义版本。启用前重新检查最终包体、摘要、Manifest、Component 与当前兼容性；检查失败保持原激活状态。停用只删除激活引用，保留全部已安装版本和插件数据。

插件卸载只接受精确版本，并要求该版本已停用。宿主复检包体后把整个版本目录重命名到 `plugins/trash/<uuid>/`，再在单个数据库事务中把不可变记录从 installed 转为 trashed；状态失败会把目录移回原位。恢复执行逆向复检、目录重命名与状态转换，恢复后保持停用。`plugins/data/<plugin-id>` 不参与包体卸载，永久清理与 trash 保留期在 M1-D3 决策前不实施。

每个插件目录重命名采用 journal → rename → 状态转换与 journal 删除同一事务的顺序。持锁恢复时，只有源目录单独存在表示 rename 未发生，只有目标目录单独存在表示 rename 已发生；两边都存在或都缺失属于歧义状态，必须停止并保留 journal 供诊断。任何自动完成前都会重新验证 journal 的 UUID、固定相对路径、包体摘要、Manifest 与 Component 结果，不接受任意路径或弱化校验。所有后续插件写操作都会先恢复未完成 journal，也可显式执行 `spt plugin recover`。

CLI 机器输出统一使用带 `ok` 判别字段的 JSON envelope。错误包含稳定 `code`、发生用例的 `stage` 和可行动 `message`，并按工作区、无效输入、兼容性、权限、生命周期冲突、不安全状态、锁争用、内部失败和输出失败映射独立退出码。人类输出继续使用 stderr 错误行；参数解析错误由 Clap 保持退出码 2。

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

发布物旁的便携定位文件名为 `softpilot-workspace.json`，用户 bootstrap 使用相同的 v1 指针结构：

```json
{
  "formatVersion": 1,
  "workspace": "/absolute/or/portable-relative/path"
}
```

便携定位文件允许相对自身目录的路径，解析并 canonicalize 后才进入核心值对象；用户 bootstrap
必须保存绝对路径。用户 bootstrap 位置为 Windows `%APPDATA%\SoftPilot\bootstrap.json`、
macOS `$HOME/Library/Application Support/SoftPilot/bootstrap.json`、Linux
`$XDG_CONFIG_HOME/softpilot/bootstrap.json`（未设置 XDG 时使用 `$HOME/.config`）。

## 11. 兼容性规则

- 插件 API 使用语义化版本；主版本不兼容，次版本只能增加可选能力。
- 插件包声明宿主版本范围、支持的 OS、架构和 libc。
- 已安装插件升级失败时保留旧版本和旧权限。
- 缓存的 Wasm 预编译产物仅是可删除缓存，不得作为唯一插件副本。
- 未知权限、未知操作步骤或未知完整性算法必须拒绝，不能忽略后继续。
