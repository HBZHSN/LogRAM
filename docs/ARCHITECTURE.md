# LogRAM 架构

Windows 侧四个入口共用 .NET 8 日志解析和查询代码。`LogRAM.Core` 持有 `ILogDocument`，并通过链接编译项引用 `LogRAM/` 下的本地引擎、编码、查询和 SSH 实现。 [文档接口](../LogRAM.Core/ILogDocument.cs) · [核心项目](../LogRAM.Core/LogRAM.Core.csproj)

| 入口 | 调用路径与进程边界 |
| --- | --- |
| 独立 GUI | WPF 应用负责标签、视口、搜索交互及单实例文件转交，经 `ILogDocument` 调用本地或远程实现。 [应用入口](../LogRAM/App.xaml.cs) · [主窗口](../LogRAM/MainWindow.xaml.cs) |
| Notepad++ | 原生插件 DLL 在宿主内处理文件接管、标签、快捷键和窗口消息，独立 WinForms 查看器持有日志文档。插件经进程管道发送控制消息，查看器窗口嵌入宿主并用窗口消息回传远程标签请求。 [宿主](../LogRAM.Npp/Plugin.cpp) · [查看器](../LogRAM.Npp.Viewer/ViewerForm.cs) · [ADR-002](ADR/002-notepad-host-worker.md) |
| CLI | CLI 入口引用 GUI 项目里的 `CliRunner`。运行器解析参数，调用 `LogFileDocument` 和 `LogQueryEngine` 输出文本、计数或 JSON。 [CLI 入口](../LogRAM-cli/Program.cs) · [运行器](../LogRAM/CliRunner.cs) |
| MCP | 独立控制台进程通过 stdio 读写 JSON-RPC。串行处理工具调用，按规范化路径缓存 Windows 可读文件，复用本地查询引擎。 [服务实现](../LogRAM-mcp/McpServer.cs) |

## 数据流与运行边界

1. 本地打开将正文按字节分块载入并建立行起始偏移。原文视口按页解析，搜索选择字节、向量化、多核或解码路径。缓存与索引的生命期由各入口管理。 [IS-001](impl-spec/001-local-log.md)
2. SSH 打开由 Windows 客户端通过用户 OpenSSH 配置连接 Linux。远端 Python 进程以登录用户权限读取日志，在远端建立索引并执行查询。客户端通过同一连接取得目录、页、命中和内存状态。 [ADR-001](ADR/001-ssh-remote-worker.md) · [IS-002](impl-spec/002-ssh-log.md)
3. 独立 GUI 的用户设置保存在 `%APPDATA%\LogRAM\settings.json`。插件使用宿主插件配置目录的 `LogRAM.Npp.json`，文件正文与查询缓存由打开文档持有。 [GUI 设置](../LogRAM/AppSettings.cs) · [插件设置](../LogRAM.Npp/README.md)

源码构建以 Windows 为目标。NPP 的原生 DLL 由 CMake 构建并与查看器打包。独立 GUI 和 MCP 的发布配置位于相应项目的 `Properties/PublishProfiles`。断线后的恢复和宿主降级见各自的规格与决策。 [构建入口](../README.md#从源码构建) · [插件构建脚本](../scripts/build-npp.ps1)
