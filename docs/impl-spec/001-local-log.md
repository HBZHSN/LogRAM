# IS-001 本地日志打开、搜索和释放

已实施，适用 1.4.0。规范补记于 2026-11-24，覆盖四种入口对 Windows 可读文件的访问。 [产品 P-01 至 P-04](../PRODUCT.md#范围与验收) · [DR-01 至 DR-06](../DOMAIN.md#规则) · [任务](../TASKS.md#t-001)

## 流程与契约

1. 从入口传入路径和读取编码。在 `LogFileDocument.Open` 检查文件存在及文件大小的内存门槛，以只读方式加载数据，建立按行访问的字节偏移。正文、索引和结果共同占用内存，文件大小检查针对正文。 [本地实现](../../LogRAM/LogFileDocument.cs)
2. GUI 和 NPP 通过 `ILogDocument` 读取原文页并搜索。结果以行号关联原文，允许跳转、查看上下文和导出。NPP 的宿主接管与故障回退见宿主决策。 [文档接口](../../LogRAM.Core/ILogDocument.cs) · [ADR-002](../ADR/002-notepad-host-worker.md)
3. CLI 和 MCP 走 `LogQueryEngine`，行范围从 1 起算，起止行均包含在查询范围内。文本或正则条件、包含任一、包含全部和排除任一共同约束命中。返回可选上下文时合并重叠范围，并用 `isMatch` 标明返回的命中行。JSON 的 `matchCount` 是总匹配数，`returnedMatchCount` 是受上限约束的返回数。 [查询引擎](../../LogRAM/LogQuery.cs) · [CLI 参数](../../LogRAM/CliOptions.cs) · [MCP 契约](../../LogRAM-mcp/McpServer.cs)
4. GUI/NPP 标签、CLI 命令和 MCP 进程各自管理文档。MCP 以规范化的绝对路径标识缓存，使用同一编码查询时可复用。切换编码会重新加载。`search_log` 默认返回最多 100 个匹配，可调到 1000。`read_log` 一次最多读 2000 行，`list_open_logs` 返回已缓存路径和占用，`close_log` 释放指定缓存或全部缓存。 [MCP 契约](../../LogRAM-mcp/McpServer.cs)

## 刷新与失败恢复

- 桌面实时刷新检查文件增长并追加索引，从变动前的末行继续匹配。文件缩小时，独立 GUI 尝试重开，NPP 暂停实时并提示手动刷新。独立 GUI 重开时先释放旧文档，NPP 手动刷新先打开新副本，峰值内存可能容纳两份正文。同大小替换可通过手动刷新读取。 [GUI](../../LogRAM/MainWindow.xaml.cs) · [NPP 查看器](../../LogRAM.Npp.Viewer/ViewerForm.cs) · [本地实现](../../LogRAM/LogFileDocument.cs)
- MCP 默认读进程内快照。传入 `refresh: true` 后读取追加，发现截断时重建缓存。关闭后再次读取会打开文件并建立新缓存。 [MCP 缓存](../../LogRAM-mcp/McpServer.cs)
- 路径不可读、内存门槛和非法查询会返回可观察的错误。两种桌面入口支持搜索取消，NPP 加载也在分块和索引检查点处理取消。源文件浏览使用只读权限，导出由用户指定目的地。 [领域规则](../DOMAIN.md#规则) · [复验](../TEST_STYLE.md)

## 验收

使用同一个五行样例核对桌面原文定位、CLI JSON 和 MCP 查询及缓存。用已有引擎自检补充 GBK、截断、追加、取消和安全导出。大日志验证使用专门样本和独立逐行计数。 [AC-01](../TEST_STYLE.md#ac-01-本地桌面查看) · [AC-02](../TEST_STYLE.md#ac-02-cli-查询) · [AC-03](../TEST_STYLE.md#ac-03-mcp-查询与缓存) · [历史大文件记录](../../LogRAM.Npp/VALIDATION.md#真实大日志)
