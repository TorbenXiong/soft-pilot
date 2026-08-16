# ADR 0001：自研 .NET 安装器

[English](0001-self-contained-dotnet-installer.md) | **简体中文**

状态：已被 [ADR 0002](0002-portable-self-migration.zh-CN.md) 取代

## 决策

V1 最初使用自包含 WPF 安装器和卸载器，不引入第三方安装框架。

## 原因与结果

该方案可复用 .NET 路径校验并支持自定义升级语义，但需要自行维护注册、快捷方式、回滚、签名和卸载自删除。后续便携式发布移除了这些维护成本。
