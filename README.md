# CodeRag

CodeRag is a local-first codebase retrieval tool for .NET solutions. It parses C# projects with Roslyn, chunks symbols with metadata, stores lexical and vector indexes in SQLite, and exposes both a CLI and MCP server for semantic code search.

Use it when an AI coding agent needs fast answers to questions like "where is this service implemented?", "which classes implement this interface?", or "show me code related to this behavior" without repeatedly scanning the whole repository.

## Features

- Index .NET solutions into a local SQLite database.
- Generate embeddings with OpenAI or Voyage.
- Query code with semantic search plus filters for symbol kind, accessibility, attributes, interfaces, and return types.
- Look up exact symbols, interface implementations, and attributed members through MCP tools.
- Keep architecture constraints enforced with custom analyzers and architecture tests.

## First-time setup

After scaffolding (`dotnet new cli -n CodeRag`), run once:

```powershell
.\setup.ps1
```

This initializes a git repo, activates `.githooks/` for the project lifecycle, and creates the initial commit.

## Build and test

```powershell
dotnet restore
dotnet build
dotnet test
```

## Development lifecycle

See [CLAUDE.md](./CLAUDE.md) for the full lifecycle (issue → branch → commit → PR).

Quick summary:
1. `gh issue create --title "..."` (every change starts with an issue)
2. `git checkout -b feat/<issue-num>-<slug>` (`reference-transaction` hook verifies the issue exists)
3. Edit + commit (pre-commit hook runs build, format, tests)
4. `gh pr create` and squash-merge

Direct commits to `main` are blocked. Edits to already-merged branches are blocked.
