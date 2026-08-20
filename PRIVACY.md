# AIEverything 1.0.1 隐私说明

## 默认状态

AIEverything 的文件名搜索、正文索引、行为排序和 ONNX 语义排序默认都在本机完成。行为排序和本地模型默认开启；首次主界面会用非模态提示披露行为学习，可确认、关闭或进入设置清除。DeepSeek 默认关闭且未授权；只有设置页披露、主动开启和下列全部搜索门控都满足后，软件才会按需读取凭据。

## 本机数据

- `%LOCALAPPDATA%\AIEverything\content.db`：DOCX、TXT、MD、MARKDOWN 的本机 SQLite/FTS 正文索引和 Word 位置图，未单独加密。
- `%LOCALAPPDATA%\AIEverything\ranking.db`：只保存由随机盐加盐的文件键、父目录键、扩展名和每日权重聚合，保留 30 天；不保存查询、正文、片段或明文路径。打开/复制权重为 `1.0`，定位为 `0.5`，单独预览为 `0`，预览后再打开/复制额外 `+0.25`。聚合按 30 天半衰衰减，并以 `file + 0.30 × parentDirectory + 0.10 × extension` 形成亲和。用户清除时会删除聚合并轮换随机盐。
- `%LOCALAPPDATA%\AIEverything\settings.json`：窗口状态、排序开关和“已知晓行为学习披露”布尔值；不保存查询、API key、token 或其他凭据。

AIEverything 不修改、删除或移动被搜索的源文件。

## 可选 DeepSeek 网络请求

只有以下条件全部满足时，AIEverything 才会向 `https://api.deepseek.com/chat/completions` 发出一次歧义复排请求：

1. 用户已接受设置页披露；
2. 用户已主动开启 DeepSeek；
3. 打包的本地 ONNX 模型正常完成推理，并返回完整有限分数；
4. 候选无 Exact，且至少 3 个 Eligible；
5. 候选出现分数接近、重复名称、名称/正文混合证据或自然语言歧义之一，同时查询不是明确文件名、引号或路径式查询；
6. Windows 凭据管理器通用凭据 `AIEverything/DeepSeek` 中存在可用密钥；只有调试构建可回退环境变量 `AIEVERYTHING_DEEPSEEK_API_KEY`。

请求只包含当前查询，以及本地前 10 项的候选 ID、文件名、完整路径和每项最多 200 字片段。片段可能来自本机正文匹配；不发送匹配来源或排序层级，也不会上传文件本体或行为历史。DeepSeek 只被要求从这些候选中返回最多 5 个 ID；未知、重复、非法、超时或网络失败会完整回退本地结果。

请求总预算为 1.5 秒（包含排队），UTF-8 请求体最大 24 KiB；超限会在读取凭据和联网前拒绝。AIEverything 只允许一个云端请求同时执行，滚动每分钟最多 10 次，并将相同请求结果在内存会话中缓存 10 分钟。HTTP 429 会立即熔断 30 秒；连续两次 5xx、网络、超时或无效服务响应也会熔断 30 秒，且不重试。HTTP 401/403 会停用本次会话的云端调用并清空会话缓存。

本地模型缺失、哈希不符、CPU 不支持 AVX2、ONNX Runtime 不可用、推理失败或超过 400 ms 时，AIEverything 不会调用 DeepSeek。关闭 DeepSeek 或撤销披露后也不会读取凭据或联网。

DeepSeek 服务收到数据后的处理受其服务条款和隐私政策约束，AIEverything 无法控制该外部服务。不要在查询、文件名、路径或正文片段中包含你不愿发送给该服务的信息。

## 凭据

设置页使用遮罩输入，将密钥保存或覆盖更新到 Windows 凭据管理器通用凭据 `AIEverything/DeepSeek`；输入框在保存成功后清空。密钥仅按需读取并用于请求授权头，不写入设置、行为库、日志或错误信息。发布版不读取环境变量；`AIEVERYTHING_DEEPSEEK_API_KEY` 仅是调试构建的开发回退。
