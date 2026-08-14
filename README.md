# SoftPilot

SoftPilot 是面向 Windows 的开发运行时生命周期管理器。V1 聚焦 Node.js、Eclipse Temurin JDK 和 CPython，支持官方版本发现、多版本安装、全局切换、外部运行时只读发现和永久卸载。

安装完成后可通过桌面快捷方式启动，也可从 Windows 开始菜单搜索“SoftPilot”。主界面按运行时提供“已安装”和“版本管理”页签，并优先打开“已安装”；“已安装”页选择当前版本，版本管理页负责安装和永久卸载。选择任一当前版本后，SoftPilot 自动启用终端集成；清除最后一个当前版本时自动恢复原 `PATH` 与 `JAVA_HOME`。官方版本目录缓存 24 小时，启动时先显示本地数据，再在后台刷新过期目录。安装新版本后不会自动设为当前版本，运行时下载期间在对应版本行显示 0–100% 进度。界面默认使用简体中文，并支持即时切换英文。

## V1 边界

- Windows 11 24H2+ x64
- Node.js 官方 Windows x64 ZIP
- Eclipse Temurin HotSpot JDK Windows x64 ZIP
- CPython 官方 Python Install Manager
- 用户级当前版本与自动终端集成
- 不包含项目级绑定、镜像、自定义源、数据库服务、Docker、AI CLI、普通软件和跨平台实现

## 技术栈

- .NET 10 LTS
- WinUI 3 + Windows App SDK（主界面）
- WPF 自研安装器与卸载器
- CommunityToolkit.Mvvm、System.CommandLine
- Microsoft.Data.Sqlite.Core + Windows `winsqlite3.dll`
- MSTest

## 构建

需要 .NET SDK 10.0.400 或与 [global.json](global.json) 匹配的 SDK。

```powershell
dotnet restore SoftPilot.slnx --locked-mode
dotnet build SoftPilot.slnx -c Release --no-restore
dotnet test tests/SoftPilot.Tests/SoftPilot.Tests.csproj -c Release --no-build --no-restore
```

## 生成安装包

`eng/package.ps1` 发布自包含的 GUI、CLI、shim 和卸载器，创建 SHA-256 负载清单，最后将负载嵌入单文件安装器。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\package.ps1 -Version 0.0.3
```

生产发布应提供当前用户证书存储中的代码签名证书指纹：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\package.ps1 `
  -Version 0.0.3 `
  -CertificateThumbprint '<SHA1 thumbprint>'
```

安装包输出到 `artifacts/release/<version>/SoftPilot-Setup.exe`。不传证书时仅生成明确标记的未签名开发构建。

## 安装目录

安装器要求用户选择“父目录”，并先规范化路径，再用 ordinal 大小写敏感比较末级名称：

- 末级精确等于 `SoftPilot`：直接使用所选目录。
- 其他情况：追加 `\SoftPilot`。

安装器按 `C:`、`D:`、`E:` 直至 `Z:` 的顺序检查盘根目录，首个通过安装路径校验的位置作为默认父目录；若没有盘根目录可用，则回退到 `%LOCALAPPDATA%\Programs`。页面只展示追加或复用 `SoftPilot` 后的最终安装目录。安装目标仅允许当前用户可写的本地固定 NTFS 磁盘，并拒绝系统目录、UNC/网络盘、可移动盘、已知 OneDrive 目录、同名文件和其他应用占用的非空目录。

详见 [架构说明](docs/architecture.md) 和 [自研安装器决策](docs/adr/0001-self-contained-dotnet-installer.md)。
