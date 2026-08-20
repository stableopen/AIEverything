# AIEverything 1.0.1 DOCX Internal Promotion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship a directly downloadable AIEverything 1.0.1 that searches eligible DOCX paragraphs and table cells alongside TXT/MD/MARKDOWN and reports useful locations without letting bad documents block indexing.

**Architecture:** Extend the existing isolated extraction pipeline with structured DOCX blocks, persist their compact source map beside FTS text, and keep current filename recall and local ranking unchanged. Add DOCX to the fixed-drive policy, expose user-oriented progress/failure states, then build and publish one portable GitHub Release.

**Tech Stack:** .NET 8, WPF, DocumentFormat.OpenXml 3.5.1, SQLite FTS5, xUnit, PowerShell, GitHub CLI.

**Spec:** `docs/superpowers/specs/2026-08-20-docx-internal-promotion-design.md`

## Global Constraints

- Version is exactly `1.0.1`; release tag is `v1.0.1`.
- Body formats are exactly `.txt`, `.md`, `.markdown`, `.docx`.
- TXT/MD/MARKDOWN maximum size is 5 MiB; DOCX maximum size is 10 MiB.
- Every document is capped at 1,000,000 extracted characters and 15 seconds.
- Filename search, ranking candidates, DeepSeek gates, and source files remain read-only and unchanged.
- Do not add PDF, other Office formats, legacy DOC, OCR, connectors, RAG, installer, updater, or signing.
- Preserve the old private repository and historical ZIPs.

---

### Task 1: Structured DOCX extraction

**Files:**
- Modify: `src/AIEverything.Content/Extraction/ExtractionModels.cs`
- Modify: `src/AIEverything.Content/Extraction/OpenXmlTextExtractor.cs`
- Modify: `src/AIEverything.Content/Extraction/WorkerTextExtractor.cs`
- Modify: `src/AIEverything.ExtractorWorker/Program.cs`
- Test: `tests/AIEverything.Server.Tests/Content/TextExtractorTests.cs`

**Interfaces:**
- Produces: `ExtractedTextBlock(int Ordinal, string Text, string LocationLabel, string? HeadingPath)`.
- Produces: `ExtractionResult(..., IReadOnlyList<ExtractedTextBlock>? Blocks = null)`.
- Preserves worker JSON serialization through the existing `WorkerExtractionResponse` contract.

- [ ] **Step 1: Write failing extraction tests** for a DOCX whose sentence spans multiple runs, whose Heading 1 contains a body paragraph, and whose table has a unique cell term. Assert one joined paragraph block, heading path propagation, and `Table 1 · row 1 · cell 2`.

```csharp
Assert.Contains(result.Blocks!, block =>
    block.Text == "Quarterly operating plan" && block.LocationLabel.Contains("paragraph"));
Assert.Contains(result.Blocks!, block =>
    block.Text.Contains("regional target") && block.HeadingPath == "Sales Plan");
Assert.Contains(result.Blocks!, block =>
    block.Text == "CellCanary" && block.LocationLabel.Contains("Table 1 · row 1 · cell 2"));
```

- [ ] **Step 2: Run the focused test and verify RED.**

```powershell
dotnet test tests\AIEverything.Server.Tests\AIEverything.Server.Tests.csproj -c Release --filter "FullyQualifiedName~TextExtractorTests"
```

Expected: failure because `ExtractionResult.Blocks` and structured DOCX extraction do not exist.

- [ ] **Step 3: Implement minimal structured extraction.** Walk `MainDocumentPart.Document.Body` children in order. Join paragraph descendant `W.Text` values without inserting line breaks between runs. Resolve outline level from direct paragraph properties, then style-chain paragraph properties. Update a nine-level heading stack and emit searchable ordinary paragraph/table-cell blocks with one-based labels. Flatten blocks with `Environment.NewLine` and truncate by whole-block text when possible.

- [ ] **Step 4: Run focused tests and verify GREEN**, including worker JSON round-trip.

- [ ] **Step 5: Commit.**

```powershell
git add src/AIEverything.Content/Extraction src/AIEverything.ExtractorWorker/Program.cs tests/AIEverything.Server.Tests/Content/TextExtractorTests.cs
git commit -m "feat: extract structured Word content"
```

### Task 2: Persist and resolve DOCX locations

**Files:**
- Modify: `src/AIEverything.Content/Storage/ContentSchema.cs`
- Modify: `src/AIEverything.Content/Storage/ContentIndexStore.cs`
- Modify: `src/AIEverything.Content/Text/SourceLocationResolver.cs`
- Modify: `src/AIEverything.Content/Indexing/ContentIndexer.cs`
- Modify: `src/AIEverything.Content/Contracts/ContentModels.cs`
- Test: `tests/AIEverything.Server.Tests/Content/SourceLocationResolverTests.cs`
- Test: `tests/AIEverything.Server.Tests/Content/MachineSnapshotStoreTests.cs`
- Test: `tests/AIEverything.Server.Tests/Content/ContentV020MigrationTests.cs`

