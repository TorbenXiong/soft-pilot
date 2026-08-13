# SoftPilot 项目开发约定

## 1. 项目定位与当前范围

SoftPilot 是面向 Windows 的开发运行时生命周期管理器。当前版本为 `0.0.1`，V1 聚焦：

- Node.js 官方 Windows x64 ZIP。
- Eclipse Temurin HotSpot JDK Windows x64 ZIP。
- CPython 官方 Python Install Manager。
- 官方版本发现、多版本安装、全局切换、外部运行时只读发现、软删除与七日恢复。
- WinUI 3 GUI、`spt` CLI、shim、自研安装器和卸载器。

V1 不包含项目级版本绑定、镜像或自定义源、数据库服务、Docker、AI CLI、普通软件和跨平台实现。不要在无明确需求时提前引入这些范围。

开始修改前按任务需要阅读：

- `README.md`：产品边界、构建与打包入口。
- `docs/architecture.md`：分层、工作区、安装事务和 Shell 行为。
- `docs/adr/0001-self-contained-dotnet-installer.md`：自研安装器决策。

## 2. 项目结构与职责

- `SoftPilot.Domain`：无基础设施依赖的核心模型。
- `SoftPilot.Application`：用例、选择策略和抽象接口。
- `SoftPilot.Infrastructure`：Windows、SQLite、网络、Provider、安装、切换、Shell 和诊断实现。
- `SoftPilot.Gui`：WinUI 3 界面及 ViewModel。
- `SoftPilot.Cli`：`spt` 命令入口。
- `SoftPilot.Shim`：`node`、`npm`、`npx`、Java 和 Python 命令转发。
- `SoftPilot.Setup`：自包含 WPF 安装器。
- `SoftPilot.Uninstall`：自包含 WPF 卸载器。
- `SoftPilot.Tests`：MSTest 自动化测试。

依赖方向保持为入口项目/Infrastructure → Application → Domain。不要让 Domain 或 Application 依赖 GUI、CLI、安装器或具体 Windows 基础设施。

## 3. 工作区与用户数据保护

SoftPilot 工作区包含 `bin`、`app`、`current`、`data`、`cache`、`staging`、`trash` 和 `logs`。

- 安装器只管理并原子替换 `bin`，不得在覆盖升级时清空根目录。
- 应用管理 `app`、`current`、`data`、`cache`、`staging`、`trash` 和 `logs`。
- 默认卸载保留运行时和数据；只有用户明确选择完整删除时才能移除整个工作区。
- 不得直接安装到 `app`：必须经过 cache、完整性验证、staging、健康检查，再原子移动到最终版本目录。
- 安装、切换、卸载和恢复必须使用工作区跨进程锁，避免 GUI 与 CLI 并发修改状态。
- 当前全局版本不得直接卸载；先切换或清除当前选择。
- 软删除进入 `trash`，七日内可恢复。不要把可恢复卸载改成直接递归删除。
- 根目录记录在 `HKCU\Software\SoftPilot\Root`。V1 不支持安装后的工作区迁移。

安装目录解析必须保持大小写敏感的 ordinal 规则：只有规范化后末级名称精确等于 `SoftPilot` 时才不追加目录名。

## 4. Provider 与供应链安全

Provider 只能使用当前范围内的官方元数据和官方发布资产：

- Node.js：官方 `index.json`、Windows x64 ZIP，以及签名的 `SHASUMS256` 清单。
- Java：Adoptium 官方 LTS 元数据和 Eclipse Temurin Windows x64 JDK；校验哈希和签名。
- Python：官方 Python Install Manager，并通过 `--target` 安装到 SoftPilot 工作区。

必须遵守：

- TLS、官方元数据、哈希、签名或健康检查任一失败即终止安装。
- 不允许加入跳过 TLS、忽略哈希、忽略签名或吞掉健康检查失败的兼容开关。
- 不要将镜像、自定义源或第三方下载地址作为隐式回退。
- 下载内容进入 `cache\downloads`，解包或安装只发生在独立 staging 目录。
- 健康检查确认实际版本后才能写入最终目录和 SQLite。
- Provider 返回的版本必须是可复现的确定版本；别名应先解析成确定版本，再进入安装或切换事务。

## 5. 全局切换与 Shell 集成

- Shell 集成必须由用户显式启用。
- 用户 PATH 前部顺序固定为 `bin\shims`、`current\node`，随后才是用户原 PATH。
- `JAVA_HOME` 指向 `current\java`；Python 不设置 `PYTHONHOME`。
- 启用前保存原 PATH 和 `JAVA_HOME`，禁用时安全恢复快照。
- 版本切换只更新 `current\<kind>` 链接，不重写 PATH。
- 链接替换后必须通过对应 Provider 重新执行健康检查并核对实际版本；失败时恢复旧链接和状态。
- 修改 shim 时同时检查安装器中的 shim 别名创建逻辑、Shell PATH 行为和 `spt doctor` 诊断。
- Node.js 必须保证 `node`、`npm`、`npx` 可用，并让当前版本的全局 npm/Corepack 命令可以从 `current\node` 解析。

## 6. 依赖、版本和生成内容

- 依赖版本集中维护在 `Directory.Packages.props`，保留各项目的 `packages.lock.json`。
- 项目版本以 `Directory.Build.props` 为主；发布版本变化时同步检查 `eng/package.ps1` 默认值、README 示例和安装包元数据。
- 不直接修改 `bin`、`obj`、`artifacts`、`TestResults` 或其他生成内容。
- 不重新引入已放弃的 WiX 安装器目录；当前安装方案是 `SoftPilot.Setup` 自研 .NET 安装器。
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
- 安装、切换、Shell、SQLite：覆盖成功、失败回滚、并发、路径冲突和恢复场景。
- 安装器或发布流程：运行 `eng/package.ps1`，核对输出文件、SHA-256 和 Authenticode 状态。

开发包命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\package.ps1 -Version 0.0.1
```

未提供证书指纹时生成的是未签名开发构建，不得描述为已签名发布版本。打包不等于安装；除非任务明确要求，不要自动运行生成的安装器或修改用户环境。

## 8. 变更一致性检查

- 新增 CLI 能力时同步检查帮助文本、README/架构文档、GUI 对应入口和 `--json` 输出。
- 修改运行时模型、SQLite 状态或序列化格式前，先评估兼容性和迁移方案。
- 修改安装布局时同步检查安装器、卸载器、shim、Root 注册表、Doctor 和升级回滚。
- 修复单一运行时问题时检查 Node.js、Java、Python 是否存在同类缺陷，但不要无关重构。
- 用户可见错误应说明失败阶段和可行动建议；安全校验失败不得降级为警告后继续。
