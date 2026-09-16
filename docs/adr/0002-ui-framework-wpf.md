# UI 框架：留在 WPF 自绘主题，不迁 WinUI 3

`docs/ui.md` 要求"现代 Windows 11 + Fluent Design"观感，直觉上该用 WinUI 3。但有两点让 WPF 更优：其一，目标机是 **Windows 10 LTSC 2021（build 19044）**，Win11 专属的窗口级特征（系统圆角、Mica、Win11 标题栏、`Segoe Fluent Icons` 字体）本就拿不到，WinUI 3 的主要优势落空；其二，该机无法访问 nuget.org，`Microsoft.WindowsAppSDK` 及其十余个依赖包无法还原。加之 ui.md 明确要求"不要直接复制 Windows 系统应用""不要玻璃拟态"，WPF 自绘主题（Design Token + 控件模板 + 少量 DWM 互操作）足以达成目标，且零新依赖、不重写既有 XAML 结构。

## Considered Options

- **WinUI 3 / WindowsAppSDK**：原生 Fluent 控件与窗口外观；但需联网还原十余个 NuGet 包（本机离线不可行），且目标是 Win10，窗口级 Win11 特征仍不可得。
- **WPF 自绘主题（选定）**：离线可行、零新依赖、对既有 4 个窗口改动可控；代价是控件模板需自行编写。

## Consequences

- 控件外观统一由 `Theme/*.xaml` 资源字典提供（颜色/字体/圆角/间距令牌 + 隐式样式与控件模板）。
- 窗口级现代感靠自绘标题栏（`WindowChrome`）+ DWM 浅色标题栏实现；**系统级圆角窗口在 Win10 上不可得**，窗口保持直角。
- 图标使用自绘矢量 Path，不依赖 Win11 才有的 `Segoe Fluent Icons` 字体。
