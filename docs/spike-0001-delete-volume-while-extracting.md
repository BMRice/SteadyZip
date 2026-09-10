# Spike：分卷"边解压边删卷"验证

状态：已验证成立（2024，对应 `docs/spec.md` §2 与 ADR-0001 的前置验证）。

## 结论

**成立。** 用一个自定义 `IInStream`（`MultiVolumeStream`）把整套分卷按顺序拼成一个可 Seek 的逻辑流，直接喂给 7z.dll 的格式处理器（7z / zip），就绪后把"读尽某卷→删该卷"挂在"顺序读取越过卷边界"这一信号上，即可在解压期间逐卷永久删除源卷，且解出的文件与原始文件逐字节一致。

spike 程序：`spike/VolumeDeleteSpike`。用 7z.exe（`-v64k`）造出 11 卷的 solid 7z / non-solid 7z / zip，再经 7z.dll COM 解压并删卷：

| 用例 | 结果 |
| --- | --- |
| 7z solid（11 卷） | PASS：解出 8 文件全部一致，解压期间删除 10 卷 |
| 7z non-solid（11 卷） | PASS：同上 |
| zip（11 卷） | PASS：同上 |

## 关键发现

1. **不能用 7z.dll 的 Split handler 做删卷。** Split handler 会走 `IArchiveOpenVolumeCallback`，把每个卷当独立流在 Open 阶段逐个探测（Seek 到末尾拿长度），并把流保留到 Extract 阶段复用；"请求下一卷"只在 Open 发生一次，不是解压时的顺序推进信号。
2. **正确做法是自定义合并流**（ADR 的"自定义卷输入流"）：格式处理器看到的是"一个完整压缩包"，Open 阶段 Seek 读目录、Extract 阶段顺序读数据；顺序读越过卷边界即触发删卷。
3. **删除信号**：`MultiVolumeStream.Read` 顺序读耗尽某卷（读到该卷 EOF）时，关闭并永久删除该卷，再打开下一卷。Seek 不删卷，因此 Open 阶段（Seek 到末尾读目录）不会误删。
4. **7z.dll 的 COM 接口在 .NET 上必须"平铺"声明**：接口继承（`IInStream : ISequentialInStream`）会让 CLR 生成 CCW 时崩溃（ExecutionEngineException）。每个接口单独列出全部方法即可。
5. **卷命名**：7z.exe 的 `-v` 对 7z/zip 一律产出 `.NNN`（通用 split）；原生 `.z01`/`.part1.rar` 是其它工具的分卷命名，两者 `VolumeSet` 都要识别。
6. **末卷**：末卷末尾是目录（Open 时读，Extract 不重读），顺序读不会把它读到 EOF，故解压期间不删；解压成功后由引擎统一清理剩余卷。

## 落地

`MultiVolumeStream`（`src/UnzipTool.Core/SevenZip/MultiVolumeStream.cs`）实现该逻辑；`AllowDelete` 仅在完整性测试通过、开始真正解压前由引擎置位，保证"测试失败绝不删卷"。
