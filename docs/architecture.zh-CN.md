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
   ├─ app\<kind>\<version>\
   ├─ current\<kind>
   ├─ tools\shims\
   ├─ data\
   ├─ cache\downloads\
   ├─ staging\
   └─ logs\
```

根目录 EXE 可独立替换。运行时、工具和用户数据均保存在 `SoftPilotData`；IDE 可直接引用 `app` 下的具体版本。根目录记录在 `HKCU\Software\SoftPilot\Root`。

## 生命周期

- 首次启动校验所选本地 NTFS 路径，复制并校验 EXE 哈希，原子替换目标后重新启动，最后只删除已验证的源 EXE。
- 启动时根据清单校验并按需原子部署内嵌 CLI 与 shim。
- 运行时安装遵循 `cache → staging → 健康检查 → app → SQLite`；官方元数据、TLS、哈希、签名或健康检查失败都会终止事务。
- 卸载先把运行时移入 staging，删除状态后再删除文件；失败时恢复目录和状态。
- GUI 与 CLI 的修改操作共用工作区跨进程锁。

Node.js 校验官方签名的校验清单；Temurin 校验 Adoptium 哈希和签名；Python 使用 python.org 官方目录与 Install Manager。用户已有的 Python Install Manager 始终保留；缺少时，SoftPilot 校验、临时注册并在任务后移除官方包。

## 下载来源

Node.js 与 Temurin 默认并发探测内置官方源和清华 TUNA 归档源，每个来源最多读取 64 KiB、等待四秒。网络失败可回退；完整性失败立即终止且不回退。版本目录、完整性数据和 Python 始终使用官方来源，也不接受自定义源。

模块显示与排序会立即更新并串行保存，无需单独操作。

## 终端默认版本

首次选择终端默认版本时保存用户 `PATH` 和 `JAVA_HOME`，再配置 SoftPilot shim、Node.js `current` 和 Java `JAVA_HOME`；始终不设置 `PYTHONHOME`。清除最后一个选择时恢复快照。切换只替换 `current\<kind>`，核对实际版本并在失败时回滚。变更对新打开的终端生效。
