# AIEverything Scoped Content Index Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build AIEverything v0.2 so AI agents can search document bodies inside explicitly authorized local Windows directories without scanning whole disks.

**Architecture:** Keep Everything 1.4 as the filename/path metadata engine. Add a per-user daemon with a SQLite FTS5 content index, a one-file-per-process extractor worker, a current-user-only named-pipe API, and thin CLI/MCP clients. The daemon is the single database writer and only enumerates roots accepted by the hard root policy.

**Tech Stack:** .NET 8, C# 12, Microsoft.Data.Sqlite 8.0.29, DocumentFormat.OpenXml 3.5.1, PdfPig 0.1.15, ModelContextProtocol 1.3.0, xUnit 2.9.3, PowerShell packaging.

## Global Constraints

- Windows x64 and .NET 8 remain the supported runtime.
- Content indexing starts with zero roots and reads only user-authorized local directories.
- Reject drive roots, Windows, Program Files, ProgramData, AppData, UNC paths, and reparse points.
- Never follow reparse points and never silently fall back to a whole-drive content scan.
- Default maximum source file size is 20 MiB; extracted text is capped at 2,000,000 characters.
- Default extractor concurrency is one process with a 30-second per-file timeout.
- SQLite uses WAL and a single daemon writer; no HTTP listener is introduced.
- Content result limit defaults to 20 and is capped at 100.
- Existing Everything metadata CLI and MCP contracts remain compatible.
- v0.2 excludes OCR, semantic vectors, legacy Office formats, desktop UI, installer, and public updater.

---

## File Map

- `src/AIEverything.Content/`: shared contracts, root policy, tokenization, extraction, SQLite storage, scoped enumeration, and indexing orchestration.
- `src/AIEverything.ExtractorWorker/`: short-lived executable that extracts exactly one file and emits one JSON response.
- `src/AIEverything.Daemon/`: per-user singleton, persistent queue processor, watchers/reconcile loop, and named-pipe server.
- `src/AIEverything.Server/Content/`: daemon client and filename/content hybrid fusion used by CLI and MCP.
- `tests/AIEverything.Server.Tests/Content/`: policy, tokenizer, extractor, store, protocol, daemon, CLI, MCP, and integration coverage.
- `scripts/build.ps1` and `scripts/verify.ps1`: publish and verify all three executables and the v0.2 smoke path.

### Task 1: Add content domain contracts and hard root policy

**Files:**
- Create: `src/AIEverything.Content/AIEverything.Content.csproj`
- Create: `src/AIEverything.Content/Contracts/ContentModels.cs`
- Create: `src/AIEverything.Content/Errors/ContentIndexException.cs`
- Create: `src/AIEverything.Content/Policy/ContentRootPolicy.cs`
- Create: `tests/AIEverything.Server.Tests/Content/ContentRootPolicyTests.cs`
- Modify: `AIEverything.sln`
- Modify: `tests/AIEverything.Server.Tests/AIEverything.Server.Tests.csproj`

**Interfaces:**
- Produces: `ContentRootPolicy.Validate(string): AuthorizedRoot`, `ContentIndexException`, `ContentSearchRequest`, `ContentSearchResponse`, `ContentIndexStatus`, and `RootOperationResponse`.

- [ ] **Step 1: Write failing policy tests** covering an allowed temporary directory and rejection of a drive root, system directory, UNC path, missing path, and reparse point. Assert exact codes `ROOT_NOT_ALLOWED` or `ROOT_NOT_FOUND`.
- [ ] **Step 2: Run** `dotnet test AIEverything.sln --filter FullyQualifiedName~ContentRootPolicyTests` and verify compilation/test failure because the content types do not exist.
- [ ] **Step 3: Implement contracts and `ContentRootPolicy`** with `Path.GetFullPath`, local-drive checks, `DirectoryInfo.Attributes`, protected-directory ancestry checks, and a normalized path without a trailing separator except for roots (which are rejected).
- [ ] **Step 4: Run the focused policy tests and the existing 52 tests**; expect all green.
- [ ] **Step 5: Commit** `feat: add scoped content root policy`.

