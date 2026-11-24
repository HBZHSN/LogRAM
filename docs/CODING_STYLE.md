# 代码约定

- 共用搜索、编码、索引和 SSH 行为沿用 `LogRAM.Core` 的实现和链接编译布局。入口负责自身参数、退出码、展示和生命周期。修改共用语义时检查四个入口的调用和返回。 [架构](ARCHITECTURE.md) · [核心项目](../LogRAM.Core/LogRAM.Core.csproj)
- C# 延续当前文件的命名空间、可空声明和命名方式。局部状态在相应窗口、会话或命令中管理。格式化和翻译使用 `Loc` 与现有双语路径。 [本地化](../LogRAM/Localization.cs) · [界面规范](FRONTEND_STYLE.md)
- 文档和会话沿用 `IDisposable` 的释放入口。打开、刷新、搜索或取消的改动核对文档状态、行索引、结果缓存及已启动的后台进程。 [文档接口](../LogRAM.Core/ILogDocument.cs) · [领域规则](DOMAIN.md#规则)
- NPP DLL 内的宿主操作保留在 `LogRAM.Npp`，日志界面和缓存保留在 WinForms 查看器。宿主消息和架构兼容性以便携 NPP 的集成回归为据。 [宿主决策](ADR/002-notepad-host-worker.md) · [验证](TEST_STYLE.md#界面与真实环境)
- 远端工作进程沿用 Python 3.7 兼容的标准库。协议字段、错误响应和取消语义连同 C# 适配层一并核对。 [远程规格](impl-spec/002-ssh-log.md) · [协议测试](../scripts/test_ssh_agent.py)
