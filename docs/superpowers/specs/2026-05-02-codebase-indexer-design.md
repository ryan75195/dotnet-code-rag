# Codebase Indexer — Design Spec

**Date:** 2026-05-02
**Status:** Draft, pending implementation plan
**Owner:** ryan75195

## Goal

Add a new CLI verb to CodeRag that takes a `.sln` and produces a single, portable, queryable index file describing every code symbol in the solution, with an embedding per symbol and rich Roslyn-derived metadata for filtering.

The indexer is the **writer side** of CodeRag. A separate spec will cover the reader/query side. Designing the artifact now so the writer and reader stay decoupled.

## Non-goals (v1)

- Query verb / CLI search — separate feature, separate spec.
- MCP server — later, on top of the same `index.db` artifact.
- Languages other than C#.
- Call graph, inverse references, cyclomatic complexity.
- Multi-solution indices in one DB.
- Online schema migrations.
- Hybrid BM25 + vector search, re-ranking.

## High-level decisions

| Decision | Choice |
|---|---|
| Embedding source | OpenAI `text-embedding-3-large` (3072-dim), hardcoded. API key from `OPENAI_API_KEY`. |
| Chunk granularity | Roslyn syntactic chunks (one chunk per type / member). C# only. |
| Index format | Single SQLite file with the `sqlite-vec` extension. Vectors and metadata in one artifact, joinable by `rowid`. |
| Update strategy | Git-aware incremental: diff between last indexed commit and current `HEAD` (plus uncommitted changes via `git status`), reconcile per file by FQN + content hash. `.sln` change forces full reindex. |
| Default output | `<sln-dir>/.coderag/index.db` (gitignored). |

## CLI surface

```
coderag index <path-to-sln> [--out <path>]
```

- `--out` defaults to `<sln-dir>/.coderag/index.db`.
- `OPENAI_API_KEY` env var is required. Indexer fails fast at startup if absent.
- No `--model` flag in v1; embedding model is hardcoded.

## Code organisation

Slots into existing `Cli` / `Core` / `Analyzers` split. Existing architecture tests in `CodeRag.Tests.Architecture` enforce the rules below automatically.

**`CodeRag.Cli`** — the `index` verb only. Argument parsing, top-level orchestration, console output. Thin.

**`CodeRag.Core`** — every interface and implementation that does real work, all registered via `AddCoreServices()`:

| Interface | Responsibility |
|---|---|
| `IWorkspaceLoader` | Open `.sln` with `MSBuildWorkspace`. Return projects + compilations. |
| `IChunkExtractor` | Walk a `Compilation` / `SyntaxTree`. Emit `CodeChunk` records. |
| `IGitDiffProvider` | `GetHeadShaAsync()`, `GetChangedFilesSinceAsync(sha)`, `GetDirtyFilesAsync()`. Wraps `git` CLI. |
| `IEmbeddingClient` | Batched OpenAI calls. Retry/backoff. Token-truncation. |
| `IIndexStore` | Open / migrate the SQLite file. Upsert / delete chunks. Read / write `index_metadata`. |
| `IIndexer` | Orchestrator. Pulls the others together. Runs full or incremental. |

## Data model

Single SQLite file. Three "tiers": metadata (1 row), chunks (1 row per symbol + 1:N child tables), and a `vec0` virtual table for embeddings.

