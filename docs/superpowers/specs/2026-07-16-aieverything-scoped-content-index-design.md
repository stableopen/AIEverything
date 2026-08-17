# AIEverything 局部正文索引与产品化设计

**日期：** 2026-07-16

**状态：** 已确认方向，书面规格待用户复核
**当前实施周期：** v0.2 局部正文搜索引擎

## 1. 产品结论

AIEverything 继续使用 Everything 负责文件名、路径和文件元数据搜索，同时增加一套只读取用户明确授权目录的正文索引。正文引擎不扫描整个磁盘，不默认读取 C 盘系统目录，不依赖 Everything 1.5 测试版，也不把文件内容上传到网络。

最终产品由四层组成：

1. Everything 元数据搜索层。
2. AIEverything 局部正文索引层。
3. 统一搜索服务层，为文件名、正文和混合搜索提供一致接口。
4. 面向 Codex、其他 MCP 客户端、桌面 UI 和后续 REST SDK 的连接层。

当前只实现第 2、3 层以及 MCP/CLI 接口。桌面 UI、安装器和公开更新系统分别进入 v0.3 和 v1.0，避免首轮范围过大。

## 2. 已验证的技术前提

Everything 1.4 支持 `content:` 查询，但官方明确说明正文不会被索引，内容搜索会非常慢，必须先通过文件名、日期等条件缩小范围。Everything 1.5 增加正文索引，但当前仍属于 1.5 测试版路线，正文索引还会增加内存占用。因此 v0.2 不把 Everything 1.5 作为运行前提。

参考：

- https://www.voidtools.com/en-us/support/everything/searching/
- https://voidtools.com/support/everything/options/
- https://www.voidtools.com/everything-1.5/

## 3. 方案选择

### 方案 A：依赖 Everything 1.5 内容索引

优点是代码少、接入快。缺点是要求普通用户使用测试版，正文提取依赖系统 iFilter，内存、稳定性和错误恢复不受 AIEverything 控制。该方案不作为产品基础。

### 方案 B：Everything 1.4 + AIEverything 局部正文索引

Everything 保持文件定位优势，AIEverything 只索引授权目录的正文。它能独立控制支持格式、资源占用、中文分词、隐私、错误恢复和版本升级。该方案是最终选择。

### 方案 C：查询时临时读取正文

无需保存索引，但每次查询都要重新打开和解析文件，延迟不可预测，对 PDF 和 Office 文件尤其明显。它只适合作为未索引文件的显式慢速回退，不是默认搜索路径。

## 4. 目标与非目标

### v0.2 目标

- 只索引用户明确添加的本地目录。
- 支持文本、代码、PDF 和现代 Office 文件。
- 提供中文、英文关键词正文搜索。
- 提供文件名与正文的混合排序。
- 支持增量更新、暂停、恢复、重建和删除索引目录。
- 索引进程保持低优先级，不明显影响前台工作。
- 通过 CLI 和 MCP 暴露稳定接口。
- 保持现有三个 Everything 工具兼容。

### v0.2 非目标

- 不索引整个磁盘或盘符根目录。
- 不支持 UNC、NAS、可移动磁盘和云端占位文件。
- 不支持旧版 `.doc/.xls/.ppt`、压缩包、邮件、音视频和数据库正文。
- 不做图片 OCR、音频转写、向量语义搜索或云端同步。
- 不提供桌面 UI、公开网站、应用商店或跨平台版本。
- 不开放 HTTP 端口。

## 5. 授权目录与安全边界

### 添加目录

目录必须满足以下全部条件：

