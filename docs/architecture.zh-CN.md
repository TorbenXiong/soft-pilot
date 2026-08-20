# SoftPilot V1 架构

[English](architecture.md) | **简体中文**

## 组件

- `Domain`：运行时和任务模型。
- `Application`：用例与基础设施抽象。
- `Infrastructure`：Provider、SQLite、下载、Windows 集成和事务。
- `Gui`：WinUI 界面、首次设置、自迁移和内嵌工具部署。
- `Cli` 与 `Shim`：命令行管理及终端默认版本转发。

依赖统一指向 `Application` 和 `Domain`；两者不依赖入口项目或 Windows 实现。

## 工作区

```text
<SoftPilotRoot>\
├─ SoftPilot.exe
└─ SoftPilotData\
   ├─ app\
   │  ├─ <kind>\<version>\
   │  └─ git\
   ├─ current\<kind>
   ├─ tools\shims\
   ├─ data\
   │  └─ redis\<version>\redis.conf 与数据库文件
   ├─ cache\downloads\
   ├─ staging\
   └─ logs\
```

根目录 EXE 可独立替换。运行时、工具和用户数据均保存在 `SoftPilotData`；IDE 可直接引用 `app` 下的具体版本。根目录记录在 `HKCU\Software\SoftPilot\Root`。

## 生命周期

- 首次启动校验所选本地 NTFS 路径，复制并校验 EXE 哈希，原子替换目标后重新启动，最后只删除已验证的源 EXE。
- 启动时根据清单校验并按需原子部署内嵌 CLI 与 shim。
- 运行时安装遵循 `cache → staging → 健康检查 → app → SQLite`；官方元数据、TLS、哈希、签名或健康检查失败都会终止事务。
- 所有模块共用 `cache\downloads`；SoftPilot 每次启动时通过统一缓存服务删除超过 30 天的文件和空目录，模块卸载不单独清理缓存。`spt cache clean` 保留为立即清空入口。
- Git 安装与升级遵循 `官方最新发布 → cache → SHA-256 校验 → staging → 版本健康检查 → app\git`；升级以可回滚方式替换唯一受管目录，卸载不会触碰统一下载缓存或其他 Git 安装。Git 页面仅在用户点击保存后，通过受管 Git 显式读写全局 `user.name` 与 `user.email`；卸载保留包括 `user.name`、`user.email` 在内的 Git 全局配置、SSH 密钥、凭据和仓库。
- 卸载先把运行时移入 staging，删除状态后再删除文件；失败时恢复目录和状态。
- GUI 与 CLI 的修改操作共用工作区跨进程锁。

Node.js 校验官方签名的校验清单；Temurin 校验 Adoptium 哈希和签名；Python 使用 python.org 官方目录与 Install Manager。Redis 版本必须存在于 Redis 官方发布目录中；Windows 归档来自 `redis-windows/redis-windows` GitHub Releases，并强制校验 GitHub Asset SHA-256 摘要。Git 只接受 `git-for-windows/git` 官方最新 Release 的 x64 PortableGit 自解压归档，并强制校验 GitHub Asset SHA-256 摘要。用户已有的 Python Install Manager 始终保留；缺少时，SoftPilot 校验、临时注册并在任务后移除官方包。

## 下载来源

Node.js 与 Temurin 默认并发探测内置官方源和清华 TUNA 归档源，每个来源最多读取 64 KiB、等待四秒。网络失败可回退；完整性失败立即终止且不回退。Python 始终使用官方来源。Redis 只使用固定的社区 Windows 归档源，每个版本都与 Redis 官方元数据交叉核对，不接受自定义源。Git 只使用官方来源，不提供来源或版本选择。

## Redis 服务

Redis 以单个本地前台进程运行，GUI 或 CLI 退出后进程继续存在。启动时创建按版本隔离的配置、数据和日志路径，并要求 `redis-cli PING` 成功、Redis 版本符合预期，且 Windows TCP 监听者 PID 与 SoftPilot 启动的进程一致；不得把 Redis 报告的 MSYS2 POSIX PID 与 Windows PID 直接比较。只有监听者 PID 匹配时才发送 `SHUTDOWN`；只有保存的 PID、可执行文件路径和进程启动时间全部匹配时，才允许执行兜底终止。SoftPilot 不注册 Windows Service，也不设置开机启动。卸载 Redis 运行时版本时默认保留数据和日志；GUI 确认框或 CLI `--delete-data` 可明确选择把它们纳入同一套可回滚卸载事务。

模块显示与排序会立即更新并串行保存，无需单独操作。

## 终端默认版本

首次选择终端默认版本时保存用户 `PATH` 和 `JAVA_HOME`，再配置 SoftPilot shim、Node.js `current` 和 Java `JAVA_HOME`；始终不设置 `PYTHONHOME`。Redis 通过 `current\redis` 提供 `redis-server` 与 `redis-cli` shim，选择版本不会自动启动服务。清除最后一个选择时恢复快照。切换只替换 `current\<kind>`，核对实际版本并在失败时回滚。变更对新打开的终端生效。
