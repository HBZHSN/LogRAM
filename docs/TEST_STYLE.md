# LogRAM 复验指南

复验从固定本地样例开始。涉及 SSH 协议、真实主机或宿主集成时执行对应层的检查，并在任务中记录实际环境。历史大日志的耗时与容量使用原记录中的版本和机器条件。 [产品验收](PRODUCT.md#范围与验收) · [验证证据](TASKS.md#t-001) · [历史大日志](../LogRAM.Npp/VALIDATION.md#真实大日志)

## 共用样例

将以下五行以 UTF-8 保存为专用临时目录中的 `sample.log`，末行保留换行。复验开始和结束可用 `Get-FileHash` 比对源文件。导出到同一临时目录的另一个文件。

```text
INFO ready
ERROR first
error retry
WARNING review
INFO end
```

### AC-01 本地桌面查看

1. 在独立 GUI 的打开对话框、专用便携 NPP 的文件打开对话框中分别打开同一 `sample.log`。选择 UTF-8。
2. 关闭“区分大小写”选项，在搜索框输入 `in(error,warning);notin(retry)`。预期命中第 2、4 行。
3. 点击第 4 行结果，原文定位到 `WARNING review`，可看到相邻的第 3、5 行。导出应包含对应行号及文本。
4. 关闭日志标签，再打开文件并搜索，仍得到相同结果。NPP 可以切回普通编辑区，关闭标签释放对应的查看器会话。 [插件行为](../LogRAM.Npp/README.md) · [IS-001](impl-spec/001-local-log.md)

### AC-02 CLI 查询

构建后以 Windows 可读的样例路径执行下列查询。

```powershell
dotnet run --project LogRAM-cli/LogRAM-cli.csproj -c Release --no-build -- 'C:\path\sample.log' --any error --any warning --exclude retry --context 1 --json
```

预期退出码为 0。JSON 的 `matchCount` 和 `returnedMatchCount` 均为 2，`lines` 包含第 1 至 5 行，仅第 2、4 行的 `isMatch` 为真。 [CLI 用法](../README.md#命令行) · [IS-001](impl-spec/001-local-log.md)

### AC-03 MCP 查询与缓存

在新开的测试 MCP 进程中，给以下调用使用同一样例的绝对路径。`path` 用实际路径替换。

| 步骤 | 参数 | 预期 |
| --- | --- | --- |
| `search_log` | `path`、`include_any: ["error","warning"]`、`exclude: ["retry"]`、`before: 1`、`after: 1` | 命中与 CLI 一致，首次 `cacheHit` 为假。 |
| `read_log` | `path`、`start_line: 4`、`line_count: 2` | 返回 `WARNING review` 和 `INFO end`，`cacheHit` 为真。 |
| `list_open_logs` | `{}` | 样例路径已缓存。 |
| `close_log`，再调用 `list_open_logs` | 关闭时给 `path`，列出时给 `{}` | `closed` 为 1，列表为空，源文件哈希一致。 |

安装方式与工具参数以 README 和服务契约为准。 [MCP 配置](../README.md#mcp-服务) · [工具契约](../LogRAM-mcp/McpServer.cs)

### AC-04 SSH 远程查看

1. 在独立 GUI 和 NPP 各验一遍，每轮开始时在隔离 Linux 主机上准备可读的五行样例。在 SSH 入口选择测试别名，用路径前缀定位并打开。
2. 使用 AC-01 的查询，预期得到同样的行号和上下文，状态区显示远端可用内存与工作进程占用。开启实时后从测试主机给样例追加 `ERROR appended` 一行，预期新增第 6 行命中。
3. 模拟断线后点击刷新，确认能重新打开并查到三个匹配行。关闭标签，确认对应连接和远端工作进程退出。
4. 用超过 1000 个匹配的测试样本检查总数、已返回结果和导出。总数应覆盖所有匹配行，返回和导出遵循行数及字节预算。 [使用前提](../README.md#ssh-远程日志) · [IS-002](impl-spec/002-ssh-log.md)

## 已有自检命令

目标框架为 Windows .NET 8。解决方案构建采用能读取 `LogRAM.slnx` 的 SDK，本机协议自检需要 Python。默认本机自检使用临时样例。`LOGRAM_SSH_TEST_CONFIG` 未设置时，CLI 的 SSH 自检在本机启动工作进程并验证协议。

```powershell
dotnet build LogRAM.slnx -c Release -p:Platform=x64
python -B scripts/test_ssh_agent.py
dotnet run --project LogRAM-cli/LogRAM-cli.csproj -c Release --no-build -- --ssh-self-test
dotnet run --project LogRAM.Npp.Viewer/LogRAM.Npp.Viewer.csproj -c Release --no-build -- --self-test
dotnet run --project LogRAM-mcp/LogRAM-mcp.csproj -c Release --no-build -- --self-test
```

引擎自检覆盖 UTF-8/GBK、查询、取消、追加末行、截断和导出。协议自检覆盖 SSH 配置、启动、分页、回传上限与错误恢复。MCP 自检覆盖 Unicode 组合查询、上下文、缓存和刷新。 [引擎自检](../LogRAM.Npp.Viewer/SelfTest.cs) · [SSH 自检](../LogRAM/SshSelfTest.cs) · [协议测试](../scripts/test_ssh_agent.py) · [MCP 自检](../LogRAM-mcp/McpServer.cs)

## 界面与真实环境

- 界面自检在 Windows 桌面运行。独立 GUI 使用 `--ssh-ui-self-test` 和输出目录，WinForms 查看器使用 `--ui-self-test`。覆盖工作区布局、快捷键、远程分页和关闭释放等交互，实际命令见现有说明。 [GUI 自检](../README.md#从源码构建) · [NPP 自检](../LogRAM.Npp/README.md#从源码构建)
- NPP 宿主回归使用已安装匹配插件的专用便携目录，按 x64/x86 分别执行 `scripts/test-npp.ps1`。`-AutoLog` 验证自动接管，普通模式验证打开已保存文档。脚本会调整该目录的主题、窗口位置和文件检测设置。 [原生构建](../scripts/build-npp.ps1) · [宿主验证](../LogRAM.Npp/VALIDATION.md#复验)
- 真实 SSH 复验遵循 AC-04。本机协议测试证明 Python 工作进程与适配层的交互，实际登录、主机权限和重连需在隔离 OpenSSH 环境验证。 [SSH 自检](../LogRAM/SshSelfTest.cs)
