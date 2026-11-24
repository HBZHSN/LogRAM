# 桌面组件索引

登记承担独立交互契约、宿主集成或多处复用的界面部件。WPF 和 WinForms 分别维护自己的控件实现，共用文档和查询行为由核心层提供。 [新增登记模板](components/_TEMPLATE.md) · [界面规范](FRONTEND_STYLE.md)

| 部件 | 用途与使用约定 | 源码与说明 |
| --- | --- | --- |
| `MainWindow` | WPF 日志工作区，组织打开标签、原文页、搜索结果、键盘导航和内存状态。 | [XAML](../LogRAM/MainWindow.xaml) · [交互实现](../LogRAM/MainWindow.xaml.cs) · [GUI 使用](../README.md#gui-使用) |
| `ViewerForm` | WinForms 宿主内工作区，接受 NPP 文件和主题命令，组织只读原文、结果和五组操作。 | [实现](../LogRAM.Npp.Viewer/ViewerForm.cs) · [插件用法](../LogRAM.Npp/README.md) |
| `SshOpenDialog` 和 `SshOpenForm` | 各入口选择 SSH 主机、连接、按前缀浏览并打开路径。对话框释放未交给文档的连接。 | [WPF](../LogRAM/SshOpenDialog.cs) · [WinForms](../LogRAM.Npp.Viewer/SshOpenForm.cs) · [SSH 流程](impl-spec/002-ssh-log.md) |
| `ToolbarIcons` | NPP 查看器工具栏按当前前景色生成操作图标。按钮表意由查看器的 Tooltip 和无障碍名称提供。 | [绘制](../LogRAM.Npp.Viewer/ToolbarIcons.cs) · [按钮语义](../LogRAM.Npp.Viewer/ViewerForm.cs) |
