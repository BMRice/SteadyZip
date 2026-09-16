# 解压软件

Windows 桌面压缩/解压工具（C# / WPF），按 `docs/spec.md` 实现。差异化能力集中在解压侧：**解压前完整性测试**、**分卷边解压边删卷**、**密码库自动尝试**。

## 目录结构

```
src/UnzipTool.Core/   核心引擎（无 UI 依赖）：7z.dll COM 直绑、引擎路由、密码库、zip-slip、分卷
src/UnzipTool.App/    WPF 界面
tests/UnzipTool.Core.Tests/   最小可运行自检（无测试框架）
deps/7z/7z.dll        7-Zip 22.01 (x64) 运行时（LGPL，作为嵌入资源打进程序集）
```

## 构建与运行

前置：.NET SDK（本仓库在 SDK 10 上验证）。

```powershell
# 单进程构建（本环境并行构建会触发 MSBuild 编译服务器超时）
dotnet build UnzipTool.sln -m:1

# 运行自检（会调用 NVIDIA App 或 Program Files\7-Zip 下的 7z.exe 做交叉校验）
dotnet run --project tests/UnzipTool.Core.Tests

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
- **RAR 有两个处理器**：7z.dll 把 RAR4 和 RAR5 分成两个 handler（`Rar` = `…000110030000`，`Rar5` = `…000110CC0000`），扩展名都是 `.rar`，只能按文件签名（第 7 字节 `01`/`00`）区分，见 `FormatDetection.GetClsidForOpen`。把 RAR5 喂给 RAR4 处理器不会报错——它会以 `S_OK` + **0 个条目**收场，界面上表现为"选了包但列表是空的"。
- **加密头部要"拒绝"而不是给空密码**：头部加密的包（`rar a -hp`），7z.dll 会向 open 回调索取密码。回调若回 `S_OK` + 空字符串，等于声明"密码就是空串"，处理器解不开头就以 `S_OK` + **0 个条目**收场——与"空压缩包"无法区分，界面上同样是"选了包但列表是空的"，而且密码库里已有的正确密码永远轮不到。回调必须在没有密码时返回 `E_ABORT`（`OpenCallback.CryptoGetTextPassword`）；引擎再用"处理是否索取过密码"（`PasswordRequested`）区分"头部已加密"与"真的是空包"，抛 `ArchivePasswordException`，界面据此弹出密码输入框。
- **open 回调要回答 `kpidName`，且 `S_FALSE` 不能当"损坏"**：7z.dll 会向回调询问被交给它的文件名，回调若答空，Tar 处理器会判定打不开而回 `S_FALSE`——一个合法的空 `.tar` 因此被当成坏文件，`OpenCallback` 现在总是回答该名字。但反过来也不要把 `S_FALSE` 当作损坏信号：实测同一个空 `.tar`（1024 个零字节，7z.exe 能正常打开）在有的进程状态回 `S_FALSE`、有的回 `S_OK`，两者不可区分，所以损坏文件仍显示为空列表。原因见 `SevenZipEngine.OpenInternal` 的 `ponytail:` 注释。
- **7z.dll 的 handler CLSID 要照抄**：`FormatIds.BZip2` 曾把 `…-1000-000110020000` 误写成 `…-000110020200`，指向不存在的类，`.tar.bz2` 一律以 `CLASS_E_CLASSNOTAVAILABLE` 失败。
- **边解压边删卷**：`MultiVolumeStream` 把整套分卷拼成一个可 Seek 的逻辑流喂给格式处理器；顺序读取耗尽某卷时关闭并永久删除该卷（不进回收站），Seek 不删卷。详见 `docs/spike-0001-delete-volume-while-extracting.md`。
- **RAR 分卷不能拼接**：`.partN.rar` 每卷都是独立压缩包，必须由 7z.dll 自己经 `IArchiveOpenVolumeCallback` 串卷（`OpenCallback`），拼接只会得到第一卷。删卷因此改用 `VolumeReaper`：它持有每个卷的文件句柄，实测 Rar5 处理器在解压期严格顺序读卷、不回退，于是"开始读第 N+1 卷"即判定第 N 卷已完成并删除。该判定依赖的是实测行为而非 7z.dll 的承诺，上限与升级路径见 `VolumeReaper` 的注释。
- **分卷创建**：7z/zip/rar 分卷本质是"单个压缩包按固定大小切片"（spike 中字节级确认），故创建分卷 = 先生成单个压缩包到临时文件，再切片。
- **密码库**：DPAPI（crypt32 `CryptProtectData`）加密，绑定当前 Windows 用户，存于 `%LOCALAPPDATA%\UnzipTool\passwords.dat`。

## 与规格的偏差

- **.NET 版本**：规格写 .NET 8；本机离线环境仅有 SDK 10 + net10.0 引用包，故目标框架为 `net10.0` / `net10.0-windows`。代码本身不依赖 .NET 10 特性，改回 `net8.0` 只需改各 `.csproj` 的 `TargetFramework`。
- **批量队列**：当前一次处理一个压缩包（串行）；多文件拖入按首个处理。规格 §7 的"批量队列"留作后续 UI 增强，核心引擎已是串行、可取消、带进度的模型。

## 非目标（按规格 §11，未实现）

右键 shell 集成、多语言 i18n、包内搜索/预览、损坏包修复、包内文件编辑。