```sql
CREATE TABLE index_metadata (
    metadata_id                    INTEGER PRIMARY KEY CHECK (metadata_id = 1),
    schema_version                 INTEGER NOT NULL,
    solution_file_path             TEXT NOT NULL,
    repository_root_path           TEXT NOT NULL,
    indexed_at_commit_sha          TEXT NOT NULL,
    indexed_at_utc                 TEXT NOT NULL,
    embedding_model_name           TEXT NOT NULL,
    embedding_vector_dimensions    INTEGER NOT NULL
);

CREATE TABLE code_chunks (
    chunk_id                              INTEGER PRIMARY KEY,

    -- Location
    containing_project_name               TEXT NOT NULL,
    containing_assembly_name              TEXT NOT NULL,
    relative_file_path                    TEXT NOT NULL,
    start_line_number                     INTEGER NOT NULL,
    end_line_number                       INTEGER NOT NULL,

    -- Symbol identity
    symbol_kind                           TEXT NOT NULL,
    symbol_display_name                   TEXT NOT NULL,
    symbol_signature_display              TEXT NOT NULL,
    fully_qualified_symbol_name           TEXT NOT NULL,
    containing_namespace                  TEXT NULL,
    parent_symbol_fully_qualified_name    TEXT NULL,

    -- Visibility & modifiers
    accessibility                         TEXT NOT NULL,
    is_static                             INTEGER NOT NULL DEFAULT 0,
    is_abstract                           INTEGER NOT NULL DEFAULT 0,
    is_sealed                             INTEGER NOT NULL DEFAULT 0,
    is_virtual                            INTEGER NOT NULL DEFAULT 0,
    is_override                           INTEGER NOT NULL DEFAULT 0,
    is_async                              INTEGER NOT NULL DEFAULT 0,
    is_partial                            INTEGER NOT NULL DEFAULT 0,
    is_readonly                           INTEGER NOT NULL DEFAULT 0,
    is_extern                             INTEGER NOT NULL DEFAULT 0,
    is_unsafe                             INTEGER NOT NULL DEFAULT 0,
    is_extension_method                   INTEGER NOT NULL DEFAULT 0,
    is_generic                            INTEGER NOT NULL DEFAULT 0,

    -- Type-specific (NULL for non-types)
    base_type_fully_qualified_name        TEXT NULL,

    -- Method-specific (NULL otherwise)
    return_type_fully_qualified_name      TEXT NULL,
    parameter_count                       INTEGER NULL,

    -- Documentation
    documentation_comment_xml             TEXT NULL,

    -- Source + embedding bookkeeping
    source_text                           TEXT NOT NULL,
    source_text_hash                      TEXT NOT NULL
);

CREATE TABLE chunk_attributes (
    attribute_id                          INTEGER PRIMARY KEY,
    chunk_id                              INTEGER NOT NULL REFERENCES code_chunks(chunk_id) ON DELETE CASCADE,
    attribute_fully_qualified_name        TEXT NOT NULL,
    attribute_arguments_json              TEXT NULL
);

CREATE TABLE chunk_implemented_interfaces (
    chunk_id                              INTEGER NOT NULL REFERENCES code_chunks(chunk_id) ON DELETE CASCADE,
    interface_fully_qualified_name        TEXT NOT NULL,
    PRIMARY KEY (chunk_id, interface_fully_qualified_name)
);

CREATE TABLE chunk_method_parameters (
    chunk_id                              INTEGER NOT NULL REFERENCES code_chunks(chunk_id) ON DELETE CASCADE,
    parameter_ordinal                     INTEGER NOT NULL,
    parameter_name                        TEXT NOT NULL,
    parameter_type_fully_qualified_name   TEXT NOT NULL,
    parameter_modifier                    TEXT NULL,
    has_default_value                     INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (chunk_id, parameter_ordinal)
);

CREATE TABLE chunk_generic_type_parameters (
    chunk_id                              INTEGER NOT NULL REFERENCES code_chunks(chunk_id) ON DELETE CASCADE,
    parameter_ordinal                     INTEGER NOT NULL,
    parameter_name                        TEXT NOT NULL,
    constraints_json                      TEXT NULL,
    PRIMARY KEY (chunk_id, parameter_ordinal)
);

CREATE INDEX idx_chunks_file          ON code_chunks(relative_file_path);
CREATE INDEX idx_chunks_kind          ON code_chunks(symbol_kind);
CREATE INDEX idx_chunks_project       ON code_chunks(containing_project_name);
CREATE INDEX idx_chunks_namespace     ON code_chunks(containing_namespace);
CREATE INDEX idx_chunks_fqn           ON code_chunks(fully_qualified_symbol_name);
CREATE INDEX idx_chunks_parent        ON code_chunks(parent_symbol_fully_qualified_name);
CREATE INDEX idx_chunks_accessibility ON code_chunks(accessibility);
CREATE INDEX idx_chunks_return_type   ON code_chunks(return_type_fully_qualified_name);
CREATE INDEX idx_chunks_base_type     ON code_chunks(base_type_fully_qualified_name);
CREATE INDEX idx_attributes_name      ON chunk_attributes(attribute_fully_qualified_name);
CREATE INDEX idx_interfaces_name      ON chunk_implemented_interfaces(interface_fully_qualified_name);
CREATE INDEX idx_params_type          ON chunk_method_parameters(parameter_type_fully_qualified_name);

CREATE VIRTUAL TABLE chunk_embeddings USING vec0(embedding float[3072]);
-- chunk_embeddings.rowid kept in sync with code_chunks.chunk_id
```