### Task 2: Add deterministic English/CJK tokenization and query validation

**Files:**
- Create: `src/AIEverything.Content/Text/ContentTokenizer.cs`
- Create: `tests/AIEverything.Server.Tests/Content/ContentTokenizerTests.cs`

**Interfaces:**
- Produces: `ContentTokenizer.TokenizeForIndex(string): string`, `ContentTokenizer.BuildMatchQuery(string): string`, and `ContentTokenizer.GetQueryTerms(string): IReadOnlyList<string>`.

- [ ] **Step 1: Write failing tests** asserting lower-cased Unicode words, overlapping Chinese bigrams (exactly `人工 工智 智能` for `人工智能`), preserved numbers/technical identifiers, escaped FTS phrases, empty-query rejection, and single-CJK-character `QUERY_TOO_BROAD`.
- [ ] **Step 2: Run** the tokenizer test class and verify failure because `ContentTokenizer` is missing.
- [ ] **Step 3: Implement a Rune-based tokenizer** that emits Latin/number/identifier runs and overlapping CJK bigrams, de-duplicates query terms in encounter order, and returns quoted FTS5 terms joined by `AND`.
- [ ] **Step 4: Run focused and full tests**; expect all green.
- [ ] **Step 5: Commit** `feat: add multilingual content tokenizer`.

### Task 3: Add safe text, PDF, and Open XML extraction

**Files:**
- Create: `src/AIEverything.Content/Extraction/ITextExtractor.cs`
- Create: `src/AIEverything.Content/Extraction/ExtractionModels.cs`
- Create: `src/AIEverything.Content/Extraction/PlainTextExtractor.cs`
- Create: `src/AIEverything.Content/Extraction/OpenXmlTextExtractor.cs`
- Create: `src/AIEverything.Content/Extraction/PdfTextExtractor.cs`
- Create: `src/AIEverything.Content/Extraction/CompositeTextExtractor.cs`
- Create: `tests/AIEverything.Server.Tests/Content/TextExtractorTests.cs`
- Modify: `src/AIEverything.Content/AIEverything.Content.csproj`

**Interfaces:**
- Produces: `ITextExtractor.ExtractAsync(ExtractionRequest, CancellationToken): Task<ExtractionResult>` and `CompositeTextExtractor.CreateDefault()`.

- [ ] **Step 1: Write failing extractor tests** that create temporary UTF-8, UTF-16 LE/BE, invalid-encoding, DOCX, XLSX, PPTX, text PDF, empty PDF, oversize, and corrupt files. Assert exact text and error codes `UNSUPPORTED_ENCODING`, `FILE_TOO_LARGE`, `OCR_REQUIRED`, or `EXTRACTION_FAILED`.
- [ ] **Step 2: Run** the extractor test class and verify missing extractor types fail.
- [ ] **Step 3: Add pinned packages** `DocumentFormat.OpenXml` 3.5.1 and `PdfPig` 0.1.15, then implement extension routing, 20 MiB input guard, UTF validation, Open XML descendant text extraction without macros/external-link loading, PDF page text extraction, and the 2,000,000-character cap.
- [ ] **Step 4: Run focused and full tests**; expect all green and no warnings.
- [ ] **Step 5: Commit** `feat: extract supported document content`.

### Task 4: Add SQLite FTS5 storage, roots, queue, snippets, and recovery state

**Files:**
- Create: `src/AIEverything.Content/Storage/ContentIndexStore.cs`
- Create: `src/AIEverything.Content/Storage/ContentSchema.cs`
- Create: `tests/AIEverything.Server.Tests/Content/ContentIndexStoreTests.cs`
- Modify: `src/AIEverything.Content/AIEverything.Content.csproj`

**Interfaces:**
- Produces: async methods `InitializeAsync`, `AddRootAsync`, `RemoveRootAsync`, `ListRootsAsync`, `EnqueueAsync`, `LeaseNextAsync`, `CompleteAsync`, `FailAsync`, `RemoveDocumentAsync`, `SearchAsync`, `GetStatusAsync`, and `IntegrityCheckAsync`.

