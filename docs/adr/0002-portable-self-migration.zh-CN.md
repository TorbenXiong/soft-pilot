# ADR 0002：便携式自迁移发布

[English](0002-portable-self-migration.md) | **简体中文**

状态：已采用

## 决策

发布单个自包含 x64 `SoftPilot.exe`。首次启动时用户选择工作区；SoftPilot 校验并迁移到 `<SoftPilotRoot>\SoftPilot.exe`，所有管理内容保存在 `<SoftPilotRoot>\SoftPilotData`。

CLI 与 shim 作为经过校验的内嵌负载，原子部署到 `SoftPilotData\tools`。具体运行时保存在 `SoftPilotData\app\<kind>\<version>`，供 IDE 直接选择。

## 原因

- 无需安装，也无需面向用户的 ZIP。
- 替换 EXE 不会触碰运行时或用户数据。
- 源文件清理只删除已证明与目标相同的 EXE。
- 不再维护安装器、卸载器、Apps & Features 和开始菜单。

## 结果

可选快捷方式仅创建在桌面。删除和升级都直接操作文件：退出 SoftPilot，替换或删除 EXE；除非完整清理，否则保留 `SoftPilotData`。WinUI 单文件运行时可能把原生文件解压到用户临时目录。正式发布的 EXE 与内嵌工具应使用 Authenticode 签名。
