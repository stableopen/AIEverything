# Security Policy

## 支持范围

当前仅维护最新的 `1.x` 版本。旧的实验性 `0.x` 包不再接收安全修复。

## 报告漏洞

请不要在公开 Issue 中披露未修复漏洞、真实密钥、私人文件路径或可复现的敏感数据。优先使用仓库的 GitHub Private Vulnerability Reporting；如果该功能尚未开启，请通过维护者 GitHub 主页提供的私密联系方式报告。

报告中请包含受影响版本、最小复现步骤、影响范围和建议缓解方式。请使用测试数据，不要附加真实个人文档或凭据。

## 安全边界

- AIEverything 会读取本机文件名、DOCX/TXT/Markdown 正文以及 Classic Outlook 最近邮件，用于建立本地搜索索引。
- `content.db`、`mail.db` 和 `ranking.db` 位于当前用户的 `%LOCALAPPDATA%\AIEverything`，未单独加密；操作系统账户权限和磁盘加密仍是主要保护边界。
- DeepSeek 歧义增强默认启用，但没有凭据时不会联网；凭据存储在 Windows Credential Manager。源码、设置、数据库和日志不应包含 API Key。
- 项目不应修改、删除、移动或上传用户源文件。

如怀疑密钥已经进入提交历史，请先撤销/轮换密钥，再清理历史；仅删除当前文件不足以使旧密钥失效。
