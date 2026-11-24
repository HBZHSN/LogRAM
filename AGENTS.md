# LogRAM 维护指南

接手任务先了解使用入口、当前任务和验证记录。当前基线对应 1.4.0，范围依据见访谈记录。 [项目首页](README.md) · [任务与验证](docs/TASKS.md) · [确认记录](docs/INTAKE.md)

| 任务 | 先读 |
| --- | --- |
| 产品与用户可见行为 | [产品范围与验收](docs/PRODUCT.md) · [领域规则](docs/DOMAIN.md) |
| 查询、编码、缓存、追加或刷新 | [本地日志实现规格](docs/impl-spec/001-local-log.md) · [代码约定](docs/CODING_STYLE.md) |
| SSH 进程、协议、内存预算或远程浏览 | [架构](docs/ARCHITECTURE.md) · [SSH 决策](docs/ADR/001-ssh-remote-worker.md) · [SSH 实现规格](docs/impl-spec/002-ssh-log.md) |
| WPF 或 Notepad++ 界面 | [界面规范](docs/FRONTEND_STYLE.md) · [组件索引](docs/COMPONENTS.md) · [插件用法](LogRAM.Npp/README.md) |
| 宿主接管、测试或发布 | [宿主决策](docs/ADR/002-notepad-host-worker.md) · [复验步骤](docs/TEST_STYLE.md) · [历史宿主验证](LogRAM.Npp/VALIDATION.md) |

开始改动时根据任务核对用户可观察的结果、兼容性和选用的验收环境。影响进程、信任、部署或协议边界的取舍记入 ADR。跨入口的复杂流程和契约变更用实现规格记录调用关系、异常恢复和复验。 [ADR 模板](docs/ADR/_TEMPLATE.md) · [实现规格模板](docs/impl-spec/_TEMPLATE.md)

随行为变更同步对应的领域规则、规格、入口说明和验收，保留确认依据与历史验证日期。在任务记录写明本次执行的命令、环境、结果和待验证项。使用专用便携目录执行 NPP 回归，对真实 SSH 使用隔离样例。任务完成后需提交代码，并按变更内容依次分批提交；发布按当前任务的授权处理。
