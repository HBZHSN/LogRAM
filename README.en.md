<div align="center">

<img src="LogRAM.png" alt="LogRAM" width="128" />

# LogRAM

A large-file log viewer and search tool for Windows.

[简体中文](README.md) | English

</div>

LogRAM loads logs fully into memory and uses byte-level search, SIMD, multi-core processing, and virtualized rendering to handle large files. It is designed for multi-gigabyte logs that ordinary text editors struggle to open, and is available as a GUI, command-line tool, and MCP server.

<div align="center">

<img src="light.png" alt="Light theme" width="49%" />
<img src="dark.png" alt="Dark theme" width="49%" />

</div>

## Features

- Open and search large log files, with an available-memory check before each load.
- Browse multiple files in tabs; files opened again from Explorer are forwarded to the existing window.
- Search with plain text, regular expressions, case sensitivity, and include/exclude conditions.
- Stream and cancel search results, jump to the source line, and export results.
- Refresh files manually or follow appended content in real time.
- Read UTF-8 and GBK files.
- Recent files, search history, line-number navigation, and result context.
- Dark and light themes, with Simplified Chinese and English interfaces.
- GUI, native command-line tool, and stdio MCP server.

## Requirements

- Windows 10 1809 (build 17763) or later
- x64, x86, or ARM64
- Enough free memory for the log file and its line index

Each open file is loaded fully into memory, so memory use accumulates across tabs. Free memory at least equal to the file size is recommended.

## Installation

Download an x64 build from [Releases](../../releases):

| File | Runtime | Recommended for |
| --- | --- | --- |
| `LogRAM-win-x64.exe` | .NET runtime included | Most users; runs directly after download |
| `LogRAM-win-x64-fd.exe` | Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) | A smaller download |

LogRAM is a portable single-file application and does not require installation.

## GUI Usage

1. Click **Open** and select a log file.
2. Choose UTF-8 or GBK to match the file encoding.
3. Enter a search term and press Enter; enable regex, case sensitivity, or advanced search when needed.
4. Click a result to jump to its source line. **Refresh** reloads the file, while **Live** follows appended content.

Keyboard shortcuts:

| Shortcut | Action |
| --- | --- |
| `Ctrl+F` | Focus the search box |
| `Ctrl+G` | Go to a line number |
| `F3` / `Shift+F3` | Next / previous result |

Advanced search supports "include any" and "exclude any" terms, or the equivalent syntax:

```text
in(error,warning);notin(retry,ignored)
```

## Command Line

`LogRAM-cli` uses the same search engine as the GUI and supports OR, AND, NOT, regex, line ranges, context, result limits, and JSON output.

```powershell
# Match error or warning, require orderId=42, and exclude retry
LogRAM-cli.exe app.log --any error --any warning --all orderId=42 --exclude retry --context 3 --max-count 100 --json

# Search a line range and print only the match count
LogRAM-cli.exe app.log timeout --start-line 100000 --end-line 200000 --count-only
```

Run `LogRAM-cli.exe --help` for all options.

## MCP Server

`LogRAM-mcp` is a stdio MCP server for AI clients. A log is loaded on first use and reused while the MCP process remains alive, making repeated searches and bounded reads of the same large file efficient.

Available tools:

- `search_log`: search with multiple conditions, regex, line ranges, and context.
- `read_log`: read a bounded range of lines.
- `list_open_logs`: list cached files and memory use.
- `close_log`: release one or all cached logs.

Publish and install:

```powershell
dotnet publish LogRAM-mcp/LogRAM-mcp.csproj -c Release -p:PublishProfile=win-x64
.\scripts\install-mcp.ps1 -ConfigureCodex
```

Or configure the published executable in another MCP client:

```json
{
  "mcpServers": {
    "logram": {
      "command": "C:\\path\\to\\LogRAM-mcp.exe"
    }
  }
}
```

## Build from Source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# Run the GUI
dotnet run --project LogRAM/LogRAM.csproj

# Run the CLI
dotnet run --project LogRAM-cli/LogRAM-cli.csproj -- app.log error

# Publish the self-contained GUI
dotnet publish LogRAM/LogRAM.csproj -c Release -p:PublishProfile=win-x64

# Publish the framework-dependent GUI
dotnet publish LogRAM/LogRAM.csproj -c Release -p:PublishProfile=win-x64-fd

# Publish the MCP server
dotnet publish LogRAM-mcp/LogRAM-mcp.csproj -c Release -p:PublishProfile=win-x64
```

## Limitations

- Windows only and read-only; LogRAM does not edit files.
- Every file is loaded fully into memory.
- Only UTF-8 and GBK are currently supported.
- The fast case-insensitive path is ASCII-only; non-ASCII text uses a slower decoded path.
- Large files need an initial line-indexing pass when opened.

## License

This project is licensed under the [MIT License](LICENSE).
