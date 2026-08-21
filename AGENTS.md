# SoftPilot 项目开发约定

## 1. 项目定位与当前范围

SoftPilot 是面向 Windows 的开发运行时生命周期管理器。当前版本为 `0.0.9`，V1 聚焦：

- Node.js 官方 Windows x64 ZIP。
- Eclipse Temurin HotSpot JDK Windows x64 ZIP。
- CPython 官方 Python Install Manager。
- Redis 官方版本目录与 `redis-windows/redis-windows` Windows x64 MSYS2 社区构建，仅用于本地开发。
- Oracle MySQL Community Server 官方 Windows x64 ZIP：8.4 LTS 推荐线与最终 5.7.44 兼容线，仅用于本地开发。
- Git for Windows 官方最新版 x64 PortableGit，采用单一受管副本，不进入多版本或终端默认版本模型。
- 官方版本发现、多版本安装、终端默认版本切换、外部运行时只读发现和永久卸载；Redis 额外支持单实例启动、停止和状态检查，MySQL 支持按版本线多实例启动、停止和状态检查。
- Node.js 与 Eclipse Temurin 归档默认在官方源与清华 TUNA 镜像间智能选择；版本元数据和完整性信任链仍只使用官方来源。
- WinUI 3 GUI、`spt` CLI、shim、单文件 EXE 发布和首次启动自迁移。

除上述 Redis 单实例、MySQL 按版本线隔离的本地开发多实例和 Git 便携工具外，V1 不包含项目级版本绑定、自定义源、其他第三方镜像、其他数据库服务、Docker、AI CLI、普通软件和跨平台实现。Redis/MySQL V1 不注册 Windows Service、不设置开机启动。MySQL V1 不包含数据库/用户管理、备份恢复或跨大版本自动迁移。Git V1 只管理最新版便携副本，不修改用户 PATH、不接管外部 Git 安装，也不自动改写全局 `.gitconfig`。不要在无明确需求时提前引入这些范围。

开始修改前按任务需要阅读：

- `README.md`：安装、使用和产品边界。
- `docs/architecture.md`：分层、工作区、安装事务和 Shell 行为。
- `docs/adr/0002-portable-self-migration.md`：便携式发布和首次启动自迁移决策。

## 2. 项目结构与职责

- `SoftPilot.Domain`：无基础设施依赖的核心模型。
- `SoftPilot.Application`：用例、选择策略和抽象接口。
- `SoftPilot.Infrastructure`：Windows、SQLite、网络、Provider、安装、切换、Shell 和诊断实现。
- `SoftPilot.Cli`：`spt` 命令入口。
- `SoftPilot.Shim`：`node`、`npm`、`npx`、Java、Python、Redis 和 MySQL 命令转发。
- `SoftPilot.Gui`：WinUI 3 界面、首次启动位置选择和应用本体自迁移。
- `SoftPilot.Tests`：MSTest 自动化测试。

依赖方向保持为入口项目/Infrastructure → Application → Domain。不要让 Domain 或 Application 依赖 GUI、CLI 或具体 Windows 基础设施。

## 3. 工作区与用户数据保护

SoftPilot 应用根目录只包含可替换的 `SoftPilot.exe` 和独立的 `SoftPilotData` 管理目录。

- 首次迁移和升级只管理根目录中的 `SoftPilot.exe`，不得修改或删除 `SoftPilotData`。
- 应用管理 `SoftPilotData` 下的运行时、工具、状态、缓存、暂存和日志。
- 源目录迁移清理只能删除已验证与目标相同的源 EXE，必须保留无关文件。
- 不得直接安装到 `app`：必须经过 cache、完整性验证、staging、健康检查，再原子移动到最终版本目录。
- 安装、切换和卸载必须使用工作区跨进程锁，避免 GUI 与 CLI 并发修改状态。
- 终端默认版本不得直接卸载；先切换或清除当前选择。
- 卸载是永久操作：先移动到 staging，状态删除成功后再物理删除；失败时恢复目录和状态。
- Redis 卸载默认保留按版本隔离的数据和日志；只有用户明确选择删除数据时，才把对应数据和日志目录纳入同一 staging 卸载事务并支持失败回滚。
- MySQL 卸载默认保留按 `major.minor` 版本线隔离的数据、配置、DPAPI 凭据和日志；只有用户明确选择删除数据时，才把整条版本线纳入同一 staging 卸载事务并支持失败回滚。
- 应用根目录记录在 `HKCU\Software\SoftPilot\Root`。V1 不支持首次指定后的管理目录迁移。

