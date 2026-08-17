---
name: aieverything-search
description: Use when an AI Agent should perform read-only searches for local Windows files, folders, paths, or indexed TXT and Markdown content through AIEverything and Everything without recursive whole-drive scans.
---

# AIEverything Search 1.0.0

Use this Skill as an optional read-only adapter to a separately installed AIEverything desktop app. It is not bundled with the desktop portable package, and the desktop app does not require an Agent or Codex to run.

## Prerequisites

- Open AIEverything so its per-user content service is available.
- Filename/path search requires the local Everything engine.
- Content search reads only the desktop app's existing fixed-disk index for `.txt`, `.md`, and `.markdown` files.

Do not start another daemon, change index settings, add roots, write files, or treat a missing result as proof that content does not exist.

## Choose a read-only tool

- `search_local_files`: search filename/path metadata with optional path, extension, kind, date, sort, limit, and offset filters.
- `search_everything_query`: use explicit Everything 1.4 query syntax.
- `aieverything_status`: inspect Everything metadata-search readiness.
- `search_local_content`: search indexed TXT/Markdown bodies and locations.
- `search_local_hybrid`: combine filename/path and indexed body matches.
- `aieverything_index_status`: inspect content-service readiness and indexing status.

## Workflow

1. Prefer `search_local_files` for broad discovery by name or path. Use a known absolute scope and a 5–20 item limit when possible.
2. Use `search_local_content` when the user asks what a text or Markdown document says.
3. Use `search_local_hybrid` when either a matching name or body is useful.
4. Check `aieverything_index_status` when content coverage is uncertain.
5. Read only the returned candidate files needed for the task.
6. Fall back to a targeted native filesystem search only when Everything is unavailable and the user supplied a narrow directory. Never fall back to a recursive whole-drive scan.
7. If a location or format is not indexed for content, state the limitation and use targeted reads only when appropriate.

## Boundaries

Filename/path metadata and document-body search are separate. AIEverything 1.0.0 body indexing is local, read-only, and limited to eligible TXT/MD/MARKDOWN on fixed NTFS/ReFS disks. It excludes system, cache, dependency, build, repository, other-user and unsafe paths. It does not index PDF, Office, source code, logs, email/chat, network/removable drives, archives or OCR content.

The adapter must never modify, delete, move, upload, or claim to authorize a user's files.
