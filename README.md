# NO English Mode

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D6?style=for-the-badge)](https://github.com/zf1234d/no_english_mode)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?style=for-the-badge)](https://github.com/zf1234d/no_english_mode)

[![Releases](https://img.shields.io/github/v/release/zf1234d/no_english_mode)](https://github.com/zf1234d/no_english_mode/releases/latest)
[![Commits](https://img.shields.io/github/commit-activity/m/zf1234d/no_english_mode)](https://github.com/zf1234d/no_english_mode/commits/main)
[![License](https://img.shields.io/badge/License-Apache--2.0-blue)](LICENSE)

一个 Windows 10/11 上的托盘输入法小工具：防止微软拼音被切到英文模式——窗口切换、webview 页内输入框切换、手动按 Shift，都会被立即切回中文。纯事件驱动、零轮询，纯系统托盘，无可视化主窗口。

实现思路参考自 [mbbill/no_english_mode](https://github.com/mbbill/no_english_mode)。

## 特性

- 仅托盘运行：常驻后台，无主窗体、无弹窗。
- 纯事件驱动：焦点事件 + 系统输入指示器事件，空闲时零 CPU、零轮询。
- 全链路纠正：窗口 / 输入框切换、webview 页内切换、手动按 Shift 切英文，均会被切回中文模式。
- webview 专门处理：Chromium 系（Chrome / Edge / Electron / WebView2）与 Firefox 的页内焦点切换不产生系统事件，由输入指示器事件唤醒被动守卫。
- 只管中文：仅处理简体中文键盘布局（0x0804）；要输入英文，`Win+Space` 切英文键盘布局即可，不干预。
- 开机自启：托盘菜单一键开关（写入当前用户注册表 Run 项，默认关闭）。
- 单实例 + 日志：重复启动自动退出；`log.txt` 记录每次纠正，写在 exe 同级。
- 单文件交付：约 25 KB 单 exe，依赖系统自带 .NET Framework 4.8，目标机免装。

## 使用

- 运行 `no_english_mode.exe`，系统托盘出现图标。
- 之后输入法被切到英文的场合会被自动切回中文；想输英文用 `Win+Space` 切英文键盘布局。
- 右键托盘 → 开机自启：开机自动运行。
- 右键托盘 → 退出。

### 存放位置提示

程序会把 `log.txt` 写到 exe 所在目录。请放在普通用户目录，不要放 Program Files 等受保护目录，否则无法写日志。

## 编译

目标框架 .NET Framework 4.8，编译需 .NET SDK（5+）：

```bat
cd no_english_mode
dotnet publish -c Release
```

构建产物：`bin\Release\net48\publish\no_english_mode.exe`

## 目录结构

```text
no_english_mode/
├── src/
│   ├── Program.cs        # 入口、专用文件夹检查、单实例
│   ├── TrayApp.cs        # 隐藏窗口、托盘菜单、WinEvent 钩子、消息循环
│   ├── ImeEnforcer.cs    # 核心纠正逻辑（有界确认 + webview 被动守卫）
│   ├── NativeMethods.cs  # Win32 声明与窗口分类
│   ├── AutoStart.cs      # 开机自启（注册表 Run 键）
│   ├── AppFolder.cs      # 共用文件夹判定
│   ├── Logger.cs         # 日志
│   └── SingleInstance.cs # 单实例互斥
└── no_english_mode.csproj
```

## 已知限制

- 管理员权限的目标进程受 UIPI 保护，无法修改（日志中为 win32 error 5）；需要时以管理员身份运行本程序。
- 非 webview 应用在 2 秒确认窗口结束后切英文不会被纠正（可调大 `src/ImeEnforcer.cs` 的 `WatchTotalMs`）。
- 输入指示器事件依赖未公开文档的系统行为；个别切换路径若不触发，可把 `WebViewFallbackPollMs` 设为 5000 启用慢速兜底。

## 许可证

[Apache-2.0](LICENSE)。程序图标来自 [mbbill/no_english_mode](https://github.com/mbbill/no_english_mode)（Apache-2.0）。

## AI 生成声明

本项目（含源代码与文档）由 AI 辅助生成，并经过人工复核与迭代调试。AI 生成内容可能包含错误、遗漏、过时或误导性信息，使用前请自行阅读代码并独立验证其行为。

本项目按「现状」（AS IS）提供，作者不提供任何明示或暗示的保证，也不对因使用本项目（或依据本项目做出判断）造成的任何损失承担责任。
