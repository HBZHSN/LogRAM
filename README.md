<div align="center">

<img src="LogRAM.png" alt="LogRAM" width="128" />

# LogRAM

一个面向 Windows 的大文件日志查看与搜索工具。

简体中文 | [English](README.en.md)

</div>

LogRAM 将日志完整加载到内存，并使用字节级搜索、SIMD、多核并行和虚拟化渲染处理大文件。它适合查看普通文本编辑器难以打开的数 GB 日志，同时提供 GUI、命令行和 MCP 三种使用方式。

<div align="center">

<img src="light.png" alt="浅色主题" width="49%" />
<img src="dark.png" alt="深色主题" width="49%" />

</div>

## 主要功能

- 打开并搜索大型日志文件；每次打开文件时会按当前可用内存检查容量。
- 多标签页浏览，并将资源管理器中再次打开的文件转交给现有窗口。
- 支持纯文本、正则表达式、区分大小写和包含/排除组合搜索。
- 搜索结果流式显示，可取消、跳转到原文并导出。
- 支持刷新文件和实时读取追加内容。
- 支持 UTF-8 和 GBK 编码。
- 提供最近文件、搜索历史、行号跳转和结果上下文。
- 支持深色/浅色主题，以及简体中文/英文界面。
- 提供 GUI、原生命令行工具和 stdio MCP 服务。

## 系统要求

- Windows 10 1809（build 17763）或更高版本
- x64、x86 或 ARM64
- 足够容纳日志文件及行索引的可用内存

LogRAM 会把每个已打开文件完整加载到内存，因此多标签页的内存占用会累加。建议可用内存不少于待打开文件的大小。

## 安装

从 [Releases](../../releases) 下载 x64 版本：

| 文件 | 运行环境 | 适用场景 |
| --- | --- | --- |
| `LogRAM-win-x64.exe` | 已包含 .NET 运行时 | 推荐，大多数电脑下载后可直接运行 |
| `LogRAM-win-x64-fd.exe` | 需要 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) | 文件更小 |

LogRAM 是免安装的单文件程序，下载后直接运行即可。

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

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

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
