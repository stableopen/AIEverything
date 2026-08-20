# Building AIEverything

## 环境

- Windows 10/11 x64
- .NET 8 SDK
- Windows PowerShell 5.1 或 PowerShell 7
- Git
- 支持 AVX2 的 CPU（仅本地语义重排需要；其余功能可回退运行）

## 获取源码和模型

源码仓库不会提交 118,620,016 字节的 ONNX 文件。脚本从模型仓库的固定 revision 下载，并在写入目标位置前校验长度和 SHA-256：

```powershell
git clone https://github.com/stableye/AIEverything.git
cd AIEverything
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\fetch-model.ps1
```

固定模型：

- 仓库：`cross-encoder/mmarco-mMiniLMv2-L12-H384-v1`
- revision：`1427fd652930e4ba29e8149678df786c240d8825`
- 文件：`onnx/model_quint8_avx2.onnx`
- SHA-256：`6C2513767FB63D008A4377BEF7A7A3555433D9436342BB53E35A3A72FFC52D4B`

模型已存在且校验正确时脚本不会重复下载；错误文件不会覆盖为有效资产。

## 编译和测试

```powershell
dotnet restore AIEverything.sln
dotnet build AIEverything.sln -c Release --no-restore
dotnet test tests\AIEverything.Server.Tests\AIEverything.Server.Tests.csproj -c Release --no-build --filter "Category!=Integration"
```

最小开源检查：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test-skill-contract.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-open-source.ps1
```

`scripts/verify.ps1` 会串联还原、非集成测试、Release 构建、依赖漏洞检查和仓库自带的开源/Skill 合同检查。它不依赖作者电脑上的 Codex 安装目录。

## 便携包

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-standalone.ps1
```

输出：

- `dist\standalone\win-x64\`
- `dist\AIEverything-1.0.4-win-x64.zip`
- `dist\AIEverything-1.0.4-win-x64.zip.sha256`

`dist` 被 Git 忽略。构建脚本不要求干净 clone 存在旧发布包；如果本机保留了受保护的历史包，脚本仍会校验其哈希。模型缺失时会提示先运行 `scripts\fetch-model.ps1`。

## 可选 Agent 适配器

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-agent-connector.ps1
```

它生成独立的只读 MCP/Skill 包，不包含在桌面便携包中，也不是桌面搜索的运行前提。
