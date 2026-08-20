AIEverything 1.0.4（Windows x64）
==============================

所有文件和文件夹都可以按名称/路径搜索；DOCX、TXT、MD、MARKDOWN 还可以搜索正文。

使用：
1. 双击 AIEverything.exe。
2. 新安装会自动在后台索引本机 ready 的 fixed NTFS/ReFS 磁盘，不需要添加目录；文件名搜索不等待正文索引。
3. 搜索结果支持预览、打开、定位和复制路径/位置引用；F5 立即同步正文。
4. 行为排序、本地 ONNX 模型和 DeepSeek 歧义增强默认开启；无 DeepSeek 凭据时自动使用本地排序。

1.0.4 智能排序：
- 只作用于桌面“全部 / 文件名”；正文、CLI、MCP 不变。
- 始终保持 Exact > Eligible > Soft；行为学习和本地模型都只处理基线前 10 项，行为最多提升 10 位，本地模型在其中选择 Top5，第 11 项以后逐项不动。
- ONNX/CPU/运行时/超时失败会回退行为顺序，绝不因此调用云端。
- 打开/复制权重 1.0，定位 0.5，单独预览 0；预览后再打开/复制额外 +0.25。每日聚合按 30 天半衰衰减，候选亲和为 file + 0.30 × parentDirectory + 0.10 × extension。
- ranking.db 只保存随机盐加盐的文件键、父目录键、扩展名和 30 天每日权重聚合；不保存查询、正文、片段或明文路径。清除会删除聚合并轮换随机盐。

正文范围：
- .txt / .md / .markdown 最大 5 MiB；.docx 最大 10 MiB；每个文件最多 1,000,000 字符、处理 15 秒。
- TXT 显示行号；Markdown 显示标题路径和行号；Word 显示标题路径、段落或表格坐标。
- Word 暂不索引页眉页脚、批注、修订、脚注、文本框或嵌入对象，不承诺页码或自动跳转到命中位置。
- 过滤系统/安装/缓存/依赖/构建目录和整个代码仓库。
- 过滤只影响正文；所有格式和代码仓库仍可按文件名找到。
- Everything 不可用时不会递归爬全盘，已有正文结果保持不变并等待重试。

本地存储：
正文保存在 %LOCALAPPDATA%\AIEverything\content.db，SQLite 索引未单独加密。
行为聚合保存在 %LOCALAPPDATA%\AIEverything\ranking.db。
Outlook 邮件索引保存在 %LOCALAPPDATA%\AIEverything\mail.db，默认开启。

Classic Outlook 邮件搜索：
- 启动后在后台只读同步默认收件箱和已发送中最近 100 封邮件；无需手动开启。
- “全部 / 正文”包含邮件；“文件名”不包含邮件。双击邮件可打开 Outlook 原邮件。
- 只保存主题、人员、时间、文件夹、纯文本正文和附件名称；不读取附件正文。
- 不发送、删除、移动或修改邮件，不轮询邮箱；关闭后不再展示邮件结果，清除只删除本机邮件索引。
- Outlook 不可用时仅在设置中显示原因，不影响文件名和文件正文搜索。

可选 DeepSeek：
DeepSeek 默认启用。只有 Windows 凭据管理器已有凭据、本地 ONNX 成功且分数完整、无 Exact、至少 3 个 Eligible，并出现分数接近/重复名称/名称与正文混合证据/自然语言歧义之一时才可能请求 api.deepseek.com；明确文件名、引号或路径式查询零网络。没有凭据时静默使用本地排序。发送当前查询和本地前 10 项候选的 ID、文件名、完整路径及每项最多 200 字片段；不发送匹配来源或排序层级，不上传文件本体或行为历史。总预算 1.5 秒、请求体最大 24 KiB、单并发、滚动每分钟最多 10 次、会话缓存 10 分钟；限流或连续服务失败会熔断 30 秒且不重试。设置页用遮罩输入把密钥保存/更新到 Windows 凭据管理器 AIEverything/DeepSeek；settings.json、ranking.db 与日志不保存密钥。发布版不读环境变量，调试版才可回退 AIEVERYTHING_DEEPSEEK_API_KEY。详见 PRIVACY.md。

除上述用户显式开启的可选复排外，搜索与排序不联网。AIEverything 不会修改、删除或移动用户文件。

1.0.4 没有 Local Import、剪贴板、Teams/WeChat 或手工目录入口。
PDF、旧版 DOC、RST、XLSX、PPTX 正文支持不在本版范围。

反馈：https://github.com/stableopen/AIEverything/issues/new
