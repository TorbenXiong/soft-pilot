# SoftPilot

[English](README.md) | **简体中文**

SoftPilot 是用于管理 Node.js、Java、Python、本地开发 Redis，以及最新版便携 Git for Windows 的 Windows 便携式应用。

## 开始使用

1. 下载 `SoftPilot.exe` 后直接运行，无需安装。
2. 首次启动时选择工作区，SoftPilot 会自动迁移到该位置并重新启动。也可以同时创建桌面快捷方式。
3. 打开对应运行时的“版本管理”页签，安装需要的版本。
4. 在“已安装”页签中选择“终端默认版本”，之后新打开的终端会使用该版本。

升级时请先退出 SoftPilot，再用新版 `SoftPilot.exe` 替换原文件。已安装的运行时和应用数据都会保留。

## 下载来源

Node.js 与 Temurin 归档默认对官方源和内置清华 TUNA 镜像进行小流量 HTTPS 探测，并使用响应更快的来源。Python 始终使用官方来源。Redis 版本必须与 Redis 官方发布目录交叉核对；Windows x64 归档来自社区 `redis-windows/redis-windows` 项目，并强制校验 GitHub 提供的 SHA-256 摘要。Git 使用 Git for Windows 官方 GitHub 仓库的最新版 x64 PortableGit 资产，同样强制校验 GitHub 提供的 SHA-256 摘要。

## 主要功能

- 从 Node.js、Eclipse Temurin、Python 和 Redis 官方目录发现可管理版本。
- 并行安装和管理多个运行时版本。
- 无需重新安装或删除其他版本，即可选择终端默认版本。
- 自动为新打开的终端配置 `node`、`npm`、`npx`、Java、Python、`redis-server` 和 `redis-cli` 命令入口。
- 管理每个可用 Redis 主版本线的最新可验证补丁，并在 `127.0.0.1:6379` 运行一个数据和日志按版本隔离的本地实例。
- 以只读方式发现 SoftPilot 外部安装的运行时。
- 在对应版本旁直接展示下载进度和操作结果。
- 永久卸载不再需要的版本。
- 安装、启动、升级和卸载唯一一份最新版便携 Git for Windows，不修改用户 `PATH`，也不影响其他 Git 安装；Git 页面可检查 SSH 和 Git LFS 组件状态，并在用户明确保存时编辑全局 `user.name` 与 `user.email`。卸载保留统一下载缓存以及包括 `user.name`、`user.email` 在内的 Git 全局配置。
- 模块显示与排序修改即时生效并自动保存。
- 支持英文和简体中文即时切换，默认使用英文。

## 支持环境

- Windows 11 24H2 或更高版本
- x64 系统
- Node.js Windows x64 版本
- Eclipse Temurin HotSpot JDK Windows x64 版本
- CPython Windows x64 版本
- `redis-windows/redis-windows` 提供的 Redis x64 社区构建，仅用于本地开发
- Git for Windows 最新 PortableGit x64 版本

下载缓存统一保存在 `SoftPilotData\cache\downloads`。SoftPilot 每次启动时自动删除超过 30 天的缓存文件并清理空目录；各模块卸载不单独处理缓存。需要立即清空时仍可使用 `spt cache clean`。

## Redis CLI

```powershell
spt runtime install redis@8.2.9
spt use redis@8.2.9 --global
spt redis start
spt redis status --json
spt redis stop
spt runtime uninstall redis@8.2.9                 # 保留 Redis 数据和日志
spt runtime uninstall redis@8.2.9 --delete-data   # 同时永久删除数据和日志
```

SoftPilot 不会把 Redis 注册为 Windows Service，也不会设置开机启动。该 Windows 构建与 Redis Ltd. 无隶属或官方认可关系，不应用于生产部署。
