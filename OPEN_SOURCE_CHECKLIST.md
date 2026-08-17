# Open-source release checklist

## 发布前 P1

- [ ] 生成并人工核对第三方依赖清单/SBOM；当前 `THIRD_PARTY_NOTICES.md` 不是自动生成的完整 SBOM。
- [ ] 在干净环境复核所有可再分发许可证，尤其是 Everything 引擎、模型和 NuGet 运行时文件。
- [ ] GitHub Private Vulnerability Reporting 已启用，或已配置其他私密安全联系方式。
- [ ] 公开仓库使用新的干净仓库或 orphan 初始提交；不要推送包含旧 WeChat 二进制、公司邮箱元数据或实验连接器的完整历史。

## 仓库内容

- [ ] `powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-open-source.ps1` 通过。
- [ ] `git status --short` 中没有 `dist/`、`.codex/`、数据库、日志、密钥或 ONNX 大文件。
- [ ] `git diff --cached` 已人工检查，确认没有个人目录、内部地址或临时调试代码。
- [ ] README 截图不包含个人文件名、账户、通知或其他隐私信息。
- [ ] `LICENSE` 与 `THIRD_PARTY_NOTICES.md` 已核对。

## 构建与产品

- [ ] 在干净 clone 中运行 `scripts\fetch-model.ps1`，确认冻结 revision、长度和 SHA-256。
- [ ] Release solution build 通过。
- [ ] 选定的定向/非集成测试通过。
- [ ] 从干净 clone 生成便携 ZIP，并实际打开主窗口完成一次文件名和正文搜索。
- [ ] Release 附带 MIT License、第三方声明、SHA-256 和已知限制。
- [ ] 明确标注“AIEverything 不是 voidtools 官方产品”。

## 建议的首次公开提交

本地工作树包含多个历史实验版本。请创建新的干净仓库，或使用 orphan 分支从审核后的当前文件树生成一个初始提交；不要把现有 Git 历史直接推到公共远端。

```powershell
git switch --orphan public-main
git rm -r --cached .
git add -A
git status --short
git diff --cached --stat
git diff --cached
git commit -m "feat: open source AIEverything 1.0.0"
git remote add public <public-repository-url>
git push -u public public-main:main
```

以上命令只是维护者发布步骤；自动化整理过程不应自行提交、打 tag 或推送。
