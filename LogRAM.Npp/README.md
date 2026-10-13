# LogRAM for Notepad++

只读大日志查看插件，复用 LogRAM 的内存、字节级 SIMD、多核搜索引擎。

## 安装

关闭 Notepad++，将发布包里的整个 `LogRAM.Npp` 文件夹复制到 NPP 的 `plugins` 目录：

```text
Notepad++/
  plugins/
    LogRAM.Npp/
      LogRAM.Npp.dll
      LogRAM.Npp.Viewer.exe
      ...配套 DLL 与运行时文件
```

插件与 Notepad++ 的架构必须一致。默认 x64 包包含 .NET 8 运行时；不需要另装 .NET。
发布页还提供 `-plugin-admin.zip`：DLL 和配套文件位于 ZIP 根目录，供 NPP Plugins Admin 安装。手动使用此包时，请解压到 `plugins/LogRAM.Npp/`，不要直接解压到 `plugins/`。
重启后，使用 NPP 自带的“文件 → 打开”、最近文件、命令行或已有的 NPP 文件关联打开 `.log`，自动显示 LogRAM。扩展名不区分大小写，`.LOG` 同样支持。
`Ctrl+Alt+L` 切换 LogRAM / 普通 NPP 工作区；其他类型的文件使用普通 NPP 编辑区。
该快捷键可在 NPP 的 Shortcut Mapper 中修改，LogRAM 控件内也会使用修改后的快捷键。
LogRAM 模式使用整个编辑工作区：上方原文、下方结果，中间分隔条可拖动，操作逻辑与独立 GUI 一致。
搜索框位于下方结果区顶部；搜索、取消、选项、导出、刷新、实时、行号跳转、上一 / 下一结果在同一行。顶部文件工具栏已移除。
工具栏按搜索、搜索选项、刷新 / 实时、行号跳转、结果导航 / 导出分为五组，用竖线分隔。常用操作采用图标，正则与大小写使用原版的 `.*` / `Aa`；悬停显示操作说明及快捷键，启用的选项保持高亮。
普通 NPP 编辑区暂时隐藏；通过 NPP 插件菜单或 `Ctrl+Alt+L` 返回，原文件、修改和编辑内容都保留。全部 LogRAM 查看控件只读。
点击 `.log` 文件标签自动显示该日志的 LogRAM 界面；点击其他文件或新建普通文件，自动恢复普通编辑区。LogRAM 的已打开日志与搜索缓存仍保留。
关闭 NPP 的日志标签也会释放对应的 LogRAM 文件缓存。

`Open current saved file` 读取当前文件的磁盘版本；没有保存的修改不会出现在 LogRAM 中。
`.log` 正文直接由 LogRAM 后台加载，NPP 标签只持有简短的只读提示，不重复加载正文。已接管日志的写入受保护，不能将占位内容保存回源文件。
NPP 恢复的未保存日志快照继续保留在普通编辑区；不会用磁盘内容替换其中的修改。
**Plugins → LogRAM → Open log...** 也调用 NPP 的标准打开对话框；其他类型文件可通过 Open current saved file 查看。

## 功能

- 使用 NPP 文件标签、最近文件与关闭文件功能；关闭日志标签释放缓存。
- 编码使用 NPP 菜单：UTF-8 / UTF-8 BOM，或 Character sets → Chinese → GB2312（GBK）。仅改变读取编码，不转换或修改源文件。
- 普通搜索、正则、大小写选项、`in(error,warning);notin(retry)` 组合查询。
- 搜索进度、取消、逐批显示已返回结果、结果导航、点击结果显示原文上下文。
- 按行号跳转、可复制的只读文本、UTF-8 结果导出（行号 + Tab + 原文）。
- 刷新、当前文件实时追加与末尾跟随。追加时只查询可能变化的最后一行和新行。
- 跟随 NPP 深浅主题及界面语言（中文 / English），保留搜索历史。
- 常驻后台缓存；关闭 NPP 时退出。后台故障可通过菜单 Restart 恢复。

插件窗口内：`Ctrl+O` / `Ctrl+W` / `Ctrl+N` 使用 NPP 打开 / 关闭 / 新建，`Ctrl+Tab` / `Ctrl+Shift+Tab` 切换 NPP 标签；`Ctrl+F` 搜索、`Ctrl+G` 行号跳转、`F3` / `Shift+F3` 导航、`Esc` 取消、`PageUp` / `PageDown` 翻页。
搜索进度对并行扫描有效；部分搜索路径先完成各分块扫描，再返回结果。
取消时保留已返回的部分结果，重新搜索可得到完整结果。加载取消在分块和行索引检查点生效。

日志完整驻留内存，另有行索引和匹配行号；界面只创建当前页的行，原文不经命令管道传输。
数 GB 文件建议使用 x64 版 NPP 和插件；x86 后台进程受 32 位地址空间限制。
超长行可复制完整内容；显示列宽最大 65536 像素。
刷新会先加载新副本，成功后释放旧副本，因此刷新峰值内存可能接近两份日志。
日志被截断时暂停实时模式，保留当前快照，提示手动刷新；不跟踪同大小文件替换。
实时读取只作用于当前选中的文件，切换回来后继续读取追加内容。
导出先写同目录临时文件，完成后替换目标；禁止覆盖已打开的日志。

设置保存在 NPP 提供的插件配置目录里的 `LogRAM.Npp.json`，与独立 GUI 设置分开。
后台异常日志位于同目录的 `LogRAM.Npp.worker.log`。
已在 Notepad++ 8.9.8.1 的 x64 / x86 便携版中验证。ARM64 需要相应编译工具及实机验证。
自动接管在 NPP 的加载通知期间分流宿主进程的文件读取；如果某个 NPP 构建不支持所需入口，会提示通过 Open current saved file 查看已打开文档。
插件没有编辑、替换、保存源日志、文件关联或 MCP 客户端功能。

## 从源码构建

需要 .NET 8+ SDK，以及 CMake + MinGW-w64（x64）或 Visual Studio C++ 编译工具。

```powershell
.\scripts\build-npp.ps1
.\scripts\install-npp.ps1 -NotepadDirectory 'C:\Program Files\Notepad++'
```

输出 `LogRAM.Npp/bin/package-x64/LogRAM.Npp-1.3.4-x64.zip`。
`-Architecture x86` / `arm64` 使用对应的 Visual Studio 编译工具和 .NET 运行时。
`-FrameworkDependent` 输出到独立的 `package-x64-fd` 目录，不包含运行时，需要目标机器安装对应架构的 .NET 8 Desktop Runtime。

```powershell
dotnet run --project LogRAM.Npp.Viewer -- --self-test
dotnet run --project LogRAM.Npp.Viewer -- --ui-self-test
dotnet run --project LogRAM.Npp.Viewer -c Release -- --benchmark
dotnet run --project LogRAM.Npp.Viewer -c Release -- --benchmark 'C:\path\large.log'
.\scripts\test-npp.ps1 -NotepadDirectory 'C:\path\portable-npp'
.\scripts\test-npp.ps1 -NotepadDirectory 'C:\path\portable-npp' -AutoLog
```

宿主测试必须使用专用的便携 NPP 目录；脚本会调整其中的主题、窗口位置和文件检测设置。
