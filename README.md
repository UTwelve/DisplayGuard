# DisplayGuard

> 多屏环境窗口守护工具 — 自动监测指定显示器上的窗口，并将其迁移至目标显示器，保障窗口可见性。

当一块"显示器"其实并不是显示器——例如通过 HDMI 连接的条形音响、采集卡、EDID 模拟器被 Windows 识别为一块扩展屏幕——窗口和鼠标就可能"掉"进这块看不见的区域。DisplayGuard 以系统托盘常驻的方式监测这些屏幕，发现窗口误入即自动搬回，**不修改任何显示与音频配置**（禁用显示器可能导致 HDMI 音频中断，本工具正是为此场景设计）。

![icon](assets/DisplayGuard.png)

## 功能特性

- **系统托盘常驻**：无窗口打扰，启动即静默工作（`--quiet`）
- **检测屏幕多选**：按 EDID 关键字自动识别（如音响上报的 `EP-HDMI-RX`），或在托盘菜单手动勾选任意多块目标屏幕
- **转移目标可选**：默认搬回主屏，也可指定任意一块显示器作为目的地
- **防呆防冲突**：目标屏幕不存在时静默待机，绝不误搬；同一屏幕不能同时是"检测对象"与"转移目标"
- **可配置检查间隔**：0.5s / 1s / 2s / 5s（默认）/ 10s / 30s
- **移动日志**：按天滚动记录每次搬移与程序启停（`logs/DisplayGuard-YYYYMMDD.log`）
- **开机自启动**：一键写/删注册表 Run 键
- **零依赖免安装**：单文件 exe，仅依赖 Windows 自带的 .NET Framework 4.x

## 运行环境

- Windows 10 / 11
- .NET Framework 4.x（系统自带，无需安装运行时）

## 使用方式

1. 双击 `DisplayGuardTray.exe`，程序最小化至系统托盘
2. 右键托盘图标进行配置：
   - `检测屏幕`：自动检测或手动勾选需要看管的屏幕
   - `转移到`：选择窗口搬移目的地（默认主屏）
   - `检查间隔`、`启用防护`、`开机自启动`等
3. 配置文件 `config.ini` 与日志 `logs/` 均位于 exe 同目录

### 命令行参数

| 参数 | 说明 |
|---|---|
| （无参数） | 托盘模式运行 |
| `--quiet` | 托盘模式，静默启动（供开机自启使用） |
| `--list` | 列出所有显示器及当前配置后退出 |
| `--dry` | 扫描一次，仅报告将被移动的窗口 |
| `--once` | 扫描并实际搬移一次后退出 |
| `--quit` | 通知正在运行的托盘实例优雅退出 |

## 从源码构建

无需任何第三方工具链，使用 Windows 自带的 C# 编译器：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
  -nologo -target:exe -codepage:65001 ^
  -win32icon:assets\DisplayGuard.ico ^
  -out:DisplayGuardTray.exe ^
  -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.dll ^
  src\DisplayGuardTray.cs
```

## 目录结构

```
src/DisplayGuardTray.cs   完整源码（单文件，纯 Win32 P/Invoke）
assets/                   图标（ico / png）
tools/test-tray.ps1       自动化测试脚本（搬移测试 + 托盘冒烟测试）
tools/make_icon.py        图标生成脚本（Pillow）
```

## 已知限制

- 对以管理员权限运行的程序窗口，搬移可能失败（需 DisplayGuard 同样以管理员运行）
- 少数全屏游戏 / 自绘窗口可能忽略外部移动
- `taskkill /F` 强杀时无法写入 EXIT 日志行（OS 层面限制），正常退出请使用托盘菜单或 `--quit`

## 许可证

本项目基于 [MIT License](LICENSE) 发布，Copyright (c) 2026 UTwelve。
