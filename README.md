<div align="center">

<img src="LogRAM.png" alt="LogRAM" width="128" />

# LogRAM

一个面向 Windows 的大文件日志查看与搜索工具。

简体中文 | [English](README.en.md)

</div>

LogRAM 将日志完整加载到内存，并使用字节级搜索、SIMD、多核并行和虚拟化渲染处理大文件。它适合查看普通文本编辑器难以打开的数 GB 日志，提供独立 GUI、Notepad++ 插件、命令行工具和 MCP 服务四种使用方式，共用同一搜索引擎。

最新发布：[Notepad++ 插件 1.3.4](https://github.com/HBZHSN/LogRAM/releases/tag/npp-1.3.4)。独立 GUI、CLI 和 MCP 的发行包见 [1.3.0](https://github.com/HBZHSN/LogRAM/releases/tag/1.3.0)。

| 使用方式 | 适合的场景 | 入口 |
| --- | --- | --- |
| Notepad++ 插件 | 在 NPP 中打开大日志、筛选结果和查看上下文 | [安装与使用](#notepad-插件) |
| 独立 GUI | 多标签查看日志、实时跟随和交互搜索 | [下载](#安装与下载) · [使用](#gui-使用) |
| CLI | 终端查询、批处理、结果计数和 JSON 输出 | [命令行](#命令行) |
| MCP | AI 客户端反复查询日志并按行读取内容 | [MCP 服务](#mcp-服务) |

<div align="center">

<img src="docs/images/gui-light.png" alt="独立 GUI 浅色主题：原文、搜索条件与结果上下文" width="49%" />
<img src="docs/images/gui-dark.png" alt="独立 GUI 深色主题：同一演示日志的搜索结果" width="49%" />

</div>

## 主要功能

- 打开并搜索大型日志文件；独立 GUI 在加载前检查可用内存。
- 多标签页浏览，并将资源管理器中再次打开的文件转交给现有窗口。
- 支持纯文本、正则表达式、区分大小写和包含/排除组合搜索。
- 搜索结果流式显示，可取消、跳转到原文并导出。
- 支持刷新文件和实时读取追加内容。
- 支持 UTF-8 和 GBK 编码。
- 提供最近文件、搜索历史、行号跳转和结果上下文。
- 支持深色/浅色主题，以及简体中文/英文界面。
- 提供 GUI、命令行工具和 stdio MCP 服务。
- 提供 Notepad++ 只读日志插件，默认接管 `.log` 的加载并显示上下两栏工作区，复用同一搜索引擎。

## 系统要求

- Windows 10 1809（build 17763）或更高版本
- 独立程序源码支持 x64、x86 或 ARM64；现有独立 GUI 发行包为 x64
- Notepad++ 插件需要 NPP 8.9.8.1 或更高版本，提供 x64 / x86 包，与 NPP 架构一致
- 足够容纳日志文件及行索引的可用内存

LogRAM 会把每个已打开文件完整加载到内存，因此多标签页的内存占用会累加。除正文外，还需为行索引和匹配结果预留内存；数 GB 日志建议使用 x64。

## 安装与下载

Notepad++ 插件当前版本为 **1.3.4**，包内包含 .NET 8 运行时：

| 文件 | 下载 | 安装方式 |
| --- | --- | --- |
| `LogRAM.Npp-1.3.4-x64.zip` | [x64 下载](https://github.com/HBZHSN/LogRAM/releases/download/npp-1.3.4/LogRAM.Npp-1.3.4-x64.zip) | 手动安装，数 GB 日志建议选择 x64 |
| `LogRAM.Npp-1.3.4-x86.zip` | [x86 下载](https://github.com/HBZHSN/LogRAM/releases/download/npp-1.3.4/LogRAM.Npp-1.3.4-x86.zip) | 用于 32 位 NPP |
| `*-plugin-admin.zip` | [发行页](https://github.com/HBZHSN/LogRAM/releases/tag/npp-1.3.4) | Plugins Admin 专用结构；手动使用时解压到 `plugins/LogRAM.Npp/` |

独立 GUI、CLI 和 MCP 当前发行版本为 **1.3.0**：

| 文件 | 运行环境 | 适用场景 |
| --- | --- | --- |
| [LogRAM-win-x64.exe](https://github.com/HBZHSN/LogRAM/releases/download/1.3.0/LogRAM-win-x64.exe) | 已包含 .NET 运行时 | 推荐，大多数电脑下载后可直接运行 |
| [LogRAM-win-x64-fd.exe](https://github.com/HBZHSN/LogRAM/releases/download/1.3.0/LogRAM-win-x64-fd.exe) | 需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) | 文件更小 |
| [LogRAM-cli.exe](https://github.com/HBZHSN/LogRAM/releases/download/1.3.0/LogRAM-cli.exe) | 自包含发行包 | 命令行查询与批处理 |
| [LogRAM-mcp.exe](https://github.com/HBZHSN/LogRAM/releases/download/1.3.0/LogRAM-mcp.exe) | 自包含发行包 | stdio MCP 客户端集成 |

LogRAM 是免安装的单文件程序，下载后直接运行即可。

## Notepad++ 插件

1. 查看 NPP 的架构，下载对应的 x64 或 x86 手动安装包。
2. 关闭 NPP，将 ZIP 中整个 `LogRAM.Npp` 文件夹复制到 NPP 的 `plugins` 目录；更新时覆盖原插件文件夹。
3. 重启 NPP，用自带的“文件 → 打开”、最近文件或文件标签打开 `.log` / `.LOG`，自动显示 LogRAM 工作区。

```text
Notepad++/plugins/LogRAM.Npp/
  LogRAM.Npp.dll
  LogRAM.Npp.Viewer.exe
  ...配套 DLL、运行时与许可证文件
```

上栏显示原文与上下文，下栏显示搜索结果。搜索框位于下栏顶部，图标操作分为搜索、搜索选项、刷新 / 实时、行号跳转、结果导航 / 导出五组；悬停显示操作说明，`.*` 与 `Aa` 分别控制正则和大小写。

<div align="center">

<img src="docs/images/npp-light.png" alt="NPP 插件浅色中文界面：上下两栏与分组图标工具栏" width="49%" />
<img src="docs/images/npp-dark.png" alt="NPP 插件深色界面：原文上下文和搜索结果" width="49%" />

</div>

`.log` 正文直接由 LogRAM 后台载入内存，NPP 编辑器只保留简短的只读提示。文件打开、关闭、标签切换和最近文件使用 NPP；编码通过 NPP 的 UTF-8 或 Character sets → Chinese → GB2312 菜单选择。`Ctrl+Alt+L` 切换 LogRAM / 普通编辑区，快捷键可在 NPP Shortcut Mapper 中修改。

| 快捷键 | 插件内的功能 |
| --- | --- |
| `Ctrl+O` / `Ctrl+W` / `Ctrl+N` | 调用 NPP 打开 / 关闭 / 新建 |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | 切换 NPP 标签 |
| `Ctrl+F` / `Ctrl+G` | 聚焦搜索 / 行号跳转 |
| `F3` / `Shift+F3` | 下一 / 上一结果 |
| `Esc` | 取消当前操作 |

已提交 [NPP 官方插件列表收录 PR #1220](https://github.com/notepad-plus-plus/nppPluginList/pull/1220)。维护者审核并发布列表后，才能通过 Plugins Admin 安装；当前可使用上面的手动安装包。

详细功能、内存行为与使用边界见 [插件说明](LogRAM.Npp/README.md)，测试依据见 [验证记录](LogRAM.Npp/VALIDATION.md)。本页截图均使用生成的演示日志。

## GUI 使用

1. 点击“打开”选择日志文件。
2. 根据文件编码选择 UTF-8 或 GBK。
3. 输入关键词并按 Enter；需要时启用正则、区分大小写或高级搜索。
4. 点击搜索结果可跳转到原文；“刷新”重新读取文件，“实时”持续读取追加内容。

常用快捷键：

| 快捷键 | 功能 |
| --- | --- |
| `Ctrl+F` | 聚焦搜索框 |
| `Ctrl+G` | 跳转到指定行 |
| `F3` / `Shift+F3` | 下一个 / 上一个搜索结果 |

高级搜索支持“包含任一”和“排除任一”，也可直接使用以下语法：

```text
in(error,warning);notin(retry,ignored)
```

## 命令行

`LogRAM-cli` 使用与 GUI 相同的搜索引擎，支持 OR、AND、NOT、正则、行范围、上下文、结果上限和 JSON 输出。

```powershell
# 包含 error 或 warning，同时包含 orderId=42，并排除 retry
LogRAM-cli.exe app.log --any error --any warning --all orderId=42 --exclude retry --context 3 --max-count 100 --json

# 查询指定行范围并只输出命中数
LogRAM-cli.exe app.log timeout --start-line 100000 --end-line 200000 --count-only
```

运行 `LogRAM-cli.exe --help` 查看全部参数。

## MCP 服务

`LogRAM-mcp` 是供 AI 客户端使用的 stdio MCP 服务。日志首次查询时加载到内存，并在 MCP 进程存活期间复用，适合对同一大文件进行多次搜索和局部读取。

提供以下工具：

- `search_log`：搜索日志，支持多条件、正则、行范围和上下文。
- `read_log`：按行号读取局部内容。
- `list_open_logs`：查看已缓存文件和内存占用。
- `close_log`：释放一个或全部已缓存日志。

发布并安装：

```powershell
dotnet publish LogRAM-mcp/LogRAM-mcp.csproj -c Release -p:PublishProfile=win-x64
.\scripts\install-mcp.ps1 -ConfigureCodex
```

也可以将发布后的程序配置到其他 MCP 客户端：

```json
{
  "mcpServers": {
    "logram": {
      "command": "C:\\path\\to\\LogRAM-mcp.exe"
    }
  }
}
```

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。构建 Notepad++ 插件还需要 CMake，以及 MinGW-w64（x64）或 Visual Studio C++ 编译工具（x86 / ARM64）。默认自包含包包含运行时：

```powershell
.\scripts\build-npp.ps1 -Architecture x64
.\scripts\install-npp.ps1 -NotepadDirectory 'C:\Program Files\Notepad++' -Architecture x64
```

GUI、CLI 和 MCP 的运行与发布示例：

```powershell
# 运行 GUI
dotnet run --project LogRAM/LogRAM.csproj

# 运行 CLI
dotnet run --project LogRAM-cli/LogRAM-cli.csproj -- app.log error

# 发布自包含 GUI
dotnet publish LogRAM/LogRAM.csproj -c Release -p:PublishProfile=win-x64

# 发布依赖运行时的 GUI
dotnet publish LogRAM/LogRAM.csproj -c Release -p:PublishProfile=win-x64-fd

# 发布 MCP 服务
dotnet publish LogRAM-mcp/LogRAM-mcp.csproj -c Release -p:PublishProfile=win-x64
```

## 限制

- 仅支持 Windows，且只能查看日志，不能编辑。
- 每个文件都会完整载入内存。
- 目前只支持 UTF-8 和 GBK。
- 忽略大小写的高速搜索仅适用于 ASCII；非 ASCII 内容会使用较慢的解码路径。
- 首次打开大文件时需要建立行索引。

## 许可证

本项目采用 [MIT License](LICENSE)。
