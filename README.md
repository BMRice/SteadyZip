# 解压软件

Windows 桌面压缩/解压工具（C# / WPF），按 `docs/spec.md` 实现。差异化能力集中在解压侧：**解压前完整性测试**、**分卷边解压边删卷**、**密码库自动尝试**。

## 目录结构

```
src/UnzipTool.Core/   核心引擎（无 UI 依赖）：7z.dll COM 直绑、引擎路由、密码库、zip-slip、分卷
src/UnzipTool.App/    WPF 界面
spike/VolumeDeleteSpike/   分卷"边解压边删卷"spike（见 docs/spike-0001-*.md）
tests/UnzipTool.Core.Tests/   最小可运行自检（无测试框架）
deps/7z/7z.dll        7-Zip 22.01 (x64) 运行时（LGPL，随构建复制到输出目录）
```

## 构建与运行

前置：.NET SDK（本仓库在 SDK 10 上验证）。

```powershell
# 单进程构建（本环境并行构建会触发 MSBuild 编译服务器超时）
dotnet build UnzipTool.sln -m:1

# 运行自检（会调用 NVIDIA App 或 Program Files\7-Zip 下的 7z.exe 做交叉校验）
dotnet run --project tests/UnzipTool.Core.Tests

# 运行 spike（验证边解压边删卷）
dotnet run --project spike/VolumeDeleteSpike

# 运行 GUI
dotnet run --project src/UnzipTool.App
```

### 直接运行（免命令行）

发布到根目录：双击根目录的 `SteadyZip.exe` 即可（需本机已装 .NET 10 运行时）。同目录的 `SteadyZip.dll`、`UnzipTool.Core.dll`、`SteadyZip.deps.json`、`SteadyZip.runtimeconfig.json`、`7z.dll` 是运行所必需，需与 exe 放一起。

重新生成根目录可运行文件：

```powershell
dotnet publish src/UnzipTool.App/UnzipTool.App.csproj -c Release --no-restore -o publish
# 然后把 publish 里的 SteadyZip.exe / SteadyZip.dll / UnzipTool.Core.dll / SteadyZip.deps.json / SteadyZip.runtimeconfig.json
# 以及 deps/7z/7z.dll 一起复制到根目录
```

> 生成**单个**自包含 exe（不依赖已装 .NET，约几十 MB）需要联网下载 .NET 运行时包，命令为：
> `dotnet publish src/UnzipTool.App/UnzipTool.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`。
> 7z.dll 已作为嵌入资源打进程序集，运行时会自动解压到 `%LOCALAPPDATA%\SteadyZip`，因此该方式产出的单个 exe 也能用。

创建 RAR 需要机器上装 WinRAR 的 `rar.exe`，缺失时界面自动置灰。

## 引擎路由（ADR-0001）

| 操作 | ZIP | 7z | RAR | tar.gz |
| --- | --- | --- | --- | --- |
| 创建 | 7z.dll | 7z.dll | rar.exe（缺失置灰） | — |
| 解压 | 7z.dll | 7z.dll | 7z.dll | 7z.dll |
| 完整性测试 | 7z.dll | 7z.dll | 7z.dll | 7z.dll |

## 关键实现说明

- **COM 直绑**：`src/UnzipTool.Core/SevenZip/Interop.cs` 直接绑定 7z.dll 的 `CreateObject` 导出与全部 COM 接口。接口必须"平铺"声明（不做托管接口继承），否则 CLR 生成 CCW 时崩溃（ExecutionEngineException）。
- **边解压边删卷**：`MultiVolumeStream` 把整套分卷拼成一个可 Seek 的逻辑流喂给格式处理器；顺序读取耗尽某卷时关闭并永久删除该卷（不进回收站），Seek 不删卷。详见 `docs/spike-0001-delete-volume-while-extracting.md`。
- **分卷创建**：7z/zip/rar 分卷本质是"单个压缩包按固定大小切片"（spike 中字节级确认），故创建分卷 = 先生成单个压缩包到临时文件，再切片。
- **密码库**：DPAPI（crypt32 `CryptProtectData`）加密，绑定当前 Windows 用户，存于 `%LOCALAPPDATA%\UnzipTool\passwords.dat`。

## 与规格的偏差

- **.NET 版本**：规格写 .NET 8；本机离线环境仅有 SDK 10 + net10.0 引用包，故目标框架为 `net10.0` / `net10.0-windows`。代码本身不依赖 .NET 10 特性，改回 `net8.0` 只需改各 `.csproj` 的 `TargetFramework`。
- **批量队列**：当前一次处理一个压缩包（串行）；多文件拖入按首个处理。规格 §7 的"批量队列"留作后续 UI 增强，核心引擎已是串行、可取消、带进度的模型。

## 非目标（按规格 §11，未实现）

右键 shell 集成、多语言 i18n、包内搜索/预览、损坏包修复、包内文件编辑。