应用根目录解析必须保持大小写敏感的 ordinal 规则：只有规范化后末级名称精确等于 `SoftPilot` 时才不追加目录名。

## 4. Provider 与供应链安全

Provider 只能使用当前范围内的官方元数据和官方发布资产。Node.js 与 Eclipse Temurin 的相同归档默认在官方源与内置清华 TUNA 镜像间智能选择：

- Node.js：官方 `index.json`、Windows x64 ZIP，以及签名的 `SHASUMS256` 清单；归档可使用清华 TUNA 的 `nodejs-release` 镜像。
- Java：Adoptium 官方 LTS 元数据和 Eclipse Temurin Windows x64 JDK；校验哈希和签名；归档可使用清华 TUNA 的 Adoptium 镜像。
- Python：官方 Python Install Manager，并通过 `--target` 安装到 SoftPilot 工作区。
- Redis：版本必须同时存在于 Redis 官方 GitHub Releases；归档固定来自 `redis-windows/redis-windows` 的 Windows x64 MSYS2 GitHub Release Asset，并校验 GitHub 提供的 SHA-256 digest。该社区构建仅用于本地开发，用户界面和文档不得描述为 Redis 官方 Windows 发行版。
- MySQL：只接受内置受支持目录中的 Oracle 官方 Windows x64 ZIP，当前为 8.4 LTS 与最终 5.7.44；归档固定来自 `cdn.mysql.com`，并使用 `repo.mysql.com` 官方公钥和固定主密钥指纹验证同名 `.asc` 分离签名。
- MySQL 安装前检测 HKLM 中 Microsoft Visual C++ x64 v14 Runtime；低于 `14.29.30157` 或缺失时，只允许从 Microsoft 官方 `aka.ms/vc14/vc_redist.x64.exe` 下载，必须验证有效 Authenticode 签名和 Microsoft Corporation 发布者后再通过 UAC 安装。该系统共享组件不随 MySQL 卸载或事务失败回滚；需要重启时中止 MySQL 安装并提示重试。
- Git：只使用 `git-for-windows/git` 官方最新稳定 Release 的 `PortableGit-*-64-bit.7z.exe`，必须校验 GitHub 提供的 SHA-256 digest，并在 staging 中解包和核对 `git --version` 后才可替换 `app\git` 唯一受管目录。

必须遵守：

- TLS、官方元数据、哈希、签名或健康检查任一失败即终止安装。
- 不允许加入跳过 TLS、忽略哈希、忽略签名或吞掉健康检查失败的兼容开关。
- Node.js 与 Temurin 默认自动探测官方源与内置清华 TUNA 归档源；Redis 只使用上述固定社区构建源；MySQL 只使用 Oracle 官方源；不接受其他镜像或自定义源。
- 网络错误可在内置来源间回退；哈希或签名失败必须立即终止。
- 下载内容进入 `cache\downloads`，解包或安装只发生在独立 staging 目录。
- 健康检查确认实际版本后才能写入最终目录和 SQLite。
- Provider 返回的版本必须是可复现的确定版本；别名应先解析成确定版本，再进入安装或切换事务。

## 5. 全局切换与 Shell 集成

