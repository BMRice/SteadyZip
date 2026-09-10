# 双引擎分派：7z.dll 兜底，rar.exe 仅创建 RAR

解压软件要覆盖 ZIP/7z/RAR 的创建、解压与完整性测试，但 RAR 是 RARLAB 专有格式——7-Zip 只能解压不能创建，而 rar.exe 不可随应用重分发。因此采用双引擎路由器：除"创建 RAR"外，其余全部走 7z.dll（COM 接口直绑，以便自定义卷输入流支撑"边解压边删卷"）；"创建 RAR"仅在检测到或用户指定 rar.exe 时可用，否则置灰。

## Considered Options

- **单一 7z.dll**：最简，但无法创建 RAR，不满足"完整压缩"范围。
- **单一 rar.exe/unrar**：RAR 侧强，但 ZIP/7z 侧不如 7z.dll，且同样不可重分发。
- **双引擎分派（选定）**：7z.dll 覆盖绝大部分；rar.exe 只补"创建 RAR"这一格，缺失时优雅降级。

## Consequences

- 需要一个引擎路由器，按 格式×操作 分派。
- RAR 创建是可选能力，依赖用户机器上的 rar.exe；缺失时 UI 置灰、RAR 解压不受影响。
- 7z.dll 用 COM 直绑而非命令行，这是"读尽某卷→删该卷"的技术前提，动工前用 spike 验证。