**Interfaces:**
- Consumes: `ExtractionResult.Blocks` from Task 1.
- Produces: `StoreDocumentAsync(QueueLease, ExtractionResult, CancellationToken)` with location JSON persisted in `documents.location_map`.
- Produces: `SourceLocationResolver.Resolve(content, extension, queryTerms, blocks, maxHits)`.

- [ ] **Step 1: Write failing persistence/search tests.** Index a synthetic DOCX result with two blocks, reopen the store, search a unique term, and assert the stored `LocationLabel` and `HeadingPath` are returned.

```csharp
Assert.Equal("Sales Plan · paragraph 4", item.LocationLabel);
Assert.Equal("Sales Plan", item.HeadingPath);
```

- [ ] **Step 2: Verify RED** with the focused content tests.

- [ ] **Step 3: Add a nullable `location_map` column** through idempotent schema migration. Serialize only ordinal, text boundaries, label, and heading path. Update document writes atomically with FTS writes. Deserialize defensively; an invalid/missing map falls back to current line-based snippets without failing search.

- [ ] **Step 4: Add the DOCX resolver branch.** Match all query terms against block text, prefer the smallest contiguous block window containing all terms, return no more than three representative hits, and retain TXT/Markdown behavior byte-for-byte.

- [ ] **Step 5: Bump compatibility constants** to protocol `6` and extraction revision `machine-docx-blocks-v1`, ensuring fingerprints trigger one controlled rebuild.

- [ ] **Step 6: Run migration, resolver, and snapshot tests and verify GREEN.**

- [ ] **Step 7: Commit.**

```powershell
git add src/AIEverything.Content tests/AIEverything.Server.Tests/Content
git commit -m "feat: preserve Word hit locations"
```

### Task 3: Admit DOCX and isolate permanent failures

**Files:**
- Modify: `src/AIEverything.Content/MachineIndex/MachineTextIndexPolicy.cs`
- Modify: `src/AIEverything.Content/Indexing/ContentIndexer.cs`
- Modify: `src/AIEverything.Content/Errors/ContentErrorCodes.cs`
- Modify: `src/AIEverything.Content/Storage/ContentIndexStore.cs`
- Test: `tests/AIEverything.Server.Tests/Content/MachineTextIndexPolicyTests.cs`
- Test: `tests/AIEverything.Server.Tests/Content/MachineSnapshotStoreTests.cs`

**Interfaces:**
- Produces: policy whitelist `.txt/.md/.markdown/.docx`, with DOCX 10 MiB.
- Produces: `FailureDisposition` classification separating permanent current-fingerprint failures from transient retryable I/O.

- [ ] **Step 1: Write failing policy tests** asserting DOCX acceptance at 10 MiB, rejection above 10 MiB, and continued rejection of PDF/XLSX/PPTX.

- [ ] **Step 2: Write a failing queue test** where corrupt DOCX is recorded once while the following valid file is indexed; another scan with the same fingerprint must not enqueue the corrupt file.

- [ ] **Step 3: Verify both tests RED.**

- [ ] **Step 4: Add DOCX to `FormatLimits`** and map known extraction codes for corrupt/unsupported-encrypted, size, timeout, and access denied to permanent failure. Keep unexpected I/O on the existing 1 minute/10 minute/1 hour bounded schedule. Preserve the fingerprint change escape hatch and add a store operation used by explicit retry to clear failures.

- [ ] **Step 5: Run focused tests and verify GREEN.**

- [ ] **Step 6: Commit.**

```powershell
git add src/AIEverything.Content tests/AIEverything.Server.Tests/Content
git commit -m "feat: index eligible Word documents"
```

### Task 4: User-oriented status and feedback

**Files:**
- Modify: `src/AIEverything.Content/Contracts/ContentModels.cs`
- Modify: `src/AIEverything.Content/Storage/ContentIndexStore.cs`
- Modify: `src/AIEverything.Daemon/ContentDaemon.cs`
- Modify: `src/AIEverything.App/MainWindow.xaml.cs`
- Modify: `src/AIEverything.App/ContentSettingsWindow.xaml`
- Modify: `src/AIEverything.App/ContentSettingsWindow.xaml.cs`
- Test: `tests/AIEverything.Server.Tests/Desktop/StandaloneProductContractTests.cs`

**Interfaces:**
- Produces status totals `IndexedDocuments`, `QueuedDocuments`, `FailedDocuments` plus grouped failure counts.
- Produces `index.failures.retry` action clearing current failures and requesting sync.

- [ ] **Step 1: Write failing product-contract tests** for the exact short main-window messages, Word/TXT/Markdown disclosure, grouped failure labels, retry action, and `https://github.com/stableye/AIEverything/issues/new`.

- [ ] **Step 2: Verify RED.**

- [ ] **Step 3: Implement the compact status copy.** The home surface may show searchable count and next action only; queue/failure details remain in Settings. Add a `Report a problem` hyperlink and a `Retry failed files` button in Settings without adding a new main-window toolbar row.

- [ ] **Step 4: Run focused desktop/product tests and verify GREEN.**

- [ ] **Step 5: Commit.**