- [ ] **Step 1: Write failing store tests** for schema creation, WAL mode, idempotent root add, root removal cascade, queue de-duplication, retry timestamps, document upsert by fingerprint, Chinese/English FTS matches, filters, BM25 title boost, snippets, and `integrity_check`.
- [ ] **Step 2: Run** the store tests and verify missing store types fail.
- [ ] **Step 3: Add** `Microsoft.Data.Sqlite` 8.0.29 and implement parameterized SQL for `roots`, `documents`, `content_fts`, `index_queue`, and `index_failures`; use transactions for document/FTS updates and never interpolate user query text into SQL.
- [ ] **Step 4: Run focused and full tests**; expect all green.
- [ ] **Step 5: Commit** `feat: persist scoped content index`.

### Task 5: Add scoped enumeration and persistent indexing orchestration

**Files:**
- Create: `src/AIEverything.Content/Indexing/ScopedFileEnumerator.cs`
- Create: `src/AIEverything.Content/Indexing/ContentIndexer.cs`
- Create: `tests/AIEverything.Server.Tests/Content/ContentIndexerTests.cs`

**Interfaces:**
- Produces: `ScopedFileEnumerator.Enumerate(AuthorizedRoot): IEnumerable<FileCandidate>`, `ContentIndexer.EnqueueRootAsync`, `ContentIndexer.ProcessOneAsync`, and `ContentIndexer.RemovePathAsync`.

- [ ] **Step 1: Write failing tests** proving enumeration stays inside the root, skips default excluded directories/hidden/system/reparse/cloud-placeholder/unsupported/oversize files, enqueues changed fingerprints only, removes deleted documents, and persists extraction failures without stopping the root.
- [ ] **Step 2: Run** focused tests and verify missing indexing types fail.
- [ ] **Step 3: Implement manual stack-based traversal** with `EnumerationOptions.IgnoreInaccessible`, hard no-follow reparse checks, supported-extension filtering, normalized fingerprints, queue writes, single-item worker calls, and 1m/10m/1h retry scheduling capped at three attempts per fingerprint.
- [ ] **Step 4: Run focused and full tests**; expect all green.
- [ ] **Step 5: Commit** `feat: index authorized directories incrementally`.

### Task 6: Add isolated extractor worker

**Files:**
- Create: `src/AIEverything.ExtractorWorker/AIEverything.ExtractorWorker.csproj`
- Create: `src/AIEverything.ExtractorWorker/Program.cs`
- Create: `src/AIEverything.Content/Extraction/WorkerTextExtractor.cs`
- Create: `tests/AIEverything.Server.Tests/Content/ExtractorWorkerIntegrationTests.cs`
- Modify: `AIEverything.sln`

**Interfaces:**
- Produces: worker command `extract --path <absolute-path> --max-bytes 20971520 --max-chars 2000000` and `WorkerTextExtractor(string executablePath, TimeSpan timeout)`.

- [ ] **Step 1: Write failing integration tests** invoking the worker against a valid text file, a corrupt file, and a cancellation/timeout fixture; assert one camelCase JSON result and no extra stdout.
- [ ] **Step 2: Run** focused tests and verify the worker output is missing.
- [ ] **Step 3: Implement the worker** with strict argument parsing, below-normal priority, structured JSON errors, no file execution, and exit codes 0/1/2; implement daemon-side `ProcessStartInfo.ArgumentList`, redirected UTF-8 stdout/stderr, 30-second cancellation, and process-tree kill.
- [ ] **Step 4: Run focused and full tests**; expect all green.
- [ ] **Step 5: Commit** `feat: isolate document extraction worker`.

### Task 7: Add bounded current-user named-pipe protocol

**Files:**
- Create: `src/AIEverything.Content/Ipc/ContentPipeProtocol.cs`
- Create: `src/AIEverything.Content/Ipc/ContentPipeNaming.cs`
- Create: `tests/AIEverything.Server.Tests/Content/ContentPipeProtocolTests.cs`

**Interfaces:**
- Produces: `ContentPipeProtocol.WriteAsync<T>`, `ReadAsync<T>`, a 1 MiB limit, and `ContentPipeNaming.ForCurrentUser()` using a SHA-256 SID hash.