### `symbol_kind` enumeration

`namespace`, `class`, `interface`, `struct`, `record_class`, `record_struct`, `enum`, `delegate`, `method`, `constructor`, `destructor`, `property`, `field`, `event`, `operator`, `conversion_operator`, `indexer`.

`namespace` is reserved but not emitted as a chunk in v1 (containers only).

### What goes in `source_text`

`source_text` is the input fed to the embedding model. Designed so the same code is never embedded twice.

| Symbol kind | `source_text` contains |
|---|---|
| method, constructor, destructor, operator, conversion_operator, indexer | doc comment + full declaration including body |
| property | doc comment + signature + accessor list |
| class, struct, record_class, record_struct, interface | doc comment + signature + member signatures only (no member bodies — those are their own chunks) |
| enum | doc comment + full declaration including all members |
| delegate, event, field | doc comment + full declaration |

### Example filtered queries (consumer side)

```sql
-- Public async methods returning Task<>
WHERE symbol_kind = 'method' AND is_async = 1 AND accessibility = 'public'
  AND return_type_fully_qualified_name LIKE 'System.Threading.Tasks.Task%'

-- Classes implementing ILogger
JOIN chunk_implemented_interfaces ii USING (chunk_id)
WHERE ii.interface_fully_qualified_name = 'Microsoft.Extensions.Logging.ILogger'

-- ASP.NET controller actions
JOIN chunk_attributes a USING (chunk_id)
WHERE a.attribute_fully_qualified_name LIKE 'Microsoft.AspNetCore.Mvc.HttpGetAttribute%'

-- Methods that take CancellationToken
JOIN chunk_method_parameters p USING (chunk_id)
WHERE p.parameter_type_fully_qualified_name = 'System.Threading.CancellationToken'
```

A filtered semantic query layers a `vec_chunks MATCH :q AND k = N` predicate on top of any of the above.

## Indexing pipeline

```
1.  Open / create  index.db   (run schema migrations)

2.  Resolve change set:
      head_sha     = git rev-parse HEAD
      last_indexed = SELECT indexed_at_commit_sha FROM index_metadata
      if last_indexed IS NULL → fullReindex = true
      else:
        committed   = git diff --name-only <last_indexed> HEAD -- '*.cs' '*.csproj' '*.sln'
        uncommitted = git status --porcelain (filtered to *.cs / *.csproj / *.sln)
        changedFiles = committed ∪ uncommitted
        if any '*.sln' in changedFiles → fullReindex = true

3.  MSBuildLocator.RegisterDefaults()
    workspace = MSBuildWorkspace.Create()
    solution  = workspace.OpenSolutionAsync(slnPath)

4.  Determine processing scope:
      full          → all projects, all files
      '*.csproj' Δ → that whole project
      '*.cs' Δ     → just those files

5.  For each (project, file) to process:
      compilation = project.GetCompilationAsync()
      chunks      = ChunkExtractor.Extract(compilation, fileSyntaxTree)

6.  Reconcile per file (see algorithm below)

7.  Batch-embed all queued inserts/updates with OpenAI

8.  Apply embeddings (UPSERT into chunk_embeddings)

9.  UPDATE index_metadata SET indexed_at_commit_sha = head_sha, indexed_at_utc = now()
```

### Skip rules during chunk extraction

- Source-generator output (`tree.FilePath` outside `repository_root_path`, or `tree.GetRoot().IsGeneratedCode()`).
- Files outside the repository root (linked source from NuGet symbols, etc.).
- Compiler-generated members (`ISymbol.IsImplicitlyDeclared`).

### Partial types

One chunk per partial declaration. Each gets `is_partial = 1` and the same `fully_qualified_symbol_name`. Reconciliation matches partials on the composite key `(fully_qualified_symbol_name, relative_file_path)`.

### Reconciliation algorithm (per file)

```
oldChunks = SELECT chunk_id, fully_qualified_symbol_name, source_text_hash
            FROM code_chunks WHERE relative_file_path = :path
newChunks = ChunkExtractor.Extract(...)

byFqn_old = oldChunks indexed by fully_qualified_symbol_name
byFqn_new = newChunks indexed by fully_qualified_symbol_name

for fqn in byFqn_old.keys ∪ byFqn_new.keys:
    case (fqn in old, fqn in new):
        (true, false)  → DELETE chunk_id
                          (CASCADE drops attributes / interfaces / parameters / generics / embedding)
        (false, true)  → INSERT row + child rows; QUEUE for embedding
        (true, true):
            if old.source_text_hash == new.source_text_hash AND signature unchanged:
                no-op
            else:
                UPDATE row + replace child rows
                if source_text_hash changed → QUEUE for embedding
                else → keep existing embedding
```

