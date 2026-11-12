# 2026.11.12.3 — Android Subsystem for Windows

用 C# 写的 Windows「安卓子系统」命令行工具。产物是**单个自包含 exe**（`android.exe`，约 35 MB，目标机无需安装 .NET），它调用 Android Studio 官方那套 SDK / emulator 来完成下载、建虚拟机、启动虚拟机。

默认机型：**Pixel 9 Pro**。版本范围：Android 2.2（API 8）→ Android 17（API 37），共 29 个版本。

## 命令

```
android.exe                     首次运行向导：选下载目录 → 装 emulator/ADB → 选版本
android.exe -setup <目录>        指定下载目录并安装基础组件
android.exe -list android       列出全部版本及「已安装 / 可下载 / 不支持」
android.exe -download <版本>     下载该版本系统镜像，例如 -download android17
android.exe -download all       下载官方仓库里全部可用镜像（体积非常大）
android.exe -start <版本>        创建并启动该版本虚拟机，例如 -start android16
android.exe -start <版本> -gpu <模式>   指定图形模式 host / auto / swiftshader_indirect / angle_indirect / guest
android.exe -stop               关闭所有正在运行的模拟器
android.exe -where              显示下载目录与配置文件位置
```

版本写法：`android17`、`17`、`android2.2`、`android5.0` ……

## 首次运行会下载什么

放在用户选的目录（SDK 根）下，结构与 Android Studio 完全一致：

```
<SDK根>\
├─ cmdline-tools\latest      sdkmanager / avdmanager
├─ platform-tools\           adb 工作集
├─ emulator\                 Android Studio 虚拟机引擎
├─ system-images\android-XX\ 系统镜像（预览版可能是 android-37.0 这种带小数位的目录）
├─ avd\                      AVD（as_android16 等）
├─ logs\                     模拟器日志 emulator-<AVD>.log
└─ .staging\                 下载解压暂存（与目标同卷，避免跨卷移动失败）
```

配置保存在 `%LOCALAPPDATA%\AndroidSubsystem\settings.json`。

## 编译

需要 .NET 8 SDK（或更高）：

```
cd AndroidSubsystem
"C:\Program Files\dotnet\dotnet.exe" publish -c Release -r win-x64 --self-contained -o ..\dist
```

`PublishSingleFile / SelfContained / win-x64 / 压缩` 参数已写在 `AndroidSubsystem.csproj` 里，产物就是 `dist\android.exe`。

也可以直接跑 `build.ps1`。

## 已知问题：Android 17（API 37）在 emulator 上白屏重启

API 37 预览镜像在 emulator 上会规律性地崩溃，属上游缺陷，与本工具无关：

- `logcat -b crash`：`Fatal signal 6 (SIGABRT) in tid xxx (RegionSampling), pid xxx (surfaceflinger)`
- `Abort message: 'Assertion failed: !rcEnc->featureInfo()->hasReadColorBufferDma'`（每 20–30 秒一次）
- 紧接着 launcher 报 `java.lang.IllegalStateException: DEAD_OBJECT`，系统重启 → 白屏 + 重启循环

已逐项排除：`-gpu host` / `-gpu guest` / `-gpu swiftshader_indirect` 全部崩溃；关闭 `GLDMA`、`GLDirectMem` 无效；镜像 rev 7（rev 8 装不上）；emulator 37.2.13 与 canary 37.3.9 均崩。
对照组：Android 16（API 36）同一 emulator 上冷启动 `crash=0`，客机内 `screencap` 能截到真实桌面。

因此工具在启动后做约 45 秒健康检查（运行时长是否被重置 + crash 缓冲里有无 SurfaceFlinger 致命信号），判定失败时打印原因；`-start android17` 失败会**自动改用 Android 16 启动**，其它版本失败则以退出码 3 结束。

## 目录说明

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | 入口、异常收敛 |
| `Cli.cs` | 命令解析、list / download / start / stop / where |
| `Wizard.cs` | 首次运行向导（选目录 → 装组件 → 选版本 → 启动） |
| `AndroidSdk.cs` | SDK 安装、镜像解析、AVD 创建、启动、窗口、健康检查 |
| `Catalog.cs` | 29 个版本目录与别名解析 |
| `ImageRepo.cs` | 解析 `sdkmanager --list`，按 tag/ABI 优先级择优 |
| `Downloader.cs` | 断点前清理的单文件下载 + 进度条 |
| `ProcessRunner.cs` | 外部进程执行与后台启动（GUI 程序不隐藏窗口） |
| `ConsoleUi.cs` | 输出与交互（含 `BuildVersion`） |
| `WindowHelper.cs` | Win32 顶层窗口查找 / 显示 / 置顶 / 挪回屏幕 |
| `Settings.cs` | `%LOCALAPPDATA%` 下的配置读写 |
| `build.ps1` | 单文件发布脚本 |

更详细的操作说明与历史问题排查见 `使用说明.md`。
