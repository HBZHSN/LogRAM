# LogRAM Notepad++ 插件 1.3.4 验证记录

验证日期：2026-10-13。Windows，Notepad++ 8.9.8.1 专用便携版；测试未安装到日常使用的 NPP。

## 已通过

- x64 / x86 原生插件编译、自包含发布、引擎自检、WinForms 界面自检。
- 实际 NPP 加载插件与嵌入后台窗口；原文在上、结果在下，普通 NPP 编辑区隐藏。
- 移除顶部文件工具栏；搜索框及搜索选项、刷新、实时、行号、跳转、结果导航共用下方结果区顶部的一行。界面自检和实际控件位置断言通过。
- 1.3.4 将八个常用操作改为图标，搜索选项采用 `.*` / `Aa`；工具栏分五组，竖线分隔，保留悬停说明和无障碍名称。深浅主题图标渲染及按钮操作通过自检和实际 NPP 回归。
- 文件打开、最近文件、标签切换与关闭使用 NPP；插件打开菜单也调用 NPP 标准对话框。Ctrl+O / Ctrl+W 的宿主转发已通过界面自检。
- 实际 NPP 的 GB2312 / UTF-8 菜单改变查看器解码；GBK 中文查询通过，源文件哈希不变，NPP 占位文档保持未修改。
- 中文 / 英文界面随 NPP 语言设置，x64 浅色中文界面及 x86 深色英文界面通过实际 NPP 回归。
- 启动命令中的 `.LOG` 自动显示 LogRAM；NPP 自带的文件打开对话框选择 `.log` 时，在标准正文加载前分流到 LogRAM。
- 原生自检覆盖正文读取返回空数据、普通文本读写不变、禁止覆盖已接管源日志、还原宿主入口、未保存快照备份保留真实内容、原生重载提示时间戳保持稳定。
- 中文路径、普通 / 正则 / 大小写 / 包含排除组合查询、UTF-8 / GBK、进度、取消。
- 多文件独立保存搜索条件、结果和浏览位置；重复打开选择已有缓存。
- 实时追加、最后一行重新匹配、行号跳转、结果上下文、窗口缩放。
- GUI 导出生成全部匹配行；引擎自检覆盖禁止覆盖源日志、取消保留原目标及临时文件清理。
- 返回 NPP 保留原文档的未保存修改；NPP 新建文件时恢复普通编辑区。
- 释放全部日志、后台进程重启、重启后再次打开、NPP 关闭后工作进程退出。
- 界面自检覆盖工作区快捷键及修改后的键组合；原生端按 NPP 快捷键 API 和通知同步设置。
- 配置持久化、空值恢复、同时保存、临时文件清理及日志截断检测。

## 真实大日志

使用本机约 3 GB 的只读日志样本。

| 项目 | 结果 |
| --- | --- |
| 文件大小 | 3,059,976,898 字节 |
| 行数 | 21,217,913 |
| 引擎加载 | 3.674 秒 |
| error 搜索 | 1 条，0.072 秒 |
| SendOrder 搜索 | 1,171 条，0.045 秒 |
| 独立逐行读取校验 | 行数与两组搜索数量一致；文件大小和修改时间未变 |
| 最终 NPP 回归 | 从 NPP 普通“文件 → 打开”加载，行数与搜索结果一致；正则取消后关闭日志标签，恢复此前文件的搜索缓存 |
| 1.3.3 加载路径证明 | 普通 NPP 编辑器缓冲区只有 105 字节的只读提示；NPP 工作集 77.5 MiB，LogRAM 后台工作集 3,514.8 MiB |

时间为本机测量，受磁盘、系统缓存、内存和 CPU 影响，不是性能保证。
上表大日志实测来自 1.3.3；1.3.4 仅调整工具栏图标与分组，未重复进行大日志压力测试。

## 使用边界

- `.log` / `.LOG` 默认由 LogRAM 读取与显示；其他文件仍使用普通 NPP 编辑器，通过 Open current saved file 可手动查看其磁盘内容。
- 已有未保存的日志快照保留在 NPP 中；手动 Open current saved file 读取其磁盘版本。
- 自动分流修改当前宿主的文件读取入口，仅在加载通知期间识别源日志；未验证其他插件同时改写相同入口的组合。
- 数 GB 日志使用 x64；x86 后台进程受 32 位地址空间限制。
- ARM64 未编译及实机验证；未验证其他 NPP 版本或多显示器 DPI 切换。
- 文件整体驻留内存并建立索引；刷新时先加载新副本，峰值可能接近两份日志。
- 同大小文件替换不自动跟踪；截断后需要手动刷新。无编辑、替换或源文件保存功能。

接口定义核对：[PluginInterface.h](https://github.com/notepad-plus-plus/notepad-plus-plus/blob/v8.9.8.1/PowerEditor/src/MISC/PluginsManager/PluginInterface.h)、[Notepad_plus_msgs.h](https://github.com/notepad-plus-plus/notepad-plus-plus/blob/v8.9.8.1/PowerEditor/src/MISC/PluginsManager/Notepad_plus_msgs.h)。

## 复验

```powershell
.\scripts\build-npp.ps1 -Architecture x64
.\scripts\install-npp.ps1 -NotepadDirectory 'C:\path\portable-npp' -Architecture x64
.\scripts\test-npp.ps1 -NotepadDirectory 'C:\path\portable-npp' `
  -AutoLog -LargeLog 'C:\path\bt_tigerie.exe-20261009T160011.log' -ExpectedLines 21217913 -ExpectedMatches 1
```

自检随构建执行。NPP 回归只能指向专用的便携目录，脚本会调整其中的主题、窗口和文件检测设置。
