<div align="center">

<img src="LogRAM.png" alt="LogRAM" width="128" />

# LogRAM

**In-memory viewer for very large log files**

[简体中文](README.md) | English

</div>

LogRAM = **Log** + **RAM**. It loads the entire log file into memory, then combines a byte-level / SIMD / multi-core search engine with virtualized rendering so you can **open instantly, scroll smoothly, and search in a flash** through multi-GB — even tens-of-GB — logs that Notepad and ordinary editors simply cannot open.

<div align="center">

<img src="light.png" alt="Light theme" width="49%" />
<img src="dark.png" alt="Dark theme" width="49%" />

</div>

---

## Features

- **Huge files**: on startup LogRAM measures the free RAM and lets you open a single file up to **80%** of the memory available at launch (the status bar shows the current free RAM and this open limit live).
- **Blazing search**: plain text uses byte-level search (no per-line decoding); ASCII case-insensitive search is SIMD-vectorized; large files are searched across all CPU cores automatically.
- **Streaming results**: matches appear as they are found, the search can be cancelled at any time, and progress plus hit count are shown live.
- **Regex / case sensitivity**: toggle regular expressions and case-sensitive matching.
- **Advanced search**: combine several keywords as "include any / exclude any" — a line matches when it contains any include term (OR) and none of the exclude terms (NOT); edit them visually in a popup or type the `in(a,b);notin(c,d)` syntax directly.
- **Click to jump**: click any line in the results and the main view instantly scrolls to and highlights that log line.
- **CJK-friendly**: opens as UTF-8 by default, with a manual switch to GBK.
- **Virtualized rendering**: only visible lines are rendered, so scrolling and jumping stay smooth even with a tens-of-GB file in memory.
- **Dark / light themes**: one-click toggle that also drives the native Windows dark title bar.
- **Bilingual UI**: built-in Simplified Chinese and English interfaces, switchable any time under **Settings → Language**; defaults to Simplified Chinese and applies instantly (no restart).
- **Portable**: single-file exe, available with or without the bundled runtime.
- **Multi-arch**: x64 / x86 / ARM64.

## Strengths

- **Fast**: in-memory data + byte-level matching + SIMD + multi-core parallelism searches tens of GB in just seconds.
- **Opens files others can't**: logs beyond an editor's size limit can still be browsed, navigated, and searched.
- **Stays responsive**: paged, virtualized rendering plus a custom scrollbar — performance is independent of file size.
- **Zero-dependency deployment**: the self-contained build needs no .NET runtime install; just download and run.

## Limitations

- **Memory-hungry**: the whole file is loaded into RAM, so it needs roughly as much free memory as the file size; the open limit is 80% of the RAM available when LogRAM starts, and loading fails beyond that.
- **Windows only**: built on WPF; no macOS / Linux support today.
- **Read-only**: it is a viewer and does not edit files.
- **Limited encodings**: currently only **UTF-8** and **GBK**; UTF-16 and others are not supported yet.
- **Single-file view**: one file at a time, no tabs.
- **No live tail**: after the log grows you must click **Refresh**; it does not follow the file automatically.
- **The case-insensitive fast path is ASCII-only**: case-insensitive matching that includes non-ASCII text (e.g. Chinese) falls back to a slower decode path.
- **Initial line index build**: very large files take a few seconds to open while the line index is built (the elapsed time is shown).

## Tech Stack

- **.NET 8** / **WPF** (`net8.0-windows`)
- C# 12 with `Nullable` enabled
- `System.Text.Encoding.CodePages` (GBK / CP936 support)
- `System.Numerics.Vector<byte>` SIMD vectorization
- `System.Threading.Tasks.Parallel` multi-core parallelism
- DWM native dark title bar (`dwmapi.dll`), PerMonitorV2 DPI awareness

## How It Works

- **Chunked in-memory load**: the file is split into 64 MB chunks stored in a `byte[][]`, avoiding the single-array 2 GB / Large Object Heap limits.
- **Line index + binary search**: newline offsets are scanned at load time to build a line-start table, so locating by line number or byte offset is `O(log n)`.
- **Search engine**:
  - Plain text: `Span.IndexOf` directly over bytes (hardware-accelerated), no decoding.
  - ASCII case-insensitive: SIMD case normalization plus a high-selectivity "anchor" byte to cut down false matches.
  - Regex / non-ASCII case-insensitive: decode each line, then match.
  - Advanced search: each "include / exclude" keyword is compiled into a byte anchor pattern; sub-blocks are scanned to flag every line's hits, then lines that match any include term and none of the exclude terms are kept; SIMD and multi-core parallelism apply too, and keywords are ASCII-only.
  - Files ≥ 256 MB are scanned per chunk in parallel, then merged and de-duplicated in order.
- **Virtualized rendering**: the UI holds only the currently visible lines, reading and decoding on demand while scrolling.

## Requirements

- Windows 10 1809 (build 17763) or later
- Architecture: x64 / x86 / ARM64
- Enough free memory (recommended ≥ the size of the file you want to open)

## Download & Use

Grab a 64-bit build from this repository's **Releases** page — pick one:

| Build | File | Size | Notes |
| --- | --- | --- | --- |
| With runtime (self-contained) | `LogRAM-win-x64.exe` | Larger (tens of MB) | **No .NET install required**, download and run; recommended for most users |
| Without runtime (framework-dependent) | `LogRAM-win-x64-fd.exe` | Tiny (< 5 MB) | Requires [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64) installed first |

Double-click the exe to run — no installation needed.

### Basic Usage

1. Click **Open** and pick a `.log` / `.txt` or any text log file.
2. Files open as UTF-8 by default; the top **Encoding** dropdown switches between UTF-8 / GBK; **Refresh** re-reads the current file.
3. Type a keyword in the search bar and press **Enter** or click **Search**; optionally enable **Regex** and **Match case**, and **Cancel** mid-search.
4. Click **Advanced** to open the advanced search panel, fill in several "include any (OR)" and "exclude any (NOT)" keywords (ASCII only), then click **Search**; the `in(a,b);notin(c,d)` syntax can also be typed straight into the search bar.
5. Click any line in the results to jump to and highlight the matching log line in the main view.
6. Use the top-right button to toggle the **dark / light** theme.
7. Open **Settings** to adjust the font and size, and switch the **Language** between **Simplified Chinese / English** (applied instantly, no restart). The interface defaults to Simplified Chinese.

## Build from Source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# Restore and run
dotnet run --project LogRAM/LogRAM.csproj

# Publish: with runtime (self-contained single file, the Releases LogRAM-win-x64.exe)
dotnet publish LogRAM/LogRAM.csproj -c Release -p:PublishProfile=win-x64

# Publish: without runtime (framework-dependent single file, smallest size)
dotnet publish LogRAM/LogRAM.csproj -c Release -p:PublishProfile=win-x64-fd
```

Output lands in `LogRAM/bin/Release/net8.0-windows/win-x64/publish[-fd]/`.
`win-x86` and `win-arm64` publish profiles are also provided.

## License

See the `LICENSE` file in the repository root.
