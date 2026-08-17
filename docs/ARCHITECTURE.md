# Architecture

## 目标

AIEverything 将快速的文件名召回、本地正文定位和受约束的智能排序组合在一个 Windows 桌面搜索界面中。架构优先保证：文件名搜索立即可用、正文处理后台运行、失败可降级、用户源文件只读。

## 组件

| 项目 | 责任 |
|---|---|
| `AIEverything.App` | WPF 主窗口、设置、预览和结果操作 |
| `AIEverything.Desktop` | 桌面搜索协调、偏好、行为学习、本地/云端重排 |
| `AIEverything.Server` | Everything SDK、查询模型、CLI 和可选只读 MCP 接口 |
| `AIEverything.Content` | 正文策略、提取、FTS5 存储、协议和位置解析 |
| `AIEverything.Daemon` | 候选同步、队列、后台索引和命名管道服务 |
| `AIEverything.ExtractorWorker` | 低优先级隔离提取进程 |

## 搜索数据流

1. 桌面端对输入做防抖，并为每次查询分配版本；过期结果不会覆盖新查询。
2. 文件名/路径分支调用 Everything SDK，分页读取名称、路径和属性。
3. `NoiseAwareEverythingSearch` 删除高置信临时噪音，将系统、依赖、缓存和构建结果放入 Soft 层；精确文件名或完整路径查询可以恢复 Soft 结果。
4. 正文分支查询本机 SQLite FTS5，返回片段和源位置。
5. `StandaloneSearchService` 合并证据，同一文件只保留一个结果。
6. 桌面排序协调器在 `Exact > Eligible > Soft` 保护层内应用行为偏好、本地模型和可选云端补丁。

Everything 不可用时，应用保留已有正文索引并等待服务恢复，不退化为递归全盘扫描。

## 正文索引

`MachineTextIndexPolicy` 自动选择已就绪的本地固定 NTFS/ReFS 卷，并仅接受 `.txt`、`.md`、`.markdown`。通过 Everything 查询得到候选和仓库/排除目录标记，再应用：

- 系统、安装、缓存、依赖、构建和其他用户目录排除；
- `.git`、`.hg`、`.svn`、`.jj` 所在整个仓库排除；
- hidden、system、offline、temporary、cloud placeholder、reparse 属性排除；
- 5 MiB 和 1,000,000 字符上限；
- UTF-8、UTF-8 BOM、带 BOM 的 UTF-16 LE/BE 编码边界。

候选使用 `scan_id` 快照同步；完整扫描成功后才收敛删除。后台每 60 秒对账，F5 触发显式同步。TXT 使用行号，Markdown 使用最近 ATX 标题路径和行号。

## 排序

- 确定性基线负责精确命中、正文证据、普通/Soft 分层和稳定顺序。
- 行为库按 30 天半衰期聚合打开、定位和复制动作，只存加盐后的身份键。
- `cross-encoder/mmarco-mMiniLMv2-L12-H384-v1` 在后台处理本地前 10 项，仅更新前 5 项。
- DeepSeek 是默认关闭的低置信歧义增强；只能返回已有候选 ID，超时、非法响应或网络失败完整回退本地顺序。
- 查询变化或用户开始交互后，迟到的模型补丁会被丢弃。

## 本地数据

| 路径 | 内容 |
|---|---|
| `%LOCALAPPDATA%\AIEverything\content.db` | 提取正文、FTS5、同步队列与失败状态 |
| `%LOCALAPPDATA%\AIEverything\ranking.db` | 加盐身份键、扩展名、日期桶和行为权重 |
| `%LOCALAPPDATA%\AIEverything\settings.json` | UI 和功能开关，不保存搜索词或 API Key |
| Windows Credential Manager `AIEverything/DeepSeek` | 可选 DeepSeek 凭据 |

这些数据库未单独加密。应用没有删除用户源文件的功能。

## 外部依赖边界

Everything 引擎和 SDK 按其 MIT 许可分发。本地 MiniLM 模型固定到明确 revision 和 SHA-256，源码仓库不提交 118.6 MB ONNX 文件；构建前由 `scripts/fetch-model.ps1` 获取。完整清单见 `THIRD_PARTY_NOTICES.md`。

可选 MCP/Skill 适配器不随桌面发布包安装，只暴露搜索和状态读取能力。