For files entirely deleted between commits: `DELETE FROM code_chunks WHERE relative_file_path = :path`.

### Embedding step

- OpenAI `embeddings.create` with batches of up to **96 inputs per request**.
- **Token-truncate** every `source_text` to ~8,000 tokens (model limit is 8,191).
- **Retry policy:** exponential backoff (1s, 2s, 4s, 8s, 16s) on `429` / `5xx` / network errors. Hard-fail after 5 attempts and roll back the transaction.
- **Concurrency:** up to 4 in-flight embedding requests. DB writes serialised.
- API key from `OPENAI_API_KEY` env var. Fail fast at startup with a clear message if absent.

### Failure modes & guarantees

| Failure | Behaviour |
|---|---|
| Crash mid-run | DB unchanged (whole pass wrapped in a single SQLite transaction). |
| OpenAI hard-fails after retries | Transaction rolls back, `indexed_at_commit_sha` not updated, next run retries from same baseline. |
| Git diff lists a file that no longer exists | Treated as deletion; rows dropped by `relative_file_path`. |
| `.sln` change | Full reindex (project list may have shifted). |
| Schema version mismatch | Fail with a clear "delete index.db and re-run" message. No online migrations in v1. `schema_version` starts at `1`. |

## Testing strategy

Slots into existing `CodeRag.Tests.*` projects.

### `CodeRag.Tests.Architecture` (existing rules apply automatically)

- All new public `Core` interfaces must be registered in `AddCoreServices()`.
- One public type per file.
- No `//`/`///` comments anywhere (CI0013 at error severity).

### `CodeRag.Tests.Unit` — pure logic, no I/O

- **`ChunkExtractorTests`** — drive `IChunkExtractor` against in-memory `CSharpSyntaxTree.ParseText(...)` fixtures.
  - One test per `symbol_kind`.
  - Partial classes produce one chunk per declaration with the same FQN.
  - Generic methods populate `chunk_generic_type_parameters` correctly.
  - `[Obsolete("...")]` round-trips through `chunk_attributes` with arguments.
  - Methods with `ref`/`out`/`in`/`params` populate `parameter_modifier`.
  - Source-generated trees and files outside the repo root are skipped.
  - `source_text` for a class excludes member bodies (regression test against double-embedding).
- **`ReconciliationTests`** — given (oldChunks, newChunks) input, verify the insert/update/delete/no-op decisions. Pure function, no DB.
- **`HashingTests`** — `source_text_hash` is a stable SHA-256 over the canonical `source_text` we store. (No whitespace normalisation in v1 — hash exactly what we embed, so two equal embeddings imply two equal hashes.)

### `CodeRag.Tests.Integration` — real DB, real Roslyn, fake embeddings

- Tiny fixture solution checked in under `tests/Fixtures/SampleSolution/` (~3 projects, ~10 files, mix of classes/records/methods/properties/attributes/interfaces).
- `IEmbeddingClient` swapped for a deterministic fake (`hash(source_text) → 3072 floats`). No network in CI.
- Scenarios:
  1. **Cold index** — fresh DB, run indexer, assert row counts per `symbol_kind`, `index_metadata.indexed_at_commit_sha` populated.
  2. **No-op re-run** — index twice with no source changes, second run produces zero embedding calls.
  3. **Edit one method** — modify a fixture file, run, assert exactly one row updated and one embedding call.
  4. **Add/remove a class** — assert child rows (attributes, interfaces, parameters, generics, embedding) are inserted / cascade-deleted correctly.
  5. **Delete a file** — assert all rows for that path are gone.
  6. **`.csproj` change** — assert that whole project's files get re-parsed.
  7. **`.sln` change** — assert full reindex path runs.
  8. **Filtered query smoke test** — issue a known-good filtered KNN query against the fixture and assert expected symbols come back.
- `IGitDiffProvider` is stubbed in these tests so the test can drive change sets directly without managing a real git repo. A single end-to-end "real git" smoke test runs `IGitDiffProvider` against the host CodeRag repo to confirm the production wiring.

### `CodeRag.Tests.Analyzers`

Unchanged. Indexer code is subject to existing CI0001–CI0013.

## Open questions

None blocking. Everything else gets pinned during the implementation plan.