- [ ] **Step 1: Write failing tests** for round-trip JSON, little-endian four-byte framing, partial reads, zero/negative/over-1-MiB rejection, malformed JSON, and deterministic per-user pipe naming.
- [ ] **Step 2: Run** focused tests and verify missing protocol types fail.
- [ ] **Step 3: Implement exact framed JSON I/O** using `ReadExactlyAsync`, camelCase serialization, explicit maximum checks, and stable `INVALID_ARGUMENTS` protocol errors.
- [ ] **Step 4: Run focused and full tests**; expect all green.
- [ ] **Step 5: Commit** `feat: add local content daemon protocol`.

### Task 8: Add daemon singleton, queue loop, watchers, and reconcile

**Files:**
- Create: `src/AIEverything.Daemon/AIEverything.Daemon.csproj`
- Create: `src/AIEverything.Daemon/Program.cs`
- Create: `src/AIEverything.Daemon/ContentDaemon.cs`
- Create: `src/AIEverything.Daemon/ContentPipeServer.cs`
- Create: `src/AIEverything.Daemon/RootWatcherManager.cs`
- Create: `tests/AIEverything.Server.Tests/Content/ContentDaemonIntegrationTests.cs`
- Modify: `AIEverything.sln`

**Interfaces:**
- Produces: `AIEverything.Daemon.exe run`, request operations `root.add/remove/list`, `index.status/pause/resume/rebuild`, and `content.search`.

- [ ] **Step 1: Write failing daemon tests** using an isolated database/pipe name to add a root, observe queued/indexed counts, search body text, modify/rename/delete files, pause/resume, rebuild, reject a second singleton, and recover queued work after restart.
- [ ] **Step 2: Run** focused tests and verify daemon executable/protocol handlers are absent.
- [ ] **Step 3: Implement the daemon** with `PipeOptions.CurrentUserOnly`, one request per connection, a current-user named mutex, below-normal priority, one queue worker, 750 ms watcher debounce, and a 10-minute scoped reconcile timer. Root mutations return after durable queueing rather than waiting for full extraction.
- [ ] **Step 4: Run focused and full tests**; expect all green.
- [ ] **Step 5: Commit** `feat: run per-user content index daemon`.

### Task 9: Add daemon client, content search, and hybrid RRF

**Files:**
- Create: `src/AIEverything.Server/Content/IContentSearchService.cs`
- Create: `src/AIEverything.Server/Content/ContentDaemonClient.cs`
- Create: `src/AIEverything.Server/Content/HybridSearchService.cs`
- Create: `tests/AIEverything.Server.Tests/Content/ContentDaemonClientTests.cs`
- Create: `tests/AIEverything.Server.Tests/Content/HybridSearchServiceTests.cs`

**Interfaces:**
- Produces: `IContentSearchService.SearchAsync`, `GetStatusAsync`, `ManageRootAsync`, `SetPausedAsync`, `RebuildAsync`, and `HybridSearchService.SearchAsync` returning `name`, `content`, or `both` match sources.

- [ ] **Step 1: Write failing tests** for unavailable daemon mapping to `CONTENT_SERVICE_UNAVAILABLE`, request forwarding, filename-only/content-only/both fusion, `k=60` RRF, exact filename multiplier 1.5, title multiplier 1.2, and a 100-result cap.
- [ ] **Step 2: Run** focused tests and verify missing client/service types fail.
- [ ] **Step 3: Implement the client and fusion service**; Everything and content searches run concurrently, each takes at most 50 candidates, paths use ordinal-ignore-case identity, and Everything failure does not erase available content results.
- [ ] **Step 4: Run focused and full tests**; expect all green.
- [ ] **Step 5: Commit** `feat: combine filename and content search`.

### Task 10: Expose CLI and MCP v0.2 contracts

