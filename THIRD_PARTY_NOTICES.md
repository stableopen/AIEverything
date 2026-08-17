# Third-Party Notices

## Everything SDK

- Project: Everything by voidtools / David Carpenter
- SDK archive: https://www.voidtools.com/Everything-SDK.zip
- License source: https://www.voidtools.com/License.txt
- License: MIT for Everything, with bundled third-party notices retained in `vendor/everything-sdk/LICENSE.txt`
- Archive SHA-256: `00693A1561D86D29A24E4691877ECE7FB23E9A5D8D8CBB2435E0B8576E96F343`

Redistributed files:

| File | SHA-256 |
|---|---|
| `vendor/everything-sdk/Everything64.dll` | `C7AB8B47F7DD4C41AA735F4BA40B35AD5460A86FA7ABE0C94383F12BCE33BFB6` |
| `vendor/everything-sdk/Everything.h` | `09CBE00C0E2B024CE49EC098BACFC4272F1D998F334E19E1D427AA8357D3AF78` |
| `vendor/everything-sdk/LICENSE.txt` | `C13D19ADCBFD5D07E9512DE9DF99956A3423399ED1FADC5FD33186697AD8DF2F` |

The original license and third-party notices are distributed without modification.

## Everything 1.4 Filename Engine

- Project: Everything by voidtools
- Portable archive: https://www.voidtools.com/Everything-1.4.1.1032.x64.zip
- Version: 1.4.1.1032 x64
- License: MIT with the bundled PCRE notice retained in `vendor/everything-engine/LICENSE.txt`
- Portable archive SHA-256: `698DF475EC44E638F66F1B6A32D28FEA613CEC78D3B6310E6ABE53431EEB940C`

Redistributed engine file:

| File | SHA-256 |
|---|---|
| `vendor/everything-engine/Everything.exe` | `F191F756996A14A11E5445FA7103D302EFD510CF2FBF920E6C0C8ED51D512E36` |

The engine runs locally for filename/path metadata only. AIEverything does not use it to extract whole-drive document bodies.

## Model Context Protocol C# SDK

- Package: `ModelContextProtocol` 1.3.0
- Project: https://github.com/modelcontextprotocol/csharp-sdk
- License: Apache License 2.0
- License text: https://github.com/modelcontextprotocol/csharp-sdk/blob/v1.3.0/LICENSE

The package is a build dependency and its assemblies are included in the self-contained single-file server.

## Microsoft.Data.Sqlite

- Package: `Microsoft.Data.Sqlite` 8.0.29
- Project: https://learn.microsoft.com/dotnet/standard/data/sqlite/
- License: MIT
- License text: https://licenses.nuget.org/MIT

Used for the local content catalog/FTS5 search index and the hash-only behavior-ranking database.

## SQLitePCLRaw

- Package: `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12
- Project: https://github.com/ericsink/SQLitePCL.raw
- License: Apache License 2.0
- License text: https://licenses.nuget.org/Apache-2.0

Pins the bundled native SQLite build to a release newer than the vulnerable 2.1.11 line.

## Open XML SDK

- Package: `DocumentFormat.OpenXml` 3.5.1
- Project: https://github.com/dotnet/Open-XML-SDK
- License: MIT
- License text: https://licenses.nuget.org/MIT

Legacy parser code can extract DOCX, XLSX, and PPTX text. AIEverything 1.0.0's
machine indexing policy does not enqueue these formats and does not claim them
as supported body-search formats.

## PdfPig

- Package: `PdfPig` 0.1.15
- Project: https://github.com/UglyToad/PdfPig
- License: Apache License 2.0
- License text: https://licenses.nuget.org/Apache-2.0

Legacy parser code can extract text-based PDFs. AIEverything 1.0.0's machine
indexing policy does not enqueue PDF and does not claim PDF body search.

## Microsoft ML ONNX Runtime

- Package: `Microsoft.ML.OnnxRuntime` 1.29.0
- Project: https://github.com/microsoft/onnxruntime
- License: MIT
- License text: https://github.com/microsoft/onnxruntime/blob/v1.29.0/LICENSE
- Bundled license and notices: `licenses/Microsoft.ML.OnnxRuntime/`

Used for the default-enabled, user-disableable local CPU reranking pass. The Windows x64 runtime libraries remain external to the application single file.

## Microsoft ML Tokenizers

- Package: `Microsoft.ML.Tokenizers` 2.0.0
- Project: https://github.com/dotnet/machinelearning
- License: MIT
- License text: https://licenses.nuget.org/MIT
- Bundled license and notices: `licenses/Microsoft.ML.Tokenizers/`

Used locally to encode XLM-R query/candidate pairs for the bundled ONNX model.

## mMARCO Multilingual MiniLM Cross-Encoder

- Model: `cross-encoder/mmarco-mMiniLMv2-L12-H384-v1`
- Source: https://huggingface.co/cross-encoder/mmarco-mMiniLMv2-L12-H384-v1
- Frozen revision: `1427fd652930e4ba29e8149678df786c240d8825`
- License: Apache License 2.0
- Bundled license: `Models/mmarco-mMiniLMv2-L12-H384-v1/LICENSE.apache-2.0.txt`
- Quantized ONNX SHA-256: `6C2513767FB63D008A4377BEF7A7A3555433D9436342BB53E35A3A72FFC52D4B`

The model, tokenizer, model card, calibration metadata, manifest, license, and `SHA256SUMS.txt` are shipped as external files under `Models/mmarco-mMiniLMv2-L12-H384-v1/`. The complete frozen file list, byte lengths, and hashes are recorded in the bundled `model-manifest.json` and verified during packaging.

## .NET

- Runtime: Microsoft .NET 8, self-contained Windows x64 publication
- Project: https://github.com/dotnet/runtime
- License: MIT
- Third-party notices: https://github.com/dotnet/runtime/blob/v8.0.22/THIRD-PARTY-NOTICES.TXT
