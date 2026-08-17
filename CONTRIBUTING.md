# Contributing to AIEverything

感谢你愿意改进 AIEverything。项目优先保持一个小而可靠的 Windows 本地搜索闭环。

## 开始之前

1. 阅读 [README.md](README.md) 的范围和限制。
2. 阅读 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)，确认修改落在正确模块。
3. 按 [docs/BUILDING.md](docs/BUILDING.md) 准备模型并完成首次构建。

当前 1.0.0 的正文产品范围是 `.txt`、`.md`、`.markdown`。新增文档格式、邮箱/聊天连接器、OCR、语义召回、RAG 或写入用户文件都属于产品范围变化，请先开 Issue 说明用户价值、隐私边界、依赖和失败降级方式。

## 开发原则

- 保持文件名搜索立即可用，任何正文索引或模型加载都不能阻塞首屏。
- Everything 是文件名/路径召回来源；不要增加递归全盘扫描回退。
- AI 只重排已有候选，不能绕过精确命中、噪音过滤和候选集合边界。
- 所有用户文件操作保持只读。新增外部网络请求必须默认关闭并明确披露。
- 不提交模型二进制、发布包、数据库、日志、密钥或个人绝对路径。
- 避免与当前任务无关的大规模格式化或重构。

## 建议流程

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\fetch-model.ps1
dotnet restore AIEverything.sln
dotnet build AIEverything.sln -c Release --no-restore
dotnet test tests\AIEverything.Server.Tests\AIEverything.Server.Tests.csproj -c Release --no-build --filter "Category!=Integration"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-skill-contract.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-open-source.ps1
```

提交前运行 `git diff --check`，并检查 `git diff --cached` 中没有生成物和敏感信息。UI 改动应附真实窗口截图；搜索或排序改动应补充最小定向测试和一个可复现样例。

## Pull Request

PR 请简要说明：用户可见变化、最小方案、测试与运行证据、隐私/性能/兼容风险，以及尚未验证的限制。不要把编译通过描述成真实桌面流程已验证。