- 是已存在的绝对 Windows 本地目录。
- 不是 `C:\`、`D:\` 等盘符根目录。
- 不是 Windows、Program Files、ProgramData、AppData、回收站或系统卷信息目录。
- 不是 UNC 路径、符号链接、junction 或其他 reparse point。
- 当前用户具有读取权限。

首次运行时没有任何默认正文索引目录。产品可以建议 Documents、Desktop 或项目目录，但必须由用户主动确认。

### 默认排除

每个授权根目录都默认排除：

- `.git`、`.svn`、`.hg`
- `node_modules`、`.venv`、`venv`
- `bin`、`obj`、`target`、`dist`、`build`
- `.cache`、`.idea`、`.vs`
- 临时文件、隐藏系统文件和带 `RecallOnDataAccess` 属性的云端占位文件

用户可以增加排除规则，但 v0.2 不允许取消系统目录和 reparse point 的硬排除。

### 数据位置

配置、队列和索引数据库保存在 `%LOCALAPPDATA%\AIEverything\`，目录 ACL 仅允许当前用户访问。数据不上传、不进入 Windows 漫游配置、不主动备份。删除授权根目录时，必须同步删除该根目录对应的正文、词项和失败记录。

v0.2 的 SQLite 索引不额外加密，依赖当前用户 ACL 和设备磁盘加密。桌面公开版在 v0.3 评估 SQLCipher；在此之前文档必须明确说明本地索引含有提取后的文本。

## 6. 进程与组件边界

### `AIEverything.Daemon`

每个 Windows 用户只运行一个后台实例，负责：

- 持有 SQLite 数据库的唯一写连接。
- 管理授权根目录、初始索引、增量队列和恢复状态。
- 在授权目录内枚举候选文件，并在 Everything 可用时获取元数据和加速校验。
- 调度独立提取工作进程。
- 执行正文搜索和混合排序。
- 通过当前用户专用 named pipe 接受本地请求。

named pipe 名称包含当前用户 SID 的哈希，ACL 只允许当前用户。协议使用四字节长度前缀加 UTF-8 JSON；单个请求上限 1 MiB，单个连接只处理一个请求，降低协议和并发复杂度。

### `AIEverything.ExtractorWorker`

独立短生命周期进程，每次处理一个文件。Daemon 传入规范化路径、最大字符数和超时时间，Worker 返回提取文本或结构化失败。单文件默认超时 30 秒；超时、崩溃或取消时终止 Worker，不影响 Daemon。

### `AIEverything.Server`

现有 CLI/MCP 可执行文件变为轻量连接器：

- 现有 Everything 元数据工具保持兼容。
- 正文和混合搜索请求转发给 Daemon。
- Daemon 不可用时，元数据搜索仍可工作；正文工具返回稳定错误，不静默退化成全盘扫描。

### `AIEverything.Content`

无进程状态的类库，包含根目录策略、文本提取契约、中文/英文分词、SQLite schema、查询模型和排名逻辑。Daemon 和测试共同使用该库。

## 7. 索引数据流

### 初始索引

1. 用户通过 CLI 或 MCP 添加授权目录。
2. Daemon 验证路径和硬排除规则。
3. 优先使用 Everything 按授权路径和支持扩展名分页枚举候选文件；Everything 不可用或未覆盖该目录时，回退到严格限制在授权根目录内的 `Directory.EnumerateFiles`，且不跟随 reparse point。
4. 过滤超大文件、系统属性、云端占位文件和排除目录。
5. 以 `规范化路径 + 文件大小 + UTC 修改时间` 作为版本指纹。
6. 未索引或已变化的文件进入持久队列。
7. ExtractorWorker 提取正文并应用字符上限。
8. Daemon 在事务中更新文档表和 FTS 表。

### 增量更新

- 每个授权根目录配置递归 `FileSystemWatcher`。
- 事件经过 750 ms 去抖后进入持久队列。
- 删除和重命名立即清理旧路径。
- 每 10 分钟进行一次增量校验，优先使用 Everything 元数据；Everything 不可用时只重新枚举授权根目录，修复 watcher 丢失事件。
- Daemon 异常退出后从持久队列恢复，已经完成且指纹未变化的文件不会重复提取。

### 资源控制

- Daemon 和 Worker 使用 `BelowNormal` 进程优先级。
- 默认同时只运行一个 ExtractorWorker。
- 每 50 个文档或每 2 秒提交一次数据库事务，以先到者为准。
- 默认最大文件大小 20 MiB，最大提取文本 2,000,000 字符。
- 用户暂停后不启动新任务，正在处理的单文件完成后停下。
- v0.2 不自动判断电池或 CPU 负载；该能力进入 v0.3 桌面产品。

## 8. 支持格式

### 直接文本

支持：`.txt`、`.md`、`.csv`、`.tsv`、`.json`、`.xml`、`.yaml`、`.yml`、`.log`、`.ini`、`.config`、`.cs`、`.py`、`.js`、`.ts`、`.tsx`、`.jsx`、`.java`、`.go`、`.rs`、`.sql`、`.ps1`、`.sh`、`.bat`。

识别 UTF-8、UTF-8 BOM、UTF-16 LE/BE；无 BOM 且 UTF-8 校验失败的文件记录为 `UNSUPPORTED_ENCODING`，v0.2 不猜测本地代码页。

### PDF

使用纯托管 .NET PDF 文本提取库。扫描版 PDF 提取为空时标记为 `OCR_REQUIRED`，不自动 OCR。

### Office Open XML

支持 `.docx`、`.xlsx`、`.pptx`。提取正文、单元格文本、幻灯片文字和必要的标题信息；不执行宏、不加载外部链接、不解析嵌入对象。

## 9. 存储与中文搜索

SQLite 使用 WAL 模式、单写者和事务批处理。核心表包括：

- `roots`：授权目录和用户排除规则。
- `documents`：路径、扩展名、大小、修改时间、提取状态和原始提取文本。
- `content_fts`：标题词项和正文词项。
- `index_queue`：可恢复任务及重试时间。
- `index_failures`：稳定错误码、文件版本和最近失败信息。

全文索引使用 SQLite FTS5 `unicode61 remove_diacritics 2`。在写入 FTS 前由应用层预分词：

- 拉丁字母按 Unicode 单词归一化并转小写。
- 连续中文、日文、韩文字符生成重叠双字词。
- 数字和常见技术标识符保留。
- 单个 CJK 字符查询因结果过宽而返回 `QUERY_TOO_BROAD`。

原始提取文本保存在 `documents` 中，用于返回真实命中片段；FTS 中保存预分词文本用于检索。排名使用 FTS5 BM25，标题权重 8，正文权重 1。

## 10. 查询与结果合并

### 正文搜索

`ContentSearchRequest` 支持：查询文本、授权根目录、扩展名、修改时间范围、limit 和 offset。默认 limit 20，最大 100。结果返回：路径、文件名、扩展名、修改时间、正文片段、得分、索引状态。

### 混合搜索

混合搜索并行执行：

- Everything 文件名/路径查询，最多取前 50 项。
- 正文 FTS 查询，最多取前 50 项。

使用 Reciprocal Rank Fusion 合并，常数 `k=60`。完全文件名命中额外乘 1.5，标题正文命中额外乘 1.2。结果标记 `name`、`content` 或 `both`，便于 Agent 解释命中来源。

### 命中片段

对最终前 20 项在原始提取文本中定位查询词，返回命中前后各最多 120 个字符。找不到直接字符串但 FTS 词项命中时，返回最早命中词项附近片段。片段总长度上限 320 字符。

## 11. CLI 与 MCP 接口

新增 CLI：

- `content-root add <absolute-path>`
- `content-root remove <absolute-path>`
- `content-root list`
- `content-index status`
- `content-index pause`
- `content-index resume`
- `content-index rebuild <absolute-path>`
- `content-search <query>`
- `hybrid-search <query>`
- `daemon run`

所有输出保持 camelCase JSON。

新增 MCP 工具：

- `search_local_content`
- `search_local_hybrid`
- `aieverything_index_status`
- `aieverything_manage_roots`

`aieverything_manage_roots` 只允许 `list/add/remove`，不允许传入盘符根目录。添加和删除属于本机状态修改，必须在 MCP 注解中声明 `ReadOnly=false`；三个查询/状态工具保持只读。

## 12. 稳定错误码

- `CONTENT_SERVICE_UNAVAILABLE`：Daemon 未运行。
- `CONTENT_INDEX_NOT_CONFIGURED`：没有授权目录。
- `ROOT_NOT_FOUND`：目录不存在。
- `ROOT_NOT_ALLOWED`：盘符根、系统目录、UNC 或 reparse point。
- `UNSUPPORTED_FILE_TYPE`：格式不在支持范围。
- `UNSUPPORTED_ENCODING`：文本编码不受支持。
- `FILE_TOO_LARGE`：超过大小上限。
- `OCR_REQUIRED`：PDF 没有可提取文本。
- `EXTRACTION_TIMEOUT`：单文件超过 30 秒。
- `EXTRACTION_FAILED`：解析器失败。
- `CONTENT_INDEX_BUSY`：正在迁移或重建。
- `CONTENT_INDEX_CORRUPT`：SQLite 完整性检查失败。
- `QUERY_TOO_BROAD`：空查询或单个 CJK 字符查询。

单文件失败不会停止整个索引；同一文件版本最多自动重试 3 次，退避时间为 1 分钟、10 分钟、1 小时。文件版本变化后重置失败计数。

## 13. 性能与容量目标

验收机必须记录 CPU、磁盘、索引大小和查询延迟，不用估算代替实测。

- 10,000 个平均 100 KiB 提取文本的文档可以完成索引。
- 默认单 Worker 时前台电脑保持可用，无持续满核。
- 已预热正文查询 median `<100 ms`，P95 `<250 ms`。
- 混合查询 median `<200 ms`，P95 `<400 ms`。
- 文件变化后 30 秒内可搜索。
- Daemon 异常退出重启后不丢队列，不全量重建。
- 索引磁盘大小目标不超过已提取 UTF-8 文本总量的 2.5 倍；超过时如实报告。

## 14. 测试策略

### 单元测试

- 根目录允许/拒绝矩阵。
- 路径规范化、嵌套根和排除规则。
- 中英文预分词和过宽查询。
- 文件版本指纹和队列去重。
- BM25 与 RRF 排名。
- 稳定错误码和 MCP 注解。

### 提取器契约测试

仓库保存最小、无敏感信息的 TXT、UTF-16、PDF、DOCX、XLSX、PPTX fixture。每种格式验证正文、字符上限、取消和损坏文件行为。

### 集成测试

- 临时授权目录的初始索引。
- 新增、修改、重命名、删除和 watcher 丢事件后的 reconcile。
- Daemon named pipe 握手、权限和超大请求拒绝。
- MCP 正文搜索和混合搜索真实协议调用。
- Worker 超时、崩溃和 Daemon 重启恢复。
- SQLite `integrity_check` 和损坏数据库隔离重建。

### 性能测试

生成固定种子的 10,000 文档语料，分别测量英文、中文、路径过滤和混合搜索。性能失败不通过修改目标掩盖，必须在报告中保留原始结果。

## 15. 产品化路线

### v0.2：局部正文引擎

交付 Daemon、ExtractorWorker、SQLite 索引、CLI、MCP、测试和基准。开发者可以启动 Daemon 并配置目录；现有 Codex Plugin 能调用正文工具。

### v0.3：普通用户桌面产品

单独设计 WPF 托盘应用和 Windows 安装器，提供首次启动向导、目录选择、进度、暂停、重建、Everything 检测/可选安装、开机启动和一键配置常见 MCP 客户端。该阶段不得改变 v0.2 的索引和 IPC 契约，只通过公开接口管理 Daemon。

### v1.0：公开分发

增加代码签名、自动更新、隐私文档、崩溃恢复遥测的本地开关、公开下载渠道、静默安装和企业配置。跨平台需要替换 Everything 元数据后端，作为独立项目处理。

## 16. v0.2 完成标准

- 只有显式授权目录被读取和索引。
- 盘符根、系统目录、UNC 和 reparse point 有自动化拒绝测试。
- 支持格式均有真实 fixture 和提取结果。
- 52 项现有测试继续通过，新增正文测试全部通过。
- Daemon、Worker、CLI、MCP 和索引恢复链路通过真实集成测试。
- 真实授权目录可以完成索引并返回文件名、正文片段和混合结果。
- 完成 10,000 文档基准并记录实际指标。
- README、隐私边界、安装前提和已知限制与实际行为一致。