**Files:**
- Modify: `src/AIEverything.Server/Cli/CliCommandRunner.cs`
- Modify: `src/AIEverything.Server/Mcp/AIEverythingTools.cs`
- Modify: `src/AIEverything.Server/Program.cs`
- Modify: `tests/AIEverything.Server.Tests/Cli/CliCommandRunnerTests.cs`
- Modify: `tests/AIEverything.Server.Tests/Mcp/AIEverythingToolsTests.cs`
- Modify: `tests/AIEverything.Server.Tests/Mcp/McpStdioIntegrationTests.cs`

**Interfaces:**
- Produces CLI commands `content-root`, `content-index`, `content-search`, and `hybrid-search`; MCP tools `search_local_content`, `search_local_hybrid`, `aieverything_index_status`, and `aieverything_manage_roots`.

- [ ] **Step 1: Write failing CLI/MCP tests** for exact command JSON, argument validation, all four tool names, structured content, stable error mapping, and annotations. Root management must declare `ReadOnly=false`; search/status tools remain read-only.
- [ ] **Step 2: Run** the focused CLI/MCP tests and verify missing commands/tools fail.
- [ ] **Step 3: Inject content/hybrid services** into CLI and MCP, add async methods and validation, update server version to `0.2.0`, and keep all existing metadata method signatures unchanged.
- [ ] **Step 4: Run focused and full tests**; expect all green.
- [ ] **Step 5: Commit** `feat: expose content search to agents`.

### Task 11: Package, document, benchmark, reinstall, and verify

**Files:**
- Modify: `scripts/build.ps1`
- Modify: `scripts/verify.ps1`
- Modify: `scripts/test-skill-contract.ps1`
- Create: `scripts/benchmark-content.ps1`
- Modify: `skills/aieverything-search/SKILL.md`
- Modify: `.codex-plugin/plugin.json`
- Modify: `README.md`
- Modify: `THIRD_PARTY_NOTICES.md`
- Modify: `.codex/PROJECT_CONTEXT.md`
- Modify: `tests/AIEverything.Server.Tests/PluginContractTests.cs`

**Interfaces:**
- Produces self-contained `AIEverything.Server.exe`, `AIEverything.Daemon.exe`, and `AIEverything.ExtractorWorker.exe`, plus documented start/configure/search commands.

- [ ] **Step 1: Write failing contract tests** requiring plugin version `0.2.0`, new MCP tool names in the skill, explicit authorized-root/privacy language, and all three published executables.
- [ ] **Step 2: Run** contract tests and verify they fail against v0.1 packaging/docs.
- [ ] **Step 3: Update packaging and documentation** with exact v0.2 behavior, local extracted-text disclosure, supported formats, daemon start, root add/remove, search examples, stable errors, v0.3 installer roadmap, and third-party notices. Build publishes all executables without deleting outside `dist/win-x64`.
- [ ] **Step 4: Build and run full verification:** `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1`, then start daemon against a temporary authorized corpus, add the corpus, wait for idle, run content and hybrid searches, run `scripts/benchmark-content.ps1`, and run `scripts/verify.ps1`.
- [ ] **Step 5: Run plugin update flow:** validate plugin, call `update_plugin_cachebuster.py`, read the personal marketplace name with `read_marketplace_name.py`, and reinstall with `codex plugin add aieverything@personal` when the helper returns `personal`.
- [ ] **Step 6: Inspect** `git diff --check`, `git status --short`, package vulnerability output, the built file list, and the actual MCP tool list; fix any discrepancy before claiming completion.
- [ ] **Step 7: Commit** `release: package AIEverything content search 0.2.0`.

## Final Verification Checklist

- [ ] Existing 52 tests and all new tests pass in Release.
- [ ] Drive roots, protected directories, UNC paths, and reparse points are rejected automatically.
- [ ] Only explicitly authorized directories appear in roots and content results.
- [ ] TXT/UTF-16/PDF/DOCX/XLSX/PPTX extraction uses real test files.
- [ ] Daemon restart resumes persistent work without whole-disk enumeration.
- [ ] MCP lists seven tools and preserves the three v0.1 tools.
- [ ] A temporary real corpus returns a body snippet and a hybrid match.
- [ ] Content benchmark records median/P95 and does not invent target compliance.
- [ ] Plugin validator, skill validator, build, verification, and `git diff --check` all pass.
