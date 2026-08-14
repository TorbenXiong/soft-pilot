# SoftPilot M0 自包含发布物验证记录

状态：已归档

最后更新：2026-08-14

验证提交：`e5f0945ca98ce0488285562cb9c50de673fa0fb6`

发布矩阵：[GitHub Actions run 31799335185](https://github.com/TorbenXiong/soft-pilot/actions/runs/31799335185)

同提交常规 CI：[GitHub Actions run 31799335131](https://github.com/TorbenXiong/soft-pilot/actions/runs/31799335131)

## 1. 范围与结论

M0-10 已完成 Windows x64、macOS ARM64/x64 和 Linux x64 的可重复打包及隔离验证。发布物不包含工作区、插件、配置或软件实例；替换主发布物后，原工作区标记保持不变并可继续使用。

本记录只证明 spike 发布模型可行。产物未签名、未公证，不是正式 GitHub Release，也不包含自动更新渠道；这些生产发布能力仍属于 M6。

## 2. 发布物与校验值

下表的“包体大小”来自 `release-metadata.json` 中实际主发布物的字节数；“Artifact 大小”是 GitHub Actions 为对应 Artifact 保存的压缩归档大小。

| 平台 | Artifact | 主发布物格式 | 包体大小 | Artifact 大小 | SHA-256 |
| --- | --- | --- | ---: | ---: | --- |
| Windows x64 | `softpilot-spike-windows-x64` | 单文件 `SoftPilot.exe` | 28,000,256 B（26.70 MiB） | 11,254,258 B（10.73 MiB） | `c695a6ee93da3688abc9288868fab78b8ea85c3ab1dc69598947d12d51fefff7` |
| macOS ARM64 | `softpilot-spike-macos-arm64` | `SoftPilot.app` bundle，以 `SoftPilot-macos-arm64.zip` 传输 | 10,988,010 B（10.48 MiB） | 10,971,269 B（10.46 MiB） | `76415d06703de06c3d11cdb010cf57e45ca8aede8e81df7629d93e09ff7aed62` |
| macOS x64 | `softpilot-spike-macos-x64` | `SoftPilot.app` bundle，以 `SoftPilot-macos-x64.zip` 传输 | 11,835,859 B（11.29 MiB） | 11,812,258 B（11.27 MiB） | `a4f5a7e751127f6ac36098b774cdcf5a655631b841ef7eaa41de924b34ca2694` |
| Linux x64 | `softpilot-spike-linux-x64` | 单文件 `SoftPilot-x86_64.AppImage` | 14,047,736 B（13.40 MiB） | 13,581,269 B（12.95 MiB） | `22b78df33ca81abc630e817e1ca0ad5a0bafcc8a0ad1bfe99623cc3caba8b4e7` |

每个发布 Artifact 同时包含 `SHA256SUMS.txt` 和 `release-metadata.json`。独立验证任务另行生成 `softpilot-spike-report-<platform>` Artifact，其中的 `release-verification.json` 再次计算并核对包体大小与 SHA-256。

## 3. 干净 Runner 验证

构建和验证使用不同的 GitHub-hosted Runner。验证任务不安装 Rust，并从 `PATH` 排除 Cargo、rustup 和托管 Rust 工具缓存；开始验证前明确断言 `cargo` 与 `rustc` 不可用。

| 验证项 | Windows x64 | macOS ARM64 | macOS x64 | Linux x64 |
| --- | --- | --- | --- | --- |
| 无预装 Rust/Cargo 或其他语言运行时依赖 | 通过 | 通过 | 通过 | 通过 |
| 主发布物启动 | 通过 | 通过 | 通过 | 通过 |
| Slint 窗口冒烟 | 通过 | 通过 | 通过 | 通过 |
| 工作区选择流程 | 通过 | 通过 | 通过 | 通过 |
| Component descriptor | `dev.softpilot.lifecycle-fixture 0.1.0 api 0.1.0` | 同左 | 同左 | 同左 |
| 子进程探针 | 通过 | 通过 | 通过 | 通过 |
| 跨进程文件锁 | 通过 | 通过 | 通过 | 通过 |
| 目录链接探针 | junction 通过 | symlink 通过 | symlink 通过 | symlink 通过 |
| 替换主发布物后继续使用原工作区 | 通过 | 通过 | 通过 | 通过 |

Linux 验证设置 `APPIMAGE_EXTRACT_AND_RUN=1`，由 AppImage 自身完成解包启动，不要求 Runner 提供 FUSE 或语言运行时。

## 4. 动态系统依赖

依赖列表由目标平台原生工具记录：Windows 使用 `dumpbin /DEPENDENTS`，macOS 使用 `otool -L`，Linux 使用 `ldd` 检查 AppDir 中的主可执行文件。

### Windows x64

Windows CRT 已静态链接。动态依赖仅为 Windows 系统 DLL：

`kernel32.dll`、`user32.dll`、`dwmapi.dll`、`gdi32.dll`、`shlwapi.dll`、`uiautomationcore.dll`、`oleaut32.dll`、`comctl32.dll`、`shell32.dll`、`ole32.dll`、`dwrite.dll`、`combase.dll`、`api-ms-win-core-winrt-error-l1-1-0.dll`、`api-ms-win-core-synch-l1-2-0.dll`、`uxtheme.dll`、`advapi32.dll`、`bcryptprimitives.dll`、`OPENGL32.dll`、`imm32.dll`、`ntdll.dll`。

### macOS ARM64/x64

两个架构只链接 macOS 系统 Framework 与系统 dylib：Accessibility、OpenGL、ApplicationServices、CoreGraphics、CoreVideo、Carbon、AppKit、Foundation、CoreFoundation、CoreData、CoreImage、CloudKit、QuartzCore、CoreText、ColorSync、CoreServices，以及 `/usr/lib/libSystem.B.dylib`、`/usr/lib/libobjc.A.dylib` 和 `/usr/lib/libiconv.2.dylib`。

### Linux x64

AppDir 主可执行文件记录的系统动态依赖为：`ld-linux-x86-64.so.2`、`libc.so.6`、`libm.so.6`、`libgcc_s.so.1`、`libfontconfig.so.1`、`libfreetype.so.6`、`libexpat.so.1`、`libz.so.1`、`libbz2.so.1.0`、`libpng16.so.16`、`libbrotlidec.so.1` 和 `libbrotlicommon.so.1`。

`libxkbcommon.so.0` 与 `libxkbcommon-x11.so.0` 由窗口后端运行时动态加载，`ldd` 无法发现；打包脚本通过目标 runner 的 `ldconfig` 精确定位，并显式收入 AppImage。干净 Runner 的 Slint 窗口与工作区冒烟验证证明这两项运行时依赖已随包提供。

## 5. 可重复执行入口

- `.github/workflows/release-spike.yml`：四平台构建矩阵、Artifact 上传和独立干净 Runner 验证。
- `eng/package-release.ps1`：原生 release 构建、平台封装、SHA-256 与动态依赖元数据。
- `eng/test-release-artifact.ps1`：校验 Artifact、清除语言工具链入口并执行完整发布物探针。

常规 CI run 31799335131 与发布矩阵 run 31799335185 均在 Windows 2025 x64、macOS 15 ARM64、macOS 15 Intel 和 Ubuntu 24.04 x64 原生 Runner 全部通过。M0-10 与平台基础 Gate 因此完成，后续从 M1-02 开始。
