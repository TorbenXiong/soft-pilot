# SoftPilot V1 架构

## 分层

- `SoftPilot.Domain`：运行时种类、版本、安装、任务等核心模型。
- `SoftPilot.Application`：Provider、下载、状态、全局切换、Shell、布局和操作协调接口。
- `SoftPilot.Infrastructure`：Windows 布局、注册表、SQLite、下载与校验、Provider、进程、链接和服务实现。
- `SoftPilot.Cli`：`spt` 命令入口。
- `SoftPilot.Shim`：根据可执行文件名转发到当前全局运行时。
- `SoftPilot.Gui`：WinUI 3 桌面界面。
- `SoftPilot.Setup`：自包含 WPF 单文件安装器。
- `SoftPilot.Uninstall`：自包含 WPF 单文件卸载器。

## 工作区

```text
<SoftPilotRoot>\
├─ bin\
│  ├─ SoftPilot.exe
│  ├─ spt.exe
│  ├─ SoftPilot.Uninstall.exe
│  └─ shims\
├─ app\
│  ├─ node\<version>\
│  ├─ java\<version>\
│  └─ python\<version>\
├─ current\
├─ data\
├─ cache\
│  ├─ catalog\
│  └─ downloads\
├─ staging\
├─ trash\
└─ logs\
```

安装器只原子替换 `bin`；应用负责其余工作区。`app\node`、`app\java`、`app\python` 在对应运行时首次成功安装提交时按需创建，不在初始化空工作区时预创建。根目录记录在 `HKCU\Software\SoftPilot\Root`。默认卸载删除程序文件并保留工作区，只有显式选择完整删除时才移除根目录。

## 安装事务

1. 将内嵌 ZIP 解压到 `.bin.incoming-<id>`，拒绝路径穿越。
2. 检查必需文件，并逐项验证 `payload.sha256`；文件集合必须完全一致。
3. 将旧 `bin` 移到 `.bin.previous-<id>`。
4. 将 incoming 原子移动为 `bin`。
5. 为 shim 创建 NTFS 硬链接，初始化工作区并注册 Apps & Features 和开始菜单。
6. 失败时恢复旧 `bin`；成功后清理 previous。

安装器外层 Authenticode 签名提供发布者身份与负载清单防篡改边界。SHA-256 清单同时用于发现介质损坏。

## 运行时安装事务

统一由操作协调器执行：下载到 cache、验证官方完整性、安装到 staging、健康检查、原子移动到 `app/<kind>/<version>`、写入 SQLite，并按用户选择更新 `current`。

- Node.js：校验官方签名的 `SHASUMS256.txt`。
- Temurin：校验 Adoptium SHA-256 与签名；当元数据指向 GitHub Release 时，通过 GitHub 官方 Releases API 解析同一资产，避免 `github.com` 下载端点的链路故障，不使用第三方镜像。
- Python：版本发现由 SoftPilot 直接读取 python.org 官方 Windows 索引，不依赖本机安装组件；安装时调用官方 Python Install Manager 并指定 `--target`。

元数据、TLS、哈希、签名或健康检查失败均终止安装。

官方版本目录按运行时缓存在 `cache\catalog`，有效期为 24 小时。GUI 启动时先读取本地安装状态和已有目录缓存并立即显示页面，再在后台刷新过期目录；用户显式点击“刷新”时绕过有效期重新请求官方目录。缓存只减少版本发现请求，不绕过运行时下载时的哈希、签名和健康检查。

## 终端集成

终端集成由当前版本选择自动管理。首次为任一运行时选择当前版本时，保存用户 PATH 与 `JAVA_HOME` 快照，将 `bin\shims` 和 `current\node` 依次插入用户 PATH 前部，并把 `JAVA_HOME` 指向 `current\java`；不设置 `PYTHONHOME`。清除最后一个当前版本时恢复快照。`current\node` 让当前 Node.js 版本的全局 npm/Corepack 命令可被 Shell 找到，版本切换只更新 `current` 链接。应用启动时会根据当前版本状态修复终端集成，GUI 不提供独立开关。

GUI、CLI 的安装、切换和卸载使用工作区文件锁跨进程串行化。运行时卸载时先原子移动到 staging，删除状态成功后再物理删除；失败时恢复目录和状态，不保留七日恢复入口。切换链接后必须重新执行 Provider 健康检查并核对实际版本，失败时恢复原链接和数据库状态。

## CLI

```text
spt runtime available [node|java|python] [--json]
spt runtime list [--managed|--external] [--json]
spt runtime install <kind>@<exact-version>
spt runtime install node@lts|latest|<major>
spt runtime uninstall <kind>@<exact-version>
spt use <kind>@<exact-version> --global
spt use node@lts|latest-installed|<major> --global
spt current [--json]
spt shell status
spt doctor [--json]
spt task list|show
spt cache status|clean
```
