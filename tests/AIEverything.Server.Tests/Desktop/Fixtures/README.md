# Fixed ONNX reference

`onnx-reference-v1427fd.json` is independent reference evidence for the V0.21
local reranker. The generator uses HuggingFace `XLMRobertaTokenizer` and Python
ONNX Runtime against the locally checked model assets from revision
`1427fd652930e4ba29e8149678df786c240d8825`. It performs no network access.

The fixture covers English, Chinese, a Windows path, emoji, a long candidate,
and a full Top10 batch. Every query is asserted to remain below 48 tokenizer
tokens. Candidate snippets are truncated to 200 UTF-16 code units without
splitting a surrogate pair, before the 192-token pair truncation; names and
paths therefore remain ahead of snippets. The JSON records exact input IDs and
attention masks, Python ONNX logits, and the exact stable Top10 ranking.

Regenerate from a disposable dependency directory outside the repository:

```powershell
$deps = 'C:\path\to\temporary\python-deps'
python -X utf8 -m pip install --target $deps onnxruntime==1.28.0 sentencepiece==0.2.1 transformers==4.55.4
$env:PYTHONPATH = $deps
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8 = '1'
python -X utf8 tests\AIEverything.Server.Tests\Desktop\Fixtures\generate_onnx_reference.py
```

The checked C# runtime remains Microsoft.ML.OnnxRuntime 1.29.0. Tests compare
the tokenizer IDs and masks exactly, compare logits with the fixture's narrow
absolute tolerance, and compare ranking exactly. Python dependencies and their
temporary directory are never copied into the portable package.
