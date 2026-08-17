# AIEverything 市场定位与 Build vs Buy 决策

## 结论

单独做一个“能搜文档正文”的工具没有足够差异，现有产品已经能完成这件事。AIEverything 值得继续做的前提，是坚持以下交集：

1. 原生、轻量、安装后即可使用，不要求管理员权限、Docker、Python 或本地模型。
2. 正文只索引用户明确授权的目录，不默认扫描系统盘或整块磁盘。
3. 一个搜索框统一文件名与正文；没有 Everything 时，授权目录中完成索引的文档仍能工作。
4. 普通用户使用桌面界面，AI Agent 使用同一套权限边界和结构化结果。
5. 返回可验证的文件路径、正文片段和命中来源，而不是做一个不透明的“聊天知识库”。

一句话定位：**给人和 AI Agent 共用的、权限边界清晰的轻量 Windows 本地搜索。**

## 市场与开源项目

| 产品 | 已解决的问题 | 对 AIEverything 的启发 | 是否直接采用 |
|---|---|---|---|
| [Everything](https://www.voidtools.com/support/everything/searching/) | Windows 文件名/路径搜索极快 | 保留其全盘元数据优势；官方说明正文不会被索引，正文查询很慢，因此由 AIEverything 补齐局部正文索引 | 继续使用官方 SDK，作为可选加速器 |
| [Windows Search](https://support.microsoft.com/en-US/Windows/Experience/Performance-Optimization/search-indexing-in-windows) | 系统内置，支持选定位置和正文索引 | 证明“选择范围”是用户能理解的模型；Enhanced 全盘模式会消耗更多资源 | 不作为核心引擎，避免系统版本与索引配置差异 |
| [DocFetcher](https://docfetcher.sourceforge.io/) | 开源桌面全文检索，预建索引后快速查询 | 可借鉴“用户创建索引范围”的心智模型 | 个人使用可直接选；EPL 代码不并入当前核心 |
| [Recoll](https://recoll.org/) | 成熟的跨平台全文检索、格式支持和预览 | 可借鉴预览和高级查询体验 | 个人使用可直接选；GPL 代码不并入当前核心 |
| [Agent Ransack](https://www.mythicsoft.com/agentransack/) | 直接扫描、布尔查询、命中高亮 | 可借鉴命中高亮和高级条件入口 | 不直接集成其闭源实现 |
| [AnyTXT](https://anytxt.net/download/) | 内容索引、片段、预览、OCR、路径/类型过滤与 API | 是“只要搜正文”需求的成熟直接替代品 | 个人立即使用可直接选；产品核心不依赖它 |
| [OmniSearch](https://github.com/Eul45/omni-search) | MIT 开源 Windows 搜索，快速窗口、预览、过滤、文件操作 | 最值得借鉴紧凑布局、预览和操作设计 | 可研究和复用 MIT 许可下的独立思路；不整体 fork，其原始卷访问与全盘架构偏离本产品边界 |
| [File Brain](https://github.com/Hamza5/file-brain) | 本地语义/模糊/跨语言/OCR，覆盖大量格式 | 说明“AI 语义搜索”已有重型方案 | 需要 Python、Docker 和模型，不符合轻量定位；GPL 代码不并入核心 |
| [OpenDocuments](https://github.com/joungminsung/OpenDocuments) | 自托管 RAG、向量+全文混合、引用、MCP 和团队连接器 | 可借鉴 Agent 工具契约、引用和权限设计 | 可研究 MIT 方案；不引入其重型知识库运行时 |

## 用户痛点

### 普通用户

- Everything 找文件名很快，但不知道内容写在哪个文档里。
- 全盘正文索引范围过大，首次索引慢、噪声多，也容易让人担心隐私。
- 开源全文搜索工具通常界面老旧，或需要理解索引、语法、服务等技术概念。
- AI/RAG 工具通常要求 Docker、模型下载、向量数据库，安装和维护成本太高。
- 搜索结果只给文件列表，正文片段和“为什么命中”不清楚。

### AI Agent

- 递归扫描磁盘慢，反复调用成本高，且容易触碰不应读取的目录。
- Agent 不知道哪些目录已获得用户授权，也不知道搜索结果覆盖范围。
- 原生文件搜索通常只返回路径，缺少正文片段、命中来源和稳定的结构化接口。
- 每个 Agent 单独建索引会重复占用 CPU、磁盘和用户配置。

## 产品边界

### 现在要做

- 授权目录的文件名+正文索引，清楚展示覆盖范围。
- Everything 可选加速；未安装时，授权目录中已完成索引的文档仍可按文件名和正文搜索。
- 文件名、正文、综合三种准确语义。
- 快速搜索、可读片段、悬浮完整片段、打开/定位/复制路径。
- 独立安装包与便携包；Plugin/Skill 只作为可选 Agent 适配层。
- Agent 返回路径、片段、命中来源、索引范围/状态。

### 暂时不做

- 全盘正文抓取、原始 NTFS 卷扫描、重复文件管理。
- OCR、图片理解、语义向量、聊天式知识库、团队协作与云同步。
- 用重型模型弥补基础搜索、权限和交互还没做好的问题。

## 可直接复用与不可直接复用

- **继续复用**：Everything SDK 做全盘文件名加速；SQLite FTS5 做授权目录的轻量正文/文件名索引。
- **可借鉴**：OmniSearch 的紧凑搜索布局、结果预览与快捷操作；OpenDocuments 的引用式结果与 MCP 工具契约。
- **可作为用户替代方案**：只为自己搜正文，可直接使用 AnyTXT、DocFetcher 或 Recoll，不必等待 AIEverything。
- **不直接合入**：DocFetcher（EPL）、Recoll/File Brain（GPL）代码。若未来商业化，需要先做完整许可证审查；当前只研究公开行为和交互。

## 下一阶段价值验证

1. 找 5–10 位非技术 Windows 用户，让他们独立完成“安装—添加目录—搜到正文—打开文件”。
2. 记录首次可用时间、索引 CPU/内存、1 万/10 万文档热查询延迟和搜索成功率。
3. 让至少两种 Agent 客户端通过可选连接器完成同一目录搜索，验证权限范围与结果可追溯性。
4. 只有在用户明确出现“关键词找不到但语义相关”的高频痛点后，再评估可选语义索引，而不是默认加入重型依赖。