- Shell 集成由终端默认版本选择自动管理，不提供独立开关。
- 用户 PATH 前部顺序固定为 `<SoftPilotRoot>\SoftPilotData\tools\shims`、`<SoftPilotRoot>\SoftPilotData\current\node`，随后才是用户原 PATH。
- `JAVA_HOME` 指向 `<SoftPilotRoot>\SoftPilotData\current\java`；Python 不设置 `PYTHONHOME`。
- 启用前保存原 PATH 和 `JAVA_HOME`，禁用时安全恢复快照。
- 版本切换只更新 `current\<kind>` 链接，不重写 PATH。
- 链接替换后必须通过对应 Provider 重新执行健康检查并核对实际版本；失败时恢复旧链接和状态。
- 修改 shim 时同时检查便携打包脚本中的 shim 别名创建逻辑、Shell PATH 行为和 `spt doctor` 诊断。
- Node.js 必须保证 `node`、`npm`、`npx` 可用，并让当前版本的全局 npm/Corepack 命令可以从 `current\node` 解析。
- Redis 必须保证 `redis-server`、`redis-cli` 可用。选择 `current\redis` 不得自动启动服务；服务配置、数据和日志按完整版本隔离，默认仅绑定 `127.0.0.1:6379`。
- Redis 停止优先使用 `redis-cli SHUTDOWN`；兜底终止前必须同时验证 PID、可执行文件绝对路径和启动时间，不得按进程名批量终止。
- MySQL 必须保证 `mysqld`、`mysql`、`mysqladmin` 可用。选择 `current\mysql` 不得自动启动服务；8.4 默认绑定 `127.0.0.1:3306`，5.7 默认绑定 `127.0.0.1:3307`，端口可按版本线修改但不得重复，数据目录与进程状态同样按版本线隔离并支持并行运行。首次初始化密码必须在禁用 TCP 的通道完成，持久凭据必须使用当前 Windows 用户 DPAPI 保护，客户端命令行不得包含密码。停止优先使用 `mysqladmin shutdown`；兜底终止前必须同时验证目标版本的 PID、可执行文件绝对路径和启动时间。

## 6. 依赖、版本和生成内容

- 依赖版本集中维护在 `Directory.Packages.props`，保留各项目的 `packages.lock.json`。
- 项目版本以 `Directory.Build.props` 为主；发布版本变化时同步检查 `eng/package.ps1` 默认值和发布元数据。
- 不直接修改 `bin`、`obj`、`artifacts`、`TestResults` 或其他生成内容。
- 不重新引入已放弃的安装器或卸载器项目；当前发布方案是单个便携 EXE 与应用首次启动自迁移。
- 不在没有明确必要时新增依赖；优先复用 .NET、Windows API 和项目现有组件。

## 7. 构建与验证

原生 Windows PowerShell 环境使用仓库锁文件：

```powershell
dotnet restore SoftPilot.slnx --locked-mode
dotnet build SoftPilot.slnx -c Release --no-restore
dotnet test SoftPilot.slnx -c Release --no-restore
dotnet format SoftPilot.slnx --verify-no-changes --no-restore
```

依赖已存在时，普通修改优先执行 `--no-restore` 命令，避免验证过程隐式重新解析或下载依赖。

按修改范围执行最低验证：

- Domain/Application/Infrastructure：相关单元测试，再执行完整 Release 测试。
- GUI/XAML：完整构建，并人工检查受影响页面的布局、交互和错误状态。
- Provider：解析测试、健康检查相关测试，并在条件允许时只读验证官方目录。
- 安装、切换、卸载、Shell、SQLite：覆盖成功、失败回滚、并发和路径冲突。
- 便携发布流程：运行 `eng/package.ps1`，核对单文件 EXE、EXE SHA-256、内嵌工具完整性和 Authenticode 状态。

开发包命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\package.ps1 -Version 0.0.9
```

未提供证书指纹时生成的是未签名开发构建，不得描述为已签名发布版本。打包不等于首次启动；除非任务明确要求，不要自动运行生成的便携应用或修改用户环境。

## 8. 变更一致性检查

- 新增 CLI 能力时同步检查帮助文本、README/架构文档、GUI 对应入口和 `--json` 输出。
- 修改运行时模型、SQLite 状态或序列化格式前，先评估兼容性和迁移方案。
- 修改便携布局时同步检查首次启动迁移、源文件清理、打包、shim、Root 注册表、Doctor 和升级行为。
- 修复单一运行时问题时检查 Node.js、Java、Python 是否存在同类缺陷，但不要无关重构。
- 用户可见错误应说明失败阶段和可行动建议；安全校验失败不得降级为警告后继续。
