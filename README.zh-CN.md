# SoftPilot

[English](README.md) | **简体中文**

SoftPilot 是用于安装和管理多个 Node.js、Java 与 Python 版本的 Windows 便携式应用。

## 开始使用

1. 下载 `SoftPilot.exe` 后直接运行，无需安装。
2. 首次启动时选择工作区，SoftPilot 会自动迁移到该位置并重新启动。也可以同时创建桌面快捷方式。
3. 打开对应运行时的“版本管理”页签，安装需要的版本。
4. 在“已安装”页签中选择“终端默认版本”，之后新打开的终端会使用该版本。

升级时请先退出 SoftPilot，再用新版 `SoftPilot.exe` 替换原文件。已安装的运行时和应用数据都会保留。

## 下载来源

Node.js 与 Temurin 归档默认对官方源和内置清华 TUNA 镜像进行小流量 HTTPS 探测，并使用响应更快的来源。版本目录和完整性数据仍来自官方；校验失败立即终止，Python 始终使用官方来源。

## 主要功能

- 从 Node.js、Eclipse Temurin 和 Python 官方来源发现可管理版本。
- 并行安装和管理多个运行时版本。
- 无需重新安装或删除其他版本，即可选择终端默认版本。
- 自动为新打开的终端配置 `node`、`npm`、`npx`、Java 和 Python 命令入口。
- 以只读方式发现 SoftPilot 外部安装的运行时。
- 在对应版本旁直接展示下载进度和操作结果。
- 永久卸载不再需要的版本。
- 模块显示与排序修改即时生效并自动保存。
- 支持英文和简体中文即时切换，默认使用英文。

## 支持环境

- Windows 11 24H2 或更高版本
- x64 系统
- Node.js Windows x64 版本
- Eclipse Temurin HotSpot JDK Windows x64 版本
- CPython Windows x64 版本
