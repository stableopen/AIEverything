# AIEverything 1.0.1 DOCX Internal Promotion Design

## Outcome

A company colleague can download one portable ZIP, launch AIEverything without a development environment, and search filenames immediately plus eligible DOCX, TXT, MD, and MARKDOWN body text after enabling the local index.

The product promise is:

> Search the whole computer by filename like Everything, then find the exact Word or text passage and put the most likely result first.

## Users and core loop

The first users are ordinary company colleagues on Windows 10/11 x64. The core loop is download ZIP, extract, launch, search a filename immediately, enable body indexing once, type a remembered sentence, inspect the matched passage, then open or locate the correct file.

## Scope

- Keep Everything-based filename and path search for every discoverable format.
- Keep body indexing for `.txt`, `.md`, and `.markdown`.
- Add `.docx` as the only new body format.
- Extract DOCX main-document headings, paragraphs, and table cells in document order.
- Show a readable DOCX location: heading path plus paragraph number, or heading path plus table/row/cell coordinates.
- Merge multiple body hits from the same file into one result, preserving representative snippets.
- Prioritize Documents, Desktop, Downloads, and recently modified files during initial indexing.
- Keep filename search usable while body indexing runs.
- Publish `v1.0.1` as a GitHub Release with a portable ZIP, SHA-256 checksum file, real screenshot, three-step instructions, known limitations, and a GitHub Issues feedback link.

## Explicit non-goals

- PDF, XLSX, PPTX, RTF, legacy `.doc`, OCR, email, chat, or cloud-drive ingestion.
- DOCX headers, footers, comments, tracked revisions, footnotes, text boxes, or embedded objects.
- Stable DOCX page numbers or opening Word at the exact hit location.
- New ranking models, RAG, answer generation, semantic recall, installer, updater, or code signing.
- Broad soak or scale testing beyond the minimum real promotion loop.

## DOCX extraction and locations

`OpenXmlTextExtractor` will produce structured blocks rather than joining every `w:t` node. A block has searchable text plus source metadata:

```csharp
public sealed record ExtractedTextBlock(
    int Ordinal,
    string Text,
    string LocationLabel,
    string? HeadingPath = null);

public sealed record ExtractionResult(
    string Text,
    bool Truncated,
    int Characters,
    IReadOnlyList<ExtractedTextBlock>? Blocks = null);
```

Paragraph text is assembled from runs so a sentence is not split at formatting boundaries. Heading depth is resolved from paragraph/style outline levels; heading text updates the current heading path. Ordinary paragraphs receive a one-based paragraph ordinal. Table cells receive `Table N · row R · cell C`. Empty blocks are skipped. Extraction stops cleanly at 1,000,000 searchable characters and records truncation.

The index stores a compact JSON location map beside the flattened searchable text. Search still uses SQLite FTS5. After FTS selects a document, `SourceLocationResolver` matches query terms against blocks and returns the stored label and heading path. TXT and Markdown location behavior remains unchanged.

This requires a schema/protocol/extraction revision bump so existing TXT/Markdown rows are rebuilt consistently and old daemons cannot serve incompatible location data.

## Candidate policy and failure behavior

The machine policy whitelist becomes `.txt`, `.md`, `.markdown`, `.docx`. Text formats retain the 5 MiB limit; DOCX uses 10 MiB. Every format remains limited to 1,000,000 extracted characters. The existing worker-process boundary and 15-second timeout remain authoritative.

Permanent failures are recorded once for the current fingerprint and do not re-enter the queue until the file changes or the user explicitly retries failures. Permanent categories are unsupported/encrypted, corrupt, too large, timeout, and access denied. Unexpected transient I/O failures keep the existing bounded retry schedule. One failure never stops the queue.

## First-use and status experience

The main window remains compact. It uses one short status line:

- Disabled: `Filename search is ready. Enable body indexing to search Word, TXT, and Markdown.`
- Indexing: `Building the body index. {n} files are searchable.`
- Ready: `{n} results · intelligently ranked`
- Paused: `Body indexing is paused. Existing content remains searchable.`
- Filename service unavailable: `Filename service is temporarily unavailable; retrying. Existing body content remains searchable.`
- Partial failure: `Body indexing completed; some files were not processed. See Settings.`

The settings window shows searchable, waiting, and unprocessed counts. Failures are grouped as corrupt, encrypted/unsupported, too large, timeout, and access denied. It retains sync/pause controls and adds `Report a problem`, opening `https://github.com/stableye/AIEverything/issues/new`.

## Release and documentation

The application, daemon, worker, plugin metadata, build scripts, and documentation move to version `1.0.1`. README leads with a direct GitHub Release download instead of requiring users to build. The portable archive includes the local model and all licenses, so colleagues need no SDK or separate model download.

The release contains:

- `AIEverything-1.0.1-win-x64.zip`
- `AIEverything-1.0.1-win-x64.zip.sha256`
- a concise Chinese release description with three usage steps and known limitations
- a real screenshot showing a DOCX body hit and readable location

## Verification

Minimum delivery evidence:

- Targeted red/green tests cover DOCX run joining, heading path, table coordinates, whitelist/limit, location-map persistence, permanent failure classification, and product/version contracts.
- Release solution build succeeds.
- A fresh portable extraction launches without a development environment.
- A controlled DOCX proves body hits in a normal paragraph, under a heading, and in a table cell; the UI shows one merged file result with readable location and snippet.
- A corrupt DOCX fails without preventing a valid DOCX from becoming searchable.
- Filename search remains immediate and unsupported body formats remain filename-searchable.
- GitHub Release assets download successfully and their SHA-256 matches the published checksum.

## Source control boundary

The clean public repository `stableye/AIEverything` is the source of truth for 1.0.1 and later promotion releases. The old private experiment repository and its historical ZIP files remain untouched. Development occurs on `codex/docx-promotion-1.0.1`, then publishes reviewed commits and tag `v1.0.1` to the public repository.