```powershell
git add src/AIEverything.App src/AIEverything.Content src/AIEverything.Daemon tests/AIEverything.Server.Tests/Desktop
git commit -m "feat: simplify indexing status for colleagues"
```

### Task 5: Version, documentation, and release packaging

**Files:**
- Modify: `src/AIEverything.App/AIEverything.App.csproj`
- Modify: `src/AIEverything.Daemon/AIEverything.Daemon.csproj`
- Modify: `src/AIEverything.ExtractorWorker/AIEverything.ExtractorWorker.csproj`
- Modify: `.codex-plugin/plugin.json`
- Modify: `src/AIEverything.Server/Program.cs`
- Modify: `scripts/build-standalone.ps1`
- Modify: `scripts/build-agent-connector.ps1`
- Modify: `README.md`
- Modify: `PRIVACY.md`
- Modify: `THIRD_PARTY_NOTICES.md`
- Modify: `docs/BUILDING.md`
- Modify: `docs/STANDALONE-README.txt`
- Create: `docs/releases/1.0.1.md`
- Test: `tests/AIEverything.Server.Tests/Desktop/StandaloneProductContractTests.cs`
- Test: `tests/AIEverything.Server.Tests/PluginContractTests.cs`

**Interfaces:**
- Produces `dist/AIEverything-1.0.1-win-x64.zip` and checksum sidecar.
- Produces user-facing release notes with three steps, scope, limitations, and feedback URL.

- [ ] **Step 1: Write failing version/package contract tests** for `1.0.1`, the new ZIP name, DOCX documentation, and absence of PDF/XLSX/PPTX claims.

- [ ] **Step 2: Verify RED.**

- [ ] **Step 3: Update all shipped version surfaces** to `1.0.1`/`1.0.1.0`. Update README to use the eventual `v1.0.1` download URL and explain filename-immediate/body-progressive behavior. Make `build-standalone.ps1` emit the SHA-256 sidecar after byte-identity checks.

- [ ] **Step 4: Run product/plugin contracts and open-source audit; verify GREEN.**

```powershell
dotnet test tests\AIEverything.Server.Tests\AIEverything.Server.Tests.csproj -c Release --filter "FullyQualifiedName~StandaloneProductContractTests|FullyQualifiedName~PluginContractTests"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-open-source.ps1
```

- [ ] **Step 5: Commit.**

```powershell
git add .codex-plugin src scripts README.md PRIVACY.md THIRD_PARTY_NOTICES.md docs tests
git commit -m "docs: prepare AIEverything 1.0.1 release"
```

### Task 6: Build, real-window proof, and GitHub Release

**Files:**
- Create: `docs/images/aieverything-1.0.1-docx.png`
- Modify: `README.md`
- Modify: `docs/releases/1.0.1.md`

**Interfaces:**
- Consumes all prior tasks.
- Produces public commit(s), tag `v1.0.1`, and GitHub Release assets.

- [ ] **Step 1: Fetch the frozen local model** and verify its expected SHA-256 through `scripts/fetch-model.ps1`.

- [ ] **Step 2: Run targeted tests, then Release build.**

```powershell
dotnet test tests\AIEverything.Server.Tests\AIEverything.Server.Tests.csproj -c Release --filter "FullyQualifiedName~TextExtractorTests|FullyQualifiedName~SourceLocationResolverTests|FullyQualifiedName~MachineTextIndexPolicyTests|FullyQualifiedName~MachineSnapshotStoreTests|FullyQualifiedName~StandaloneProductContractTests|FullyQualifiedName~PluginContractTests"
dotnet build AIEverything.sln -c Release
```

- [ ] **Step 3: Build the portable package** and verify the ZIP and sidecar hash match.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-standalone.ps1 -SkipTests
Get-FileHash dist\AIEverything-1.0.1-win-x64.zip -Algorithm SHA256
```

- [ ] **Step 4: Run the real promotion loop.** Extract the exact ZIP to a fresh temporary directory. Create one controlled DOCX with a heading, normal paragraph, and table canary plus one corrupt DOCX. Launch the packaged app, enable indexing, prove filename search is immediately usable, prove the three DOCX canaries produce one readable result each, and prove the corrupt file does not block the valid file. Capture a privacy-safe screenshot of a DOCX hit.

- [ ] **Step 5: Add the screenshot and final hash to docs**, rerun the product contracts and open-source audit, then commit.

- [ ] **Step 6: Push branch and publish.** Push `codex/docx-promotion-1.0.1`, open a PR or merge the verified branch to `main`, tag the verified main commit `v1.0.1`, and create the GitHub Release using `docs/releases/1.0.1.md` with the ZIP and checksum sidecar.

- [ ] **Step 7: Verify remote evidence.** Download both assets from GitHub, confirm the published checksum, verify the public README screenshot and direct download link resolve, and confirm the Release is public and not a draft.

- [ ] **Step 8: Product acceptance.** Give the product owner the exact ZIP path/hash, real-window screenshot, controlled-search evidence, and release URL. A PASS is required before marking the goal complete.
