# LogRAM 任务

### T-001

补记 1.4.0 已发布能力。基线对应提交 `dd14e04`，文档补记日期为 2026-11-24。

| 工作 | 状态 | 依据 |
| --- | --- | --- |
| 确认基线范围 | 已确认 | [Q-01](INTAKE.md#q-01) · [Q-02](INTAKE.md#q-02) |
| 补记产品、领域规则和验收 | 已完成 | [产品](PRODUCT.md) · [领域规则](DOMAIN.md) · [复验](TEST_STYLE.md) |
| 补记架构、主要取舍和实现规格 | 已完成 | [架构](ARCHITECTURE.md) · [ADR-001](ADR/001-ssh-remote-worker.md) · [ADR-002](ADR/002-notepad-host-worker.md) · [本地规格](impl-spec/001-local-log.md) · [远程规格](impl-spec/002-ssh-log.md) |
| 建立维护与界面导航 | 已完成 | [维护指南](../AGENTS.md) · [界面规范](FRONTEND_STYLE.md) · [组件索引](COMPONENTS.md) |
| 校验文档与验收追踪 | 已通过 | 核对 21 份 Markdown 中的 219 个相对链接和图片，检查锚点、README 可达性及 P-01 至 P-05 的验收映射。 |

### 本次复验

2026-11-24 在 Windows 执行，目标框架为 `net8.0-windows`。使用 .NET SDK 11.0.100-rc.1.26425.128、.NET Desktop Runtime 8.0.29 和 Python 3.14.3。 [命令](TEST_STYLE.md#已有自检命令)

| 验证 | 结果 |
| --- | --- |
| 解决方案 Release x64 构建 | 五个托管项目构建通过，0 错误，0 警告。 |
| Python 工作进程测试 | 11 项通过。 |
| CLI SSH 自检 | 本机工作进程的配置、启动、分页、搜索、追加、限制、取消和错误恢复通过。 |
| NPP 查看器引擎自检 | 通过编码、查询、SSH 会话、取消、追加、导出与截断检查。 |
| MCP 自检 | 通过组合查询、上下文、缓存与追加刷新检查。 |
| 五行样例，AC-02 与 AC-03 | 实际 CLI JSON 和 MCP stdio 返回一致，命中第 2、4 行。进一步确认按行读取、缓存命中、关闭释放及源文件字节一致。 |

### 历史证据与复验范围

- 1.4.0 的 NPP 宿主、界面与原生包证据来自 2026-11-09 的 Notepad++ 8.9.8.1 x64/x86 便携版记录。约 3 GB 的对照计数与性能来自 1.3.3。 [插件验证](../LogRAM.Npp/VALIDATION.md)
- 桌面界面、真实 NPP 宿主、真实 OpenSSH 连接和大日志实测本次未重跑。对应环境与步骤见复验指南。 [界面与真实环境](TEST_STYLE.md#界面与真实环境)
- ADR 的备选方案按代码和历史记录补记。原始决策方及当时的方案比较待核实，已实施的行为以 1.4.0 为据。 [SSH 决策](ADR/001-ssh-remote-worker.md) · [宿主决策](ADR/002-notepad-host-worker.md)
