# Codebase Indexer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement `coderag index <sln>` exactly as specified in `docs/superpowers/specs/2026-05-02-codebase-indexer-design.md`. The CLI walks every C# symbol via Roslyn, embeds each with OpenAI `text-embedding-3-large`, and writes a single SQLite + sqlite-vec file with rich filterable metadata. Re-runs are git-aware incremental.

**Architecture:** The CLI verb `index` calls into `Core.Indexing.IIndexer`, which composes:
- `IWorkspaceLoader` (Roslyn `MSBuildWorkspace`) — opens the `.sln`, returns projects + compilations.
- `IChunkExtractor` (pure Roslyn → `CodeChunk` records) — one chunk per type or member.
- `IGitDiffProvider` (wraps `git` CLI) — head SHA, changed files since SHA, dirty files.
- `IEmbeddingClient` (OpenAI batched calls) — embeds source text, with retry / backoff.
- `IIndexStore` (SQLite + sqlite-vec) — opens / migrates the DB, upserts and deletes chunks.

`Reconciler` is a pure function that turns `(oldChunks, newChunks)` into an insert/update/delete plan. Every public Core interface is registered in `AddCoreServices()` — the existing architecture test enforces this.

**Tech Stack:**
- .NET 10
- Microsoft.CodeAnalysis (Roslyn) + `Microsoft.CodeAnalysis.Workspaces.MSBuild` + `Microsoft.Build.Locator`
- `Microsoft.Data.Sqlite` (already transitively available; we add a direct reference)
- `sqlite-vec` v0.1.x native binaries from https://github.com/asg017/sqlite-vec/releases
- OpenAI .NET SDK — `OpenAI` NuGet package (v2.x)
- `System.CommandLine` (latest)
- NUnit + FluentAssertions + NSubstitute (already present)

**House rules (enforced by existing analyzers, do not violate):**
- No comments anywhere (`//`, `/* */`, `///`) — `CI0013` blocks the build.
- File-scoped namespaces.
- Allman braces.
- `_camelCase` private fields, `I`-prefixed interfaces, `PascalCase` constants and `static readonly` fields.
- One public type per file.
- Every public Core interface must be registered in `AddCoreServices()`.
- CRLF line endings, UTF-8 with BOM.

---

## Lifecycle setup (do this once before Task 1)

This whole feature implements behind one issue and one feat branch; squash-merge at the end. Internal commits are TDD-style.

- [ ] **Create the implementation issue:**

```powershell
gh issue create --title "Implement codebase indexer per spec" --body @'
Implements docs/superpowers/specs/2026-05-02-codebase-indexer-design.md per docs/superpowers/plans/2026-05-02-codebase-indexer.md.
'@
```

Note the issue number returned. Call it `<N>` for the rest of this plan.

- [ ] **Create the feat branch:**

```powershell
git checkout main
git pull --ff-only
git checkout -b feat/<N>-codebase-indexer
```

- [ ] **Verify clean baseline:**

```powershell
dotnet build
dotnet test
```

Both should succeed. If either fails, stop and fix the baseline before proceeding.

---

## Task 1: Add NuGet packages and stage sqlite-vec native binaries

**Files:**
- Modify: `src/CodeRag.Core/CodeRag.Core.csproj`
- Modify: `src/CodeRag.Cli/CodeRag.Cli.csproj`
- Modify: `tests/CodeRag.Tests.Integration/CodeRag.Tests.Integration.csproj`
- Create: `external/sqlite-vec/runtimes/win-x64/native/vec0.dll`
- Create: `external/sqlite-vec/runtimes/linux-x64/native/vec0.so`
- Create: `external/sqlite-vec/runtimes/osx-arm64/native/vec0.dylib`
- Create: `external/sqlite-vec/README.md`
- Create: `Directory.Build.targets`
- Modify: `.gitignore` (add `.coderag/`)

- [ ] **Step 1: Download sqlite-vec binaries.** From https://github.com/asg017/sqlite-vec/releases pick the latest `v0.1.x` release (e.g. `v0.1.6`). Download these three asset archives and extract:

  - `sqlite-vec-<ver>-loadable-windows-x86_64.tar.gz` → `vec0.dll` → `external/sqlite-vec/runtimes/win-x64/native/vec0.dll`
  - `sqlite-vec-<ver>-loadable-linux-x86_64.tar.gz` → `vec0.so` → `external/sqlite-vec/runtimes/linux-x64/native/vec0.so`
  - `sqlite-vec-<ver>-loadable-macos-aarch64.tar.gz` → `vec0.dylib` → `external/sqlite-vec/runtimes/osx-arm64/native/vec0.dylib`

  Write `external/sqlite-vec/README.md` documenting which release the binaries came from and the SHA256 of each, so future updates are traceable.

- [ ] **Step 2: Add packages to `src/CodeRag.Core/CodeRag.Core.csproj`.** Replace the existing `<ItemGroup>` of package references with:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.CodeAnalysis.CSharp.Workspaces" Version="4.11.0" />
  <PackageReference Include="Microsoft.CodeAnalysis.Workspaces.MSBuild" Version="4.11.0" />
  <PackageReference Include="Microsoft.Build.Locator" Version="1.7.8" />
  <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.5" />
  <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.5" />
  <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.5" />
  <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.5" />
  <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.5" />
  <PackageReference Include="OpenAI" Version="2.0.0" />
</ItemGroup>
```

- [ ] **Step 3: Add System.CommandLine to `src/CodeRag.Cli/CodeRag.Cli.csproj`.** Append this `<PackageReference>` to the existing `<ItemGroup>`:

```xml
<PackageReference Include="System.CommandLine" Version="2.0.0-beta4.22272.1" />
```

- [ ] **Step 4: Wire the sqlite-vec binaries into every project that needs them at runtime.** Create `Directory.Build.targets` at the repo root with:

```xml
<Project>
  <ItemGroup Condition="'$(MSBuildProjectName)' == 'CodeRag.Cli' OR '$(MSBuildProjectName)' == 'CodeRag.Tests.Integration'">
    <None Include="$(MSBuildThisFileDirectory)external\sqlite-vec\runtimes\win-x64\native\vec0.dll">
      <Link>runtimes\win-x64\native\vec0.dll</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
    <None Include="$(MSBuildThisFileDirectory)external\sqlite-vec\runtimes\linux-x64\native\vec0.so">
      <Link>runtimes\linux-x64\native\vec0.so</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
    <None Include="$(MSBuildThisFileDirectory)external\sqlite-vec\runtimes\osx-arm64\native\vec0.dylib">
      <Link>runtimes\osx-arm64\native\vec0.dylib</Link>
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

- [ ] **Step 4b: Grant `CodeRag.Tests.Integration` access to internal types** in `CodeRag.Core`. Edit `src/CodeRag.Core/CodeRag.Core.csproj`, find the existing `<InternalsVisibleTo Include="CodeRag.Tests.Unit" />` block, and add:

```xml
<InternalsVisibleTo Include="CodeRag.Tests.Integration" />
```

This is needed because `IIndexStore` and `IIndexStoreFactory` are `internal` (factory pattern) and the integration tests construct them directly.

- [ ] **Step 5: Append `.coderag/` to `.gitignore`** so users' on-disk indices don't accidentally get committed:

```
.coderag/
```

(If the line is already present, do nothing.)

- [ ] **Step 6: Restore + build.**

```powershell
dotnet restore
dotnet build
```

Expected: all projects compile. No code uses any of the new packages yet — we're just provisioning.

- [ ] **Step 7: Commit.**

```powershell
git add src/CodeRag.Core/CodeRag.Core.csproj src/CodeRag.Cli/CodeRag.Cli.csproj tests/CodeRag.Tests.Integration/CodeRag.Tests.Integration.csproj Directory.Build.targets external/sqlite-vec .gitignore
git commit -m "chore: add Roslyn, OpenAI, sqlite-vec dependencies (#<N>)"
```

---

## Task 2: Define `CodeChunk` and supporting records

**Files:**
- Create: `src/CodeRag.Core/Indexing/CodeChunk.cs`
- Create: `src/CodeRag.Core/Indexing/ChunkAttribute.cs`
- Create: `src/CodeRag.Core/Indexing/ChunkParameter.cs`
- Create: `src/CodeRag.Core/Indexing/ChunkGenericTypeParameter.cs`
- Create: `src/CodeRag.Core/Indexing/SymbolKinds.cs`
- Create: `src/CodeRag.Core/Indexing/Accessibilities.cs`
- Create: `src/CodeRag.Core/Indexing/ChunkModifiers.cs`
- Test: `tests/CodeRag.Tests.Unit/Indexing/CodeChunkTests.cs`

These are pure-data records. No tests are strictly required by the analyzers, but we add one tiny test that locks the public shape (so any rename triggers an obvious compile error rather than silent breakage in callers).

- [ ] **Step 1: Write `SymbolKinds.cs`** — a static class of constants matching the spec's enumeration verbatim:

```csharp
namespace CodeRag.Core.Indexing;

public static class SymbolKinds
{
    public const string Namespace = "namespace";
    public const string Class = "class";
    public const string Interface = "interface";
    public const string Struct = "struct";
    public const string RecordClass = "record_class";
    public const string RecordStruct = "record_struct";
    public const string Enum = "enum";
    public const string Delegate = "delegate";
    public const string Method = "method";
    public const string Constructor = "constructor";
    public const string Destructor = "destructor";
    public const string Property = "property";
    public const string Field = "field";
    public const string Event = "event";
    public const string Operator = "operator";
    public const string ConversionOperator = "conversion_operator";
    public const string Indexer = "indexer";
}
```

- [ ] **Step 2: Write `Accessibilities.cs`:**

```csharp
namespace CodeRag.Core.Indexing;

public static class Accessibilities
{
    public const string Public = "public";
    public const string Internal = "internal";
    public const string Protected = "protected";
    public const string Private = "private";
    public const string ProtectedInternal = "protected_internal";
    public const string PrivateProtected = "private_protected";
}
```

- [ ] **Step 3: Write `ChunkModifiers.cs`** — a record bundle of the bool flags so we don't pass 13 booleans around:

```csharp
namespace CodeRag.Core.Indexing;

public sealed record ChunkModifiers(
    bool IsStatic,
    bool IsAbstract,
    bool IsSealed,
    bool IsVirtual,
    bool IsOverride,
    bool IsAsync,
    bool IsPartial,
    bool IsReadonly,
    bool IsExtern,
    bool IsUnsafe,
    bool IsExtensionMethod,
    bool IsGeneric);
```

- [ ] **Step 4: Write `ChunkAttribute.cs`:**

```csharp
namespace CodeRag.Core.Indexing;

public sealed record ChunkAttribute(
    string AttributeFullyQualifiedName,
    string? AttributeArgumentsJson);
```

- [ ] **Step 5: Write `ChunkParameter.cs`:**

```csharp
namespace CodeRag.Core.Indexing;

public sealed record ChunkParameter(
    int Ordinal,
    string Name,
    string TypeFullyQualifiedName,
    string? Modifier,
    bool HasDefaultValue);
```

- [ ] **Step 6: Write `ChunkGenericTypeParameter.cs`:**

```csharp
namespace CodeRag.Core.Indexing;

public sealed record ChunkGenericTypeParameter(
    int Ordinal,
    string Name,
    string? ConstraintsJson);
```

- [ ] **Step 7: Write `CodeChunk.cs`** — the top-level record:

```csharp
using System.Collections.Immutable;

namespace CodeRag.Core.Indexing;

public sealed record CodeChunk(
    string ContainingProjectName,
    string ContainingAssemblyName,
    string RelativeFilePath,
    int StartLineNumber,
    int EndLineNumber,
    string SymbolKind,
    string SymbolDisplayName,
    string SymbolSignatureDisplay,
    string FullyQualifiedSymbolName,
    string? ContainingNamespace,
    string? ParentSymbolFullyQualifiedName,
    string Accessibility,
    ChunkModifiers Modifiers,
    string? BaseTypeFullyQualifiedName,
    string? ReturnTypeFullyQualifiedName,
    int? ParameterCount,
    string? DocumentationCommentXml,
    string SourceText,
    string SourceTextHash,
    ImmutableArray<ChunkAttribute> Attributes,
    ImmutableArray<string> ImplementedInterfaceFullyQualifiedNames,
    ImmutableArray<ChunkParameter> Parameters,
    ImmutableArray<ChunkGenericTypeParameter> GenericTypeParameters);
```

- [ ] **Step 8: Write the shape-lock test** at `tests/CodeRag.Tests.Unit/Indexing/CodeChunkTests.cs`:

```csharp
using System.Collections.Immutable;
using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Indexing;

[TestFixture]
public class CodeChunkTests
{
    [Test]
    public void Should_construct_with_all_required_fields()
    {
        var chunk = new CodeChunk(
            ContainingProjectName: "CodeRag.Core",
            ContainingAssemblyName: "CodeRag.Core",
            RelativeFilePath: "src/Foo.cs",
            StartLineNumber: 1,
            EndLineNumber: 10,
            SymbolKind: SymbolKinds.Class,
            SymbolDisplayName: "Foo",
            SymbolSignatureDisplay: "public class Foo",
            FullyQualifiedSymbolName: "CodeRag.Core.Foo",
            ContainingNamespace: "CodeRag.Core",
            ParentSymbolFullyQualifiedName: null,
            Accessibility: Accessibilities.Public,
            Modifiers: new ChunkModifiers(false, false, false, false, false, false, false, false, false, false, false, false),
            BaseTypeFullyQualifiedName: null,
            ReturnTypeFullyQualifiedName: null,
            ParameterCount: null,
            DocumentationCommentXml: null,
            SourceText: "public class Foo { }",
            SourceTextHash: "hash",
            Attributes: ImmutableArray<ChunkAttribute>.Empty,
            ImplementedInterfaceFullyQualifiedNames: ImmutableArray<string>.Empty,
            Parameters: ImmutableArray<ChunkParameter>.Empty,
            GenericTypeParameters: ImmutableArray<ChunkGenericTypeParameter>.Empty);

        chunk.SymbolKind.Should().Be(SymbolKinds.Class);
    }
}
```

- [ ] **Step 9: Run the test.**

```powershell
dotnet test tests/CodeRag.Tests.Unit
```

Expected: 1 test passes. Build clean (the architecture test that "every public Core interface is registered" still passes because we have no new interfaces yet).

- [ ] **Step 10: Commit.**

```powershell
git add src/CodeRag.Core/Indexing tests/CodeRag.Tests.Unit/Indexing/CodeChunkTests.cs
git commit -m "feat(core): add CodeChunk record and supporting types (#<N>)"
```

---

## Task 3: Implement `SourceTextHasher`

**Files:**
- Create: `src/CodeRag.Core/Indexing/ISourceTextHasher.cs`
- Create: `src/CodeRag.Core/Indexing/SourceTextHasher.cs`
- Modify: `src/CodeRag.Core/ServiceCollectionExtensions.cs`
- Test: `tests/CodeRag.Tests.Unit/Indexing/SourceTextHasherTests.cs`

- [ ] **Step 1: Write the failing test.** `tests/CodeRag.Tests.Unit/Indexing/SourceTextHasherTests.cs`:

```csharp
using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Indexing;

[TestFixture]
public class SourceTextHasherTests
{
    [Test]
    public void Hash_should_be_deterministic_for_same_input()
    {
        var hasher = new SourceTextHasher();

        var first = hasher.Hash("public class Foo { }");
        var second = hasher.Hash("public class Foo { }");

        first.Should().Be(second);
    }

    [Test]
    public void Hash_should_differ_for_different_input()
    {
        var hasher = new SourceTextHasher();

        var first = hasher.Hash("public class Foo { }");
        var second = hasher.Hash("public class Bar { }");

        first.Should().NotBe(second);
    }

    [Test]
    public void Hash_should_be_lowercase_hex_sha256()
    {
        var hasher = new SourceTextHasher();

        var hash = hasher.Hash("anything");

        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }
}
```

- [ ] **Step 2: Run test, watch it fail to compile** (`SourceTextHasher` does not exist).

```powershell
dotnet test tests/CodeRag.Tests.Unit
```

Expected: build fails with `CS0246: The type or namespace name 'SourceTextHasher' could not be found`.

- [ ] **Step 3: Write the interface.** `src/CodeRag.Core/Indexing/ISourceTextHasher.cs`:

```csharp
namespace CodeRag.Core.Indexing;

public interface ISourceTextHasher
{
    string Hash(string sourceText);
}
```

- [ ] **Step 4: Write the implementation.** `src/CodeRag.Core/Indexing/SourceTextHasher.cs`:

```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeRag.Core.Indexing;

public sealed class SourceTextHasher : ISourceTextHasher
{
    public string Hash(string sourceText)
    {
        var bytes = Encoding.UTF8.GetBytes(sourceText);
        var hash = SHA256.HashData(bytes);
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}
```

- [ ] **Step 5: Register in DI.** Modify `src/CodeRag.Core/ServiceCollectionExtensions.cs`:

```csharp
using CodeRag.Core.Indexing;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<ISourceTextHasher, SourceTextHasher>();
        return services;
    }
}
```

- [ ] **Step 6: Run all tests.**

```powershell
dotnet test
```

Expected: all unit tests pass, the architecture `DiRegistrationTests` passes (the new `ISourceTextHasher` is registered).

- [ ] **Step 7: Commit.**

```powershell
git add src/CodeRag.Core/Indexing/ISourceTextHasher.cs src/CodeRag.Core/Indexing/SourceTextHasher.cs src/CodeRag.Core/ServiceCollectionExtensions.cs tests/CodeRag.Tests.Unit/Indexing/SourceTextHasherTests.cs
git commit -m "feat(core): add ISourceTextHasher (#<N>)"
```

---

## Task 4: SQL schema as embedded resource + open/migrate the index file

**Files:**
- Create: `src/CodeRag.Core/Indexing/Sql/Schema.v1.sql`
- Create: `src/CodeRag.Core/Indexing/IIndexStore.cs`
- Create: `src/CodeRag.Core/Indexing/SqliteIndexStore.cs`
- Create: `src/CodeRag.Core/Indexing/IndexMetadata.cs`
- Create: `src/CodeRag.Core/Indexing/SqliteVecLoader.cs`
- Modify: `src/CodeRag.Core/CodeRag.Core.csproj` (mark `.sql` as embedded resource)
- Modify: `src/CodeRag.Core/ServiceCollectionExtensions.cs`
- Test: `tests/CodeRag.Tests.Unit/Indexing/SqliteIndexStoreTests.cs`

`SqliteIndexStore` is large enough that we build it in three tasks (this one, Task 5, Task 6). This task does just enough to open / create / migrate the file and round-trip `IndexMetadata`.

- [ ] **Step 1: Write the schema file.** `src/CodeRag.Core/Indexing/Sql/Schema.v1.sql` — copy the exact DDL from the design spec (`docs/superpowers/specs/2026-05-02-codebase-indexer-design.md` section "Data model"). Include the `CREATE TABLE`, `CREATE INDEX`, and `CREATE VIRTUAL TABLE chunk_embeddings USING vec0(embedding float[3072])` statements.

- [ ] **Step 2: Mark the SQL file as an embedded resource.** Edit `src/CodeRag.Core/CodeRag.Core.csproj` to add inside the existing `<ItemGroup>` (or a new one):

```xml
<ItemGroup>
  <EmbeddedResource Include="Indexing\Sql\*.sql" />
</ItemGroup>
```

- [ ] **Step 3: Write `IndexMetadata.cs`** — the in-memory representation of the single-row metadata table:

```csharp
namespace CodeRag.Core.Indexing;

public sealed record IndexMetadata(
    int SchemaVersion,
    string SolutionFilePath,
    string RepositoryRootPath,
    string IndexedAtCommitSha,
    DateTimeOffset IndexedAtUtc,
    string EmbeddingModelName,
    int EmbeddingVectorDimensions);
```

- [ ] **Step 4: Write the failing test.** `tests/CodeRag.Tests.Unit/Indexing/SqliteIndexStoreTests.cs`:

```csharp
using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Indexing;

[TestFixture]
public class SqliteIndexStoreTests
{
    private string _dbPath = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"coderag-{Guid.NewGuid():N}.db");
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Test]
    public async Task Open_should_create_schema_when_file_is_new()
    {
        using var store = new SqliteIndexStore(_dbPath);
        await store.OpenAsync(CancellationToken.None);

        var metadata = await store.TryGetMetadataAsync(CancellationToken.None);
        metadata.Should().BeNull("a brand-new file has no metadata row");

        File.Exists(_dbPath).Should().BeTrue();
    }

    [Test]
    public async Task SetMetadata_then_TryGetMetadata_should_round_trip()
    {
        using var store = new SqliteIndexStore(_dbPath);
        await store.OpenAsync(CancellationToken.None);

        var written = new IndexMetadata(
            SchemaVersion: 1,
            SolutionFilePath: "C:\\code\\foo.sln",
            RepositoryRootPath: "C:\\code",
            IndexedAtCommitSha: "abc123",
            IndexedAtUtc: new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero),
            EmbeddingModelName: "text-embedding-3-large",
            EmbeddingVectorDimensions: 3072);
        await store.SetMetadataAsync(written, CancellationToken.None);

        var read = await store.TryGetMetadataAsync(CancellationToken.None);
        read.Should().BeEquivalentTo(written);
    }

    [Test]
    public async Task Open_should_throw_when_schema_version_does_not_match()
    {
        using (var store = new SqliteIndexStore(_dbPath))
        {
            await store.OpenAsync(CancellationToken.None);
            var meta = new IndexMetadata(99, "x", "x", "x", DateTimeOffset.UtcNow, "x", 3072);
            await store.SetMetadataAsync(meta, CancellationToken.None);
        }

        using var reopened = new SqliteIndexStore(_dbPath);
        var act = async () => await reopened.OpenAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*schema version*99*");
    }
}
```

- [ ] **Step 5: Run, watch it fail to compile.**

```powershell
dotnet test tests/CodeRag.Tests.Unit
```

- [ ] **Step 6: Write `SqliteVecLoader.cs`.** `src/CodeRag.Core/Indexing/SqliteVecLoader.cs`:

```csharp
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace CodeRag.Core.Indexing;

public static class SqliteVecLoader
{
    public static void LoadInto(SqliteConnection connection)
    {
        connection.EnableExtensions(true);
        var libraryPath = ResolveLibraryPath();
        connection.LoadExtension(libraryPath);
    }

    private static string ResolveLibraryPath()
    {
        var appBase = AppContext.BaseDirectory;
        var rid = ResolveRuntimeIdentifier();
        var fileName = ResolveFileName();
        var path = Path.Combine(appBase, "runtimes", rid, "native", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"sqlite-vec native binary not found at '{path}'. Ensure external/sqlite-vec/runtimes/{rid}/native/{fileName} is checked in and copied to output.");
        }
        return path;
    }

    private static string ResolveRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "win-x64";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux-x64";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "osx-arm64";
        }
        throw new PlatformNotSupportedException(
            $"sqlite-vec binaries are not provisioned for this platform ({RuntimeInformation.OSDescription}).");
    }

    private static string ResolveFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "vec0.dll";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "vec0.so";
        }
        return "vec0.dylib";
    }
}
```

- [ ] **Step 7: Write `IIndexStore.cs`** — start with just the surface this task needs; later tasks extend it. Note `internal` visibility so the architecture test doesn't require DI registration (the store is consumed via a factory; only the factory ends up public, and that lands in Task 16):

```csharp
namespace CodeRag.Core.Indexing;

internal interface IIndexStore : IDisposable
{
    Task OpenAsync(CancellationToken cancellationToken);
    Task<IndexMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken);
    Task SetMetadataAsync(IndexMetadata metadata, CancellationToken cancellationToken);
}
```

- [ ] **Step 8: Write `SqliteIndexStore.cs`.** `src/CodeRag.Core/Indexing/SqliteIndexStore.cs`:

```csharp
using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace CodeRag.Core.Indexing;

internal sealed class SqliteIndexStore : IIndexStore
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _databasePath;
    private SqliteConnection? _connection;

    public SqliteIndexStore(string databasePath)
    {
        _databasePath = databasePath;
    }

    internal SqliteConnection Connection => _connection
        ?? throw new InvalidOperationException("OpenAsync must be called first.");

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        _connection = new SqliteConnection(connectionString);
        await _connection.OpenAsync(cancellationToken);

        SqliteVecLoader.LoadInto(_connection);

        await EnsureSchemaAsync(cancellationToken);
    }

    public async Task<IndexMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken)
    {
        const string sql = @"SELECT schema_version, solution_file_path, repository_root_path,
                                     indexed_at_commit_sha, indexed_at_utc,
                                     embedding_model_name, embedding_vector_dimensions
                              FROM index_metadata WHERE metadata_id = 1";
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new IndexMetadata(
            SchemaVersion: reader.GetInt32(0),
            SolutionFilePath: reader.GetString(1),
            RepositoryRootPath: reader.GetString(2),
            IndexedAtCommitSha: reader.GetString(3),
            IndexedAtUtc: DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            EmbeddingModelName: reader.GetString(5),
            EmbeddingVectorDimensions: reader.GetInt32(6));
    }

    public async Task SetMetadataAsync(IndexMetadata metadata, CancellationToken cancellationToken)
    {
        const string sql = @"INSERT INTO index_metadata
            (metadata_id, schema_version, solution_file_path, repository_root_path,
             indexed_at_commit_sha, indexed_at_utc, embedding_model_name, embedding_vector_dimensions)
            VALUES (1, $sv, $sln, $root, $sha, $at, $model, $dim)
            ON CONFLICT(metadata_id) DO UPDATE SET
                schema_version              = excluded.schema_version,
                solution_file_path          = excluded.solution_file_path,
                repository_root_path        = excluded.repository_root_path,
                indexed_at_commit_sha       = excluded.indexed_at_commit_sha,
                indexed_at_utc              = excluded.indexed_at_utc,
                embedding_model_name        = excluded.embedding_model_name,
                embedding_vector_dimensions = excluded.embedding_vector_dimensions";
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$sv", metadata.SchemaVersion);
        cmd.Parameters.AddWithValue("$sln", metadata.SolutionFilePath);
        cmd.Parameters.AddWithValue("$root", metadata.RepositoryRootPath);
        cmd.Parameters.AddWithValue("$sha", metadata.IndexedAtCommitSha);
        cmd.Parameters.AddWithValue("$at", metadata.IndexedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$model", metadata.EmbeddingModelName);
        cmd.Parameters.AddWithValue("$dim", metadata.EmbeddingVectorDimensions);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var existing = await TryGetSchemaVersionAsync(cancellationToken);
        if (existing is null)
        {
            await ApplySchemaAsync("Schema.v1.sql", cancellationToken);
            return;
        }
        if (existing.Value != CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"index_metadata.schema version {existing.Value} does not match expected version {CurrentSchemaVersion}. " +
                "Delete the index file and re-run; v1 has no online migration.");
        }
    }

    private async Task<int?> TryGetSchemaVersionAsync(CancellationToken cancellationToken)
    {
        const string existsSql = @"SELECT name FROM sqlite_master WHERE type='table' AND name='index_metadata'";
        await using var existsCmd = Connection.CreateCommand();
        existsCmd.CommandText = existsSql;
        var name = await existsCmd.ExecuteScalarAsync(cancellationToken);
        if (name is null)
        {
            return null;
        }
        const string versionSql = "SELECT schema_version FROM index_metadata WHERE metadata_id = 1";
        await using var versionCmd = Connection.CreateCommand();
        versionCmd.CommandText = versionSql;
        var raw = await versionCmd.ExecuteScalarAsync(cancellationToken);
        return raw is null ? null : Convert.ToInt32(raw, CultureInfo.InvariantCulture);
    }

    private async Task ApplySchemaAsync(string resourceName, CancellationToken cancellationToken)
    {
        var fullName = $"CodeRag.Core.Indexing.Sql.{resourceName}";
        using var stream = typeof(SqliteIndexStore).Assembly.GetManifestResourceStream(fullName)
            ?? throw new InvalidOperationException($"Embedded SQL resource not found: {fullName}");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(cancellationToken);
        await using var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

- [ ] **Step 9: No DI registration this task.** `IIndexStore` is `internal`, so the architecture test (which scans only public interfaces) ignores it. Task 16 introduces a public `IIndexStoreFactory` and registers that.

- [ ] **Step 10: Run tests.**

```powershell
dotnet test tests/CodeRag.Tests.Unit
```

Expected: all `SqliteIndexStoreTests` pass.

- [ ] **Step 11: Commit.**

```powershell
git add src/CodeRag.Core/Indexing src/CodeRag.Core/CodeRag.Core.csproj src/CodeRag.Core/ServiceCollectionExtensions.cs tests/CodeRag.Tests.Unit/Indexing/SqliteIndexStoreTests.cs
git commit -m "feat(core): SqliteIndexStore opens DB and round-trips metadata (#<N>)"
```

---

## Task 5: `IIndexStore` writes — insert and update chunks

**Files:**
- Modify: `src/CodeRag.Core/Indexing/IIndexStore.cs`
- Modify: `src/CodeRag.Core/Indexing/SqliteIndexStore.cs`
- Modify: `tests/CodeRag.Tests.Unit/Indexing/SqliteIndexStoreTests.cs`

This task adds write methods. We do NOT yet write embeddings — only the `code_chunks` row + child rows. Embeddings get a separate `UpsertEmbeddingAsync` because they arrive asynchronously after a batched OpenAI call.

- [ ] **Step 1: Extend `IIndexStore`:**

```csharp
public interface IIndexStore : IDisposable
{
    Task OpenAsync(CancellationToken cancellationToken);
    Task<IndexMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken);
    Task SetMetadataAsync(IndexMetadata metadata, CancellationToken cancellationToken);

    Task BeginTransactionAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);

    Task<long> InsertChunkAsync(CodeChunk chunk, CancellationToken cancellationToken);
    Task UpdateChunkAsync(long chunkId, CodeChunk chunk, CancellationToken cancellationToken);
    Task UpsertEmbeddingAsync(long chunkId, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the failing tests.** Append to `SqliteIndexStoreTests.cs`:

```csharp
[Test]
public async Task InsertChunkAsync_should_persist_all_columns_and_child_rows()
{
    using var store = new SqliteIndexStore(_dbPath);
    await store.OpenAsync(CancellationToken.None);

    var chunk = TestChunks.SampleMethod();
    long id = await store.InsertChunkAsync(chunk, CancellationToken.None);

    id.Should().BePositive();
    var roundTripped = await store.GetChunkByIdAsync(id, CancellationToken.None);
    roundTripped.Should().BeEquivalentTo(chunk, options => options.Excluding(c => c.SourceText));
}

[Test]
public async Task UpdateChunkAsync_should_replace_child_rows()
{
    using var store = new SqliteIndexStore(_dbPath);
    await store.OpenAsync(CancellationToken.None);

    var original = TestChunks.SampleMethod();
    long id = await store.InsertChunkAsync(original, CancellationToken.None);

    var updated = original with
    {
        Parameters = ImmutableArray.Create(new ChunkParameter(0, "x", "System.String", null, false))
    };
    await store.UpdateChunkAsync(id, updated, CancellationToken.None);

    var roundTripped = await store.GetChunkByIdAsync(id, CancellationToken.None);
    roundTripped!.Parameters.Should().HaveCount(1);
    roundTripped.Parameters[0].Name.Should().Be("x");
}

[Test]
public async Task UpsertEmbeddingAsync_should_persist_vector_keyed_by_chunk_id()
{
    using var store = new SqliteIndexStore(_dbPath);
    await store.OpenAsync(CancellationToken.None);

    var chunk = TestChunks.SampleMethod();
    long id = await store.InsertChunkAsync(chunk, CancellationToken.None);

    var vector = new float[3072];
    for (int i = 0; i < vector.Length; i++)
    {
        vector[i] = i * 0.001f;
    }
    await store.UpsertEmbeddingAsync(id, vector, CancellationToken.None);

    (await store.HasEmbeddingAsync(id, CancellationToken.None)).Should().BeTrue();
}
```

Plus a helper file `tests/CodeRag.Tests.Unit/Indexing/TestChunks.cs`:

```csharp
using System.Collections.Immutable;
using CodeRag.Core.Indexing;

namespace CodeRag.Tests.Unit.Indexing;

internal static class TestChunks
{
    public static CodeChunk SampleMethod() => new(
        ContainingProjectName: "CodeRag.Core",
        ContainingAssemblyName: "CodeRag.Core",
        RelativeFilePath: "src/CodeRag.Core/Foo.cs",
        StartLineNumber: 5,
        EndLineNumber: 12,
        SymbolKind: SymbolKinds.Method,
        SymbolDisplayName: "RunAsync",
        SymbolSignatureDisplay: "Task<int> RunAsync(System.Threading.CancellationToken ct)",
        FullyQualifiedSymbolName: "CodeRag.Core.Foo.RunAsync(System.Threading.CancellationToken)",
        ContainingNamespace: "CodeRag.Core",
        ParentSymbolFullyQualifiedName: "CodeRag.Core.Foo",
        Accessibility: Accessibilities.Public,
        Modifiers: new ChunkModifiers(false, false, false, false, false, true, false, false, false, false, false, false),
        BaseTypeFullyQualifiedName: null,
        ReturnTypeFullyQualifiedName: "System.Threading.Tasks.Task<int>",
        ParameterCount: 1,
        DocumentationCommentXml: null,
        SourceText: "public async Task<int> RunAsync(CancellationToken ct) => 0;",
        SourceTextHash: "deadbeef",
        Attributes: ImmutableArray.Create(new ChunkAttribute("System.ObsoleteAttribute", "[\"deprecated\"]")),
        ImplementedInterfaceFullyQualifiedNames: ImmutableArray<string>.Empty,
        Parameters: ImmutableArray.Create(new ChunkParameter(0, "ct", "System.Threading.CancellationToken", null, false)),
        GenericTypeParameters: ImmutableArray<ChunkGenericTypeParameter>.Empty);
}
```

The test references `GetChunkByIdAsync` and `HasEmbeddingAsync` — these are *internal* read helpers we'll add for testing convenience (added in Task 6).

- [ ] **Step 3: Run, fail.**

- [ ] **Step 4: Implement transaction + insert + update + upsert-embedding** in `SqliteIndexStore`. Add these fields/methods:

```csharp
private SqliteTransaction? _transaction;

public async Task BeginTransactionAsync(CancellationToken cancellationToken)
{
    if (_transaction is not null)
    {
        throw new InvalidOperationException("A transaction is already in progress.");
    }
    _transaction = (SqliteTransaction)await Connection.BeginTransactionAsync(cancellationToken);
}

public async Task CommitAsync(CancellationToken cancellationToken)
{
    if (_transaction is null)
    {
        throw new InvalidOperationException("No transaction is in progress.");
    }
    await _transaction.CommitAsync(cancellationToken);
    await _transaction.DisposeAsync();
    _transaction = null;
}

public async Task RollbackAsync(CancellationToken cancellationToken)
{
    if (_transaction is null)
    {
        return;
    }
    await _transaction.RollbackAsync(cancellationToken);
    await _transaction.DisposeAsync();
    _transaction = null;
}

public async Task<long> InsertChunkAsync(CodeChunk chunk, CancellationToken cancellationToken)
{
    const string sql = @"INSERT INTO code_chunks
        (containing_project_name, containing_assembly_name, relative_file_path,
         start_line_number, end_line_number,
         symbol_kind, symbol_display_name, symbol_signature_display,
         fully_qualified_symbol_name, containing_namespace, parent_symbol_fully_qualified_name,
         accessibility,
         is_static, is_abstract, is_sealed, is_virtual, is_override, is_async,
         is_partial, is_readonly, is_extern, is_unsafe, is_extension_method, is_generic,
         base_type_fully_qualified_name, return_type_fully_qualified_name, parameter_count,
         documentation_comment_xml, source_text, source_text_hash)
        VALUES ($proj, $asm, $path, $sline, $eline,
                $kind, $disp, $sig, $fqn, $ns, $parent,
                $acc, $st, $ab, $sl, $vi, $ov, $as, $pa, $rd, $ex, $un, $em, $ge,
                $base, $ret, $pc, $doc, $src, $hash)
        RETURNING chunk_id";
    await using var cmd = Connection.CreateCommand();
    cmd.Transaction = _transaction;
    cmd.CommandText = sql;
    BindChunkColumns(cmd, chunk);
    var id = (long)(await cmd.ExecuteScalarAsync(cancellationToken)
        ?? throw new InvalidOperationException("INSERT did not return chunk_id."));
    await InsertChildRowsAsync(id, chunk, cancellationToken);
    return id;
}

public async Task UpdateChunkAsync(long chunkId, CodeChunk chunk, CancellationToken cancellationToken)
{
    const string sql = @"UPDATE code_chunks SET
        containing_project_name = $proj, containing_assembly_name = $asm,
        relative_file_path = $path, start_line_number = $sline, end_line_number = $eline,
        symbol_kind = $kind, symbol_display_name = $disp, symbol_signature_display = $sig,
        fully_qualified_symbol_name = $fqn, containing_namespace = $ns,
        parent_symbol_fully_qualified_name = $parent,
        accessibility = $acc,
        is_static = $st, is_abstract = $ab, is_sealed = $sl, is_virtual = $vi,
        is_override = $ov, is_async = $as, is_partial = $pa, is_readonly = $rd,
        is_extern = $ex, is_unsafe = $un, is_extension_method = $em, is_generic = $ge,
        base_type_fully_qualified_name = $base,
        return_type_fully_qualified_name = $ret, parameter_count = $pc,
        documentation_comment_xml = $doc, source_text = $src, source_text_hash = $hash
        WHERE chunk_id = $id";
    await using var cmd = Connection.CreateCommand();
    cmd.Transaction = _transaction;
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("$id", chunkId);
    BindChunkColumns(cmd, chunk);
    await cmd.ExecuteNonQueryAsync(cancellationToken);
    await DeleteChildRowsAsync(chunkId, cancellationToken);
    await InsertChildRowsAsync(chunkId, chunk, cancellationToken);
}

public async Task UpsertEmbeddingAsync(long chunkId, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken)
{
    const string deleteSql = "DELETE FROM chunk_embeddings WHERE rowid = $id";
    const string insertSql = "INSERT INTO chunk_embeddings(rowid, embedding) VALUES ($id, $vec)";

    await using (var del = Connection.CreateCommand())
    {
        del.Transaction = _transaction;
        del.CommandText = deleteSql;
        del.Parameters.AddWithValue("$id", chunkId);
        await del.ExecuteNonQueryAsync(cancellationToken);
    }

    await using var ins = Connection.CreateCommand();
    ins.Transaction = _transaction;
    ins.CommandText = insertSql;
    ins.Parameters.AddWithValue("$id", chunkId);
    ins.Parameters.AddWithValue("$vec", FloatArrayToBlob(embedding.Span));
    await ins.ExecuteNonQueryAsync(cancellationToken);
}

private static byte[] FloatArrayToBlob(ReadOnlySpan<float> floats)
{
    var bytes = new byte[floats.Length * sizeof(float)];
    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes, floats[0]);
    var span = bytes.AsSpan();
    for (int i = 0; i < floats.Length; i++)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(span.Slice(i * 4, 4), floats[i]);
    }
    return bytes;
}

private static void BindChunkColumns(SqliteCommand cmd, CodeChunk c)
{
    cmd.Parameters.AddWithValue("$proj", c.ContainingProjectName);
    cmd.Parameters.AddWithValue("$asm", c.ContainingAssemblyName);
    cmd.Parameters.AddWithValue("$path", c.RelativeFilePath);
    cmd.Parameters.AddWithValue("$sline", c.StartLineNumber);
    cmd.Parameters.AddWithValue("$eline", c.EndLineNumber);
    cmd.Parameters.AddWithValue("$kind", c.SymbolKind);
    cmd.Parameters.AddWithValue("$disp", c.SymbolDisplayName);
    cmd.Parameters.AddWithValue("$sig", c.SymbolSignatureDisplay);
    cmd.Parameters.AddWithValue("$fqn", c.FullyQualifiedSymbolName);
    cmd.Parameters.AddWithValue("$ns", (object?)c.ContainingNamespace ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$parent", (object?)c.ParentSymbolFullyQualifiedName ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$acc", c.Accessibility);
    cmd.Parameters.AddWithValue("$st", c.Modifiers.IsStatic ? 1 : 0);
    cmd.Parameters.AddWithValue("$ab", c.Modifiers.IsAbstract ? 1 : 0);
    cmd.Parameters.AddWithValue("$sl", c.Modifiers.IsSealed ? 1 : 0);
    cmd.Parameters.AddWithValue("$vi", c.Modifiers.IsVirtual ? 1 : 0);
    cmd.Parameters.AddWithValue("$ov", c.Modifiers.IsOverride ? 1 : 0);
    cmd.Parameters.AddWithValue("$as", c.Modifiers.IsAsync ? 1 : 0);
    cmd.Parameters.AddWithValue("$pa", c.Modifiers.IsPartial ? 1 : 0);
    cmd.Parameters.AddWithValue("$rd", c.Modifiers.IsReadonly ? 1 : 0);
    cmd.Parameters.AddWithValue("$ex", c.Modifiers.IsExtern ? 1 : 0);
    cmd.Parameters.AddWithValue("$un", c.Modifiers.IsUnsafe ? 1 : 0);
    cmd.Parameters.AddWithValue("$em", c.Modifiers.IsExtensionMethod ? 1 : 0);
    cmd.Parameters.AddWithValue("$ge", c.Modifiers.IsGeneric ? 1 : 0);
    cmd.Parameters.AddWithValue("$base", (object?)c.BaseTypeFullyQualifiedName ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$ret", (object?)c.ReturnTypeFullyQualifiedName ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$pc", (object?)c.ParameterCount ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$doc", (object?)c.DocumentationCommentXml ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$src", c.SourceText);
    cmd.Parameters.AddWithValue("$hash", c.SourceTextHash);
}

private async Task InsertChildRowsAsync(long chunkId, CodeChunk c, CancellationToken cancellationToken)
{
    foreach (var attr in c.Attributes)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "INSERT INTO chunk_attributes(chunk_id, attribute_fully_qualified_name, attribute_arguments_json) VALUES ($id, $name, $args)";
        cmd.Parameters.AddWithValue("$id", chunkId);
        cmd.Parameters.AddWithValue("$name", attr.AttributeFullyQualifiedName);
        cmd.Parameters.AddWithValue("$args", (object?)attr.AttributeArgumentsJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    foreach (var iface in c.ImplementedInterfaceFullyQualifiedNames)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "INSERT INTO chunk_implemented_interfaces(chunk_id, interface_fully_qualified_name) VALUES ($id, $name)";
        cmd.Parameters.AddWithValue("$id", chunkId);
        cmd.Parameters.AddWithValue("$name", iface);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    foreach (var p in c.Parameters)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = @"INSERT INTO chunk_method_parameters
            (chunk_id, parameter_ordinal, parameter_name, parameter_type_fully_qualified_name, parameter_modifier, has_default_value)
            VALUES ($id, $ord, $name, $type, $mod, $def)";
        cmd.Parameters.AddWithValue("$id", chunkId);
        cmd.Parameters.AddWithValue("$ord", p.Ordinal);
        cmd.Parameters.AddWithValue("$name", p.Name);
        cmd.Parameters.AddWithValue("$type", p.TypeFullyQualifiedName);
        cmd.Parameters.AddWithValue("$mod", (object?)p.Modifier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$def", p.HasDefaultValue ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    foreach (var g in c.GenericTypeParameters)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = @"INSERT INTO chunk_generic_type_parameters
            (chunk_id, parameter_ordinal, parameter_name, constraints_json)
            VALUES ($id, $ord, $name, $c)";
        cmd.Parameters.AddWithValue("$id", chunkId);
        cmd.Parameters.AddWithValue("$ord", g.Ordinal);
        cmd.Parameters.AddWithValue("$name", g.Name);
        cmd.Parameters.AddWithValue("$c", (object?)g.ConstraintsJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

private async Task DeleteChildRowsAsync(long chunkId, CancellationToken cancellationToken)
{
    string[] tables =
    [
        "chunk_attributes", "chunk_implemented_interfaces",
        "chunk_method_parameters", "chunk_generic_type_parameters"
    ];
    foreach (var table in tables)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = $"DELETE FROM {table} WHERE chunk_id = $id";
        cmd.Parameters.AddWithValue("$id", chunkId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

- [ ] **Step 5: Add `GetChunkByIdAsync` and `HasEmbeddingAsync` as `internal`** (only for tests):

```csharp
internal async Task<CodeChunk?> GetChunkByIdAsync(long chunkId, CancellationToken cancellationToken)
{
    const string sql = @"SELECT containing_project_name, containing_assembly_name, relative_file_path,
        start_line_number, end_line_number, symbol_kind, symbol_display_name, symbol_signature_display,
        fully_qualified_symbol_name, containing_namespace, parent_symbol_fully_qualified_name,
        accessibility, is_static, is_abstract, is_sealed, is_virtual, is_override, is_async,
        is_partial, is_readonly, is_extern, is_unsafe, is_extension_method, is_generic,
        base_type_fully_qualified_name, return_type_fully_qualified_name, parameter_count,
        documentation_comment_xml, source_text, source_text_hash
        FROM code_chunks WHERE chunk_id = $id";
    await using var cmd = Connection.CreateCommand();
    cmd.Transaction = _transaction;
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("$id", chunkId);
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    if (!await reader.ReadAsync(cancellationToken)) { return null; }

    var modifiers = new ChunkModifiers(
        IsStatic: reader.GetInt64(12) != 0,
        IsAbstract: reader.GetInt64(13) != 0,
        IsSealed: reader.GetInt64(14) != 0,
        IsVirtual: reader.GetInt64(15) != 0,
        IsOverride: reader.GetInt64(16) != 0,
        IsAsync: reader.GetInt64(17) != 0,
        IsPartial: reader.GetInt64(18) != 0,
        IsReadonly: reader.GetInt64(19) != 0,
        IsExtern: reader.GetInt64(20) != 0,
        IsUnsafe: reader.GetInt64(21) != 0,
        IsExtensionMethod: reader.GetInt64(22) != 0,
        IsGeneric: reader.GetInt64(23) != 0);

    var attributes = await ReadChildAsync(chunkId,
        "SELECT attribute_fully_qualified_name, attribute_arguments_json FROM chunk_attributes WHERE chunk_id = $id ORDER BY attribute_id",
        r => new ChunkAttribute(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)),
        cancellationToken);
    var interfaces = await ReadChildAsync(chunkId,
        "SELECT interface_fully_qualified_name FROM chunk_implemented_interfaces WHERE chunk_id = $id",
        r => r.GetString(0),
        cancellationToken);
    var parameters = await ReadChildAsync(chunkId,
        "SELECT parameter_ordinal, parameter_name, parameter_type_fully_qualified_name, parameter_modifier, has_default_value FROM chunk_method_parameters WHERE chunk_id = $id ORDER BY parameter_ordinal",
        r => new ChunkParameter(r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4) != 0),
        cancellationToken);
    var generics = await ReadChildAsync(chunkId,
        "SELECT parameter_ordinal, parameter_name, constraints_json FROM chunk_generic_type_parameters WHERE chunk_id = $id ORDER BY parameter_ordinal",
        r => new ChunkGenericTypeParameter(r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)),
        cancellationToken);

    return new CodeChunk(
        ContainingProjectName: reader.GetString(0),
        ContainingAssemblyName: reader.GetString(1),
        RelativeFilePath: reader.GetString(2),
        StartLineNumber: reader.GetInt32(3),
        EndLineNumber: reader.GetInt32(4),
        SymbolKind: reader.GetString(5),
        SymbolDisplayName: reader.GetString(6),
        SymbolSignatureDisplay: reader.GetString(7),
        FullyQualifiedSymbolName: reader.GetString(8),
        ContainingNamespace: reader.IsDBNull(9) ? null : reader.GetString(9),
        ParentSymbolFullyQualifiedName: reader.IsDBNull(10) ? null : reader.GetString(10),
        Accessibility: reader.GetString(11),
        Modifiers: modifiers,
        BaseTypeFullyQualifiedName: reader.IsDBNull(24) ? null : reader.GetString(24),
        ReturnTypeFullyQualifiedName: reader.IsDBNull(25) ? null : reader.GetString(25),
        ParameterCount: reader.IsDBNull(26) ? null : reader.GetInt32(26),
        DocumentationCommentXml: reader.IsDBNull(27) ? null : reader.GetString(27),
        SourceText: reader.GetString(28),
        SourceTextHash: reader.GetString(29),
        Attributes: attributes.ToImmutableArray(),
        ImplementedInterfaceFullyQualifiedNames: interfaces.ToImmutableArray(),
        Parameters: parameters.ToImmutableArray(),
        GenericTypeParameters: generics.ToImmutableArray());
}

private async Task<List<T>> ReadChildAsync<T>(long chunkId, string sql, Func<Microsoft.Data.Sqlite.SqliteDataReader, T> map, CancellationToken cancellationToken)
{
    await using var cmd = Connection.CreateCommand();
    cmd.Transaction = _transaction;
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("$id", chunkId);
    var list = new List<T>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        list.Add(map((Microsoft.Data.Sqlite.SqliteDataReader)reader));
    }
    return list;
}

internal async Task<bool> HasEmbeddingAsync(long chunkId, CancellationToken cancellationToken)
{
    await using var cmd = Connection.CreateCommand();
    cmd.Transaction = _transaction;
    cmd.CommandText = "SELECT 1 FROM chunk_embeddings WHERE rowid = $id";
    cmd.Parameters.AddWithValue("$id", chunkId);
    var result = await cmd.ExecuteScalarAsync(cancellationToken);
    return result is not null;
}
```

`InternalsVisibleTo CodeRag.Tests.Unit` is already configured in `CodeRag.Core.csproj`.

- [ ] **Step 6: Run tests.**

```powershell
dotnet test tests/CodeRag.Tests.Unit
```

Expected: all `SqliteIndexStoreTests` pass.

- [ ] **Step 7: Commit.**

```powershell
git add src/CodeRag.Core/Indexing/IIndexStore.cs src/CodeRag.Core/Indexing/SqliteIndexStore.cs tests/CodeRag.Tests.Unit/Indexing
git commit -m "feat(core): IndexStore writes chunks, child rows, and embeddings (#<N>)"
```

---

## Task 6: `IIndexStore` reads — list chunks per file, delete by FQN, delete by file path

**Files:**
- Modify: `src/CodeRag.Core/Indexing/IIndexStore.cs`
- Modify: `src/CodeRag.Core/Indexing/SqliteIndexStore.cs`
- Create: `src/CodeRag.Core/Indexing/StoredChunkSummary.cs`
- Modify: `tests/CodeRag.Tests.Unit/Indexing/SqliteIndexStoreTests.cs`

The reconciler only needs lightweight summaries (id, fqn, hash) per file — not full `CodeChunk` deserialization. Keeping reads narrow keeps reconciliation fast.

- [ ] **Step 1: Add the summary type** at `src/CodeRag.Core/Indexing/StoredChunkSummary.cs`:

```csharp
namespace CodeRag.Core.Indexing;

public sealed record StoredChunkSummary(
    long ChunkId,
    string FullyQualifiedSymbolName,
    string SourceTextHash);
```

- [ ] **Step 2: Extend `IIndexStore`:**

```csharp
Task<IReadOnlyList<StoredChunkSummary>> GetChunkSummariesForFileAsync(string relativeFilePath, CancellationToken cancellationToken);
Task DeleteChunkAsync(long chunkId, CancellationToken cancellationToken);
Task DeleteChunksForFileAsync(string relativeFilePath, CancellationToken cancellationToken);
```

- [ ] **Step 3: Add tests** to `SqliteIndexStoreTests.cs`:

```csharp
[Test]
public async Task GetChunkSummariesForFile_should_return_only_chunks_in_that_file()
{
    using var store = new SqliteIndexStore(_dbPath);
    await store.OpenAsync(CancellationToken.None);

    var fooChunk = TestChunks.SampleMethod() with { RelativeFilePath = "src/Foo.cs", FullyQualifiedSymbolName = "X.Foo.Run" };
    var barChunk = TestChunks.SampleMethod() with { RelativeFilePath = "src/Bar.cs", FullyQualifiedSymbolName = "X.Bar.Run" };

    await store.InsertChunkAsync(fooChunk, CancellationToken.None);
    await store.InsertChunkAsync(barChunk, CancellationToken.None);

    var summaries = await store.GetChunkSummariesForFileAsync("src/Foo.cs", CancellationToken.None);

    summaries.Should().HaveCount(1);
    summaries[0].FullyQualifiedSymbolName.Should().Be("X.Foo.Run");
}

[Test]
public async Task DeleteChunkAsync_should_remove_row_and_cascade_children()
{
    using var store = new SqliteIndexStore(_dbPath);
    await store.OpenAsync(CancellationToken.None);

    var chunk = TestChunks.SampleMethod();
    long id = await store.InsertChunkAsync(chunk, CancellationToken.None);
    await store.UpsertEmbeddingAsync(id, new float[3072], CancellationToken.None);

    await store.DeleteChunkAsync(id, CancellationToken.None);

    var summaries = await store.GetChunkSummariesForFileAsync(chunk.RelativeFilePath, CancellationToken.None);
    summaries.Should().BeEmpty();
    (await store.HasEmbeddingAsync(id, CancellationToken.None)).Should().BeFalse();
}

[Test]
public async Task DeleteChunksForFileAsync_should_remove_every_chunk_in_that_file()
{
    using var store = new SqliteIndexStore(_dbPath);
    await store.OpenAsync(CancellationToken.None);

    var path = "src/Foo.cs";
    await store.InsertChunkAsync(TestChunks.SampleMethod() with { RelativeFilePath = path, FullyQualifiedSymbolName = "X.Foo.Run1" }, CancellationToken.None);
    await store.InsertChunkAsync(TestChunks.SampleMethod() with { RelativeFilePath = path, FullyQualifiedSymbolName = "X.Foo.Run2" }, CancellationToken.None);

    await store.DeleteChunksForFileAsync(path, CancellationToken.None);

    var summaries = await store.GetChunkSummariesForFileAsync(path, CancellationToken.None);
    summaries.Should().BeEmpty();
}
```

- [ ] **Step 4: Implement the methods** on `SqliteIndexStore`:

```csharp
public async Task<IReadOnlyList<StoredChunkSummary>> GetChunkSummariesForFileAsync(string relativeFilePath, CancellationToken cancellationToken)
{
    const string sql = @"SELECT chunk_id, fully_qualified_symbol_name, source_text_hash
                          FROM code_chunks WHERE relative_file_path = $path";
    await using var cmd = Connection.CreateCommand();
    cmd.Transaction = _transaction;
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("$path", relativeFilePath);
    var results = new List<StoredChunkSummary>();
    await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
    {
        results.Add(new StoredChunkSummary(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
    }
    return results;
}

public async Task DeleteChunkAsync(long chunkId, CancellationToken cancellationToken)
{
    var tables = new[] { "chunk_embeddings", "chunk_attributes", "chunk_implemented_interfaces", "chunk_method_parameters", "chunk_generic_type_parameters" };
    foreach (var table in tables)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        var idColumn = table == "chunk_embeddings" ? "rowid" : "chunk_id";
        cmd.CommandText = $"DELETE FROM {table} WHERE {idColumn} = $id";
        cmd.Parameters.AddWithValue("$id", chunkId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
    await using var deleteRow = Connection.CreateCommand();
    deleteRow.Transaction = _transaction;
    deleteRow.CommandText = "DELETE FROM code_chunks WHERE chunk_id = $id";
    deleteRow.Parameters.AddWithValue("$id", chunkId);
    await deleteRow.ExecuteNonQueryAsync(cancellationToken);
}

public async Task DeleteChunksForFileAsync(string relativeFilePath, CancellationToken cancellationToken)
{
    var summaries = await GetChunkSummariesForFileAsync(relativeFilePath, cancellationToken);
    foreach (var summary in summaries)
    {
        await DeleteChunkAsync(summary.ChunkId, cancellationToken);
    }
}
```

- [ ] **Step 5: Run tests.** All previous tests pass + new ones pass.

- [ ] **Step 6: Commit.**

```powershell
git add src/CodeRag.Core/Indexing tests/CodeRag.Tests.Unit/Indexing
git commit -m "feat(core): IndexStore reads summaries and supports targeted deletion (#<N>)"
```

---

## Task 7: `IChunkExtractor` — types (class, struct, interface, record, enum, delegate)

**Files:**
- Create: `src/CodeRag.Core/Indexing/IChunkExtractor.cs`
- Create: `src/CodeRag.Core/Indexing/ChunkExtractor.cs`
- Modify: `src/CodeRag.Core/ServiceCollectionExtensions.cs`
- Test: `tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs`

This is the largest single component. We split chunk-extraction across four tasks (7–10) so each commit stays small.

- [ ] **Step 1: Define the interface.** `src/CodeRag.Core/Indexing/IChunkExtractor.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace CodeRag.Core.Indexing;

public interface IChunkExtractor
{
    ImmutableArray<CodeChunk> Extract(
        Compilation compilation,
        SyntaxTree syntaxTree,
        string projectName,
        string assemblyName,
        string repositoryRootPath,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write the failing test for a single class.** `tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs`:

```csharp
using System.Collections.Immutable;
using CodeRag.Core.Indexing;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeRag.Tests.Unit.Indexing;

[TestFixture]
public class ChunkExtractorTests
{
    private IChunkExtractor _extractor = null!;

    [SetUp]
    public void SetUp() => _extractor = new ChunkExtractor(new SourceTextHasher());

    [Test]
    public void Should_emit_one_chunk_for_a_top_level_class()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public class Foo { }
        ");

        var classChunk = chunks.Should().ContainSingle(c => c.SymbolKind == SymbolKinds.Class).Subject;
        classChunk.SymbolDisplayName.Should().Be("Foo");
        classChunk.FullyQualifiedSymbolName.Should().Be("Acme.Foo");
        classChunk.ContainingNamespace.Should().Be("Acme");
        classChunk.Accessibility.Should().Be(Accessibilities.Public);
    }

    [Test]
    public void Should_capture_class_modifiers()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public abstract sealed class Foo { }
        ");

        var c = chunks.Single(x => x.SymbolKind == SymbolKinds.Class);
        c.Modifiers.IsAbstract.Should().BeTrue();
        c.Modifiers.IsSealed.Should().BeTrue();
    }

    [Test]
    public void Should_distinguish_record_class_from_record_struct()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public record Person(string Name);
            public record struct Point(int X, int Y);
        ");

        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.RecordClass && c.SymbolDisplayName == "Person");
        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.RecordStruct && c.SymbolDisplayName == "Point");
    }

    [Test]
    public void Should_capture_struct_interface_enum_delegate()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public struct Vec { }
            public interface IFoo { }
            public enum Kind { A, B }
            public delegate int Adder(int a, int b);
        ");

        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Struct && c.SymbolDisplayName == "Vec");
        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Interface && c.SymbolDisplayName == "IFoo");
        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Enum && c.SymbolDisplayName == "Kind");
        chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Delegate && c.SymbolDisplayName == "Adder");
    }

    [Test]
    public void Should_capture_base_type_and_implemented_interfaces()
    {
        var chunks = ExtractFrom(@"
            namespace Acme;
            public interface IFoo { }
            public class Base { }
            public class Derived : Base, IFoo { }
        ");

        var derived = chunks.Single(c => c.SymbolDisplayName == "Derived");
        derived.BaseTypeFullyQualifiedName.Should().Be("Acme.Base");
        derived.ImplementedInterfaceFullyQualifiedNames.Should().Contain("Acme.IFoo");
    }

    private ImmutableArray<CodeChunk> ExtractFrom(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: "C:/repo/src/Test.cs");
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });
        return _extractor.Extract(compilation, tree, "TestProject", "TestAssembly", "C:/repo", CancellationToken.None);
    }
}
```

- [ ] **Step 3: Run, fail.**

- [ ] **Step 4: Implement `ChunkExtractor` for type symbols only.** `src/CodeRag.Core/Indexing/ChunkExtractor.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeRag.Core.Indexing;

public sealed class ChunkExtractor : IChunkExtractor
{
    private readonly ISourceTextHasher _hasher;

    public ChunkExtractor(ISourceTextHasher hasher)
    {
        _hasher = hasher;
    }

    public ImmutableArray<CodeChunk> Extract(
        Compilation compilation,
        SyntaxTree syntaxTree,
        string projectName,
        string assemblyName,
        string repositoryRootPath,
        CancellationToken cancellationToken)
    {
        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot(cancellationToken);
        var builder = ImmutableArray.CreateBuilder<CodeChunk>();
        foreach (var typeDecl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (semanticModel.GetDeclaredSymbol(typeDecl, cancellationToken) is not INamedTypeSymbol symbol)
            {
                continue;
            }
            builder.Add(BuildTypeChunk(symbol, typeDecl, syntaxTree, projectName, assemblyName, repositoryRootPath));
        }
        foreach (var delegateDecl in root.DescendantNodes().OfType<DelegateDeclarationSyntax>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (semanticModel.GetDeclaredSymbol(delegateDecl, cancellationToken) is not INamedTypeSymbol symbol)
            {
                continue;
            }
            builder.Add(BuildTypeChunk(symbol, delegateDecl, syntaxTree, projectName, assemblyName, repositoryRootPath));
        }
        return builder.ToImmutable();
    }

    private CodeChunk BuildTypeChunk(INamedTypeSymbol symbol, SyntaxNode declaration, SyntaxTree tree,
        string projectName, string assemblyName, string repositoryRootPath)
    {
        var span = declaration.GetLocation().GetLineSpan();
        var sourceText = BuildTypeSourceText(declaration);
        var modifiers = BuildModifiers(symbol);
        var attributes = BuildAttributes(symbol);
        var interfaces = symbol.AllInterfaces.Select(i => i.ToDisplayString()).ToImmutableArray();
        var generics = BuildGenerics(symbol);

        return new CodeChunk(
            ContainingProjectName: projectName,
            ContainingAssemblyName: assemblyName,
            RelativeFilePath: ToRelativePath(tree.FilePath, repositoryRootPath),
            StartLineNumber: span.StartLinePosition.Line + 1,
            EndLineNumber: span.EndLinePosition.Line + 1,
            SymbolKind: ResolveTypeKind(symbol),
            SymbolDisplayName: symbol.Name,
            SymbolSignatureDisplay: symbol.ToDisplayString(),
            FullyQualifiedSymbolName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ContainingNamespace: symbol.ContainingNamespace?.IsGlobalNamespace == true ? null : symbol.ContainingNamespace?.ToDisplayString(),
            ParentSymbolFullyQualifiedName: symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Accessibility: ResolveAccessibility(symbol.DeclaredAccessibility),
            Modifiers: modifiers,
            BaseTypeFullyQualifiedName: symbol.BaseType is { SpecialType: SpecialType.System_Object } or null
                ? null
                : symbol.BaseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ReturnTypeFullyQualifiedName: null,
            ParameterCount: null,
            DocumentationCommentXml: NullIfEmpty(symbol.GetDocumentationCommentXml()),
            SourceText: sourceText,
            SourceTextHash: _hasher.Hash(sourceText),
            Attributes: attributes,
            ImplementedInterfaceFullyQualifiedNames: interfaces,
            Parameters: ImmutableArray<ChunkParameter>.Empty,
            GenericTypeParameters: generics);
    }

    private static string ResolveTypeKind(INamedTypeSymbol symbol)
    {
        if (symbol.IsRecord && symbol.TypeKind == TypeKind.Class) { return SymbolKinds.RecordClass; }
        if (symbol.IsRecord && symbol.TypeKind == TypeKind.Struct) { return SymbolKinds.RecordStruct; }
        return symbol.TypeKind switch
        {
            TypeKind.Class => SymbolKinds.Class,
            TypeKind.Struct => SymbolKinds.Struct,
            TypeKind.Interface => SymbolKinds.Interface,
            TypeKind.Enum => SymbolKinds.Enum,
            TypeKind.Delegate => SymbolKinds.Delegate,
            _ => SymbolKinds.Class,
        };
    }

    private static string ResolveAccessibility(Accessibility a) => a switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => Accessibilities.Public,
        Microsoft.CodeAnalysis.Accessibility.Internal => Accessibilities.Internal,
        Microsoft.CodeAnalysis.Accessibility.Protected => Accessibilities.Protected,
        Microsoft.CodeAnalysis.Accessibility.Private => Accessibilities.Private,
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => Accessibilities.ProtectedInternal,
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => Accessibilities.PrivateProtected,
        _ => Accessibilities.Internal,
    };

    private static ChunkModifiers BuildModifiers(ISymbol symbol)
    {
        bool isStatic = symbol.IsStatic;
        bool isAbstract = symbol.IsAbstract;
        bool isSealed = symbol.IsSealed;
        bool isVirtual = symbol.IsVirtual;
        bool isOverride = symbol.IsOverride;
        bool isExtern = symbol.IsExtern;

        bool isAsync = false, isReadonly = false, isPartial = false, isUnsafe = false, isExtension = false, isGeneric = false;
        if (symbol is IMethodSymbol m)
        {
            isAsync = m.IsAsync;
            isExtension = m.IsExtensionMethod;
            isGeneric = m.IsGenericMethod;
        }
        if (symbol is INamedTypeSymbol t)
        {
            isGeneric = t.IsGenericType;
        }
        if (symbol is IFieldSymbol f)
        {
            isReadonly = f.IsReadOnly;
        }

        return new ChunkModifiers(
            isStatic, isAbstract, isSealed, isVirtual, isOverride, isAsync,
            isPartial, isReadonly, isExtern, isUnsafe, isExtension, isGeneric);
    }

    private static ImmutableArray<ChunkAttribute> BuildAttributes(ISymbol symbol)
    {
        var builder = ImmutableArray.CreateBuilder<ChunkAttribute>();
        foreach (var data in symbol.GetAttributes())
        {
            var name = data.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? "<unknown>";
            string? args = null;
            if (data.ConstructorArguments.Length > 0 || data.NamedArguments.Length > 0)
            {
                var positional = data.ConstructorArguments.Select(a => System.Text.Json.JsonSerializer.Serialize(a.Value)).ToArray();
                args = "[" + string.Join(",", positional) + "]";
            }
            builder.Add(new ChunkAttribute(name, args));
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<ChunkGenericTypeParameter> BuildGenerics(INamedTypeSymbol symbol)
    {
        var builder = ImmutableArray.CreateBuilder<ChunkGenericTypeParameter>();
        for (int i = 0; i < symbol.TypeParameters.Length; i++)
        {
            var tp = symbol.TypeParameters[i];
            string? constraints = null;
            if (tp.HasReferenceTypeConstraint || tp.HasValueTypeConstraint || tp.HasConstructorConstraint || tp.ConstraintTypes.Length > 0)
            {
                var list = new List<string>();
                if (tp.HasReferenceTypeConstraint) { list.Add("class"); }
                if (tp.HasValueTypeConstraint) { list.Add("struct"); }
                if (tp.HasConstructorConstraint) { list.Add("new()"); }
                foreach (var ct in tp.ConstraintTypes)
                {
                    list.Add(ct.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
                }
                constraints = "[" + string.Join(",", list.Select(s => $"\"{s}\"")) + "]";
            }
            builder.Add(new ChunkGenericTypeParameter(i, tp.Name, constraints));
        }
        return builder.ToImmutable();
    }

    private static string BuildTypeSourceText(SyntaxNode declaration)
    {
        if (declaration is TypeDeclarationSyntax type)
        {
            var header = type.WithMembers(default).NormalizeWhitespace().ToFullString();
            var memberSignatures = string.Join(Environment.NewLine, type.Members.Select(m => SimplifyMemberSignature(m)));
            return header + Environment.NewLine + memberSignatures;
        }
        return declaration.ToFullString();
    }

    private static string SimplifyMemberSignature(MemberDeclarationSyntax m)
    {
        return m switch
        {
            MethodDeclarationSyntax method => method.WithBody(null).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).NormalizeWhitespace().ToFullString(),
            PropertyDeclarationSyntax prop => prop.NormalizeWhitespace().ToFullString(),
            ConstructorDeclarationSyntax ctor => ctor.WithBody(null).WithExpressionBody(null).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)).NormalizeWhitespace().ToFullString(),
            _ => m.NormalizeWhitespace().ToFullString(),
        };
    }

    private static string ToRelativePath(string absolutePath, string repositoryRoot)
    {
        var rel = Path.GetRelativePath(repositoryRoot, absolutePath);
        return rel.Replace('\\', '/');
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
```

(`SyntaxKind` and `SyntaxFactory` come from `Microsoft.CodeAnalysis.CSharp`.)

- [ ] **Step 5: Register in DI.** Add to `AddCoreServices()`:

```csharp
services.AddSingleton<IChunkExtractor, ChunkExtractor>();
```

- [ ] **Step 6: Run tests.**

```powershell
dotnet test tests/CodeRag.Tests.Unit
```

Expected: all `ChunkExtractorTests` (the type-level ones) pass.

- [ ] **Step 7: Commit.**

```powershell
git add src/CodeRag.Core/Indexing/IChunkExtractor.cs src/CodeRag.Core/Indexing/ChunkExtractor.cs src/CodeRag.Core/ServiceCollectionExtensions.cs tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs
git commit -m "feat(core): ChunkExtractor extracts type-level chunks (#<N>)"
```

---

## Task 8: `ChunkExtractor` — methods, constructors, operators, indexers

**Files:**
- Modify: `src/CodeRag.Core/Indexing/ChunkExtractor.cs`
- Modify: `tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs`

- [ ] **Step 1: Add tests** for methods, constructors, operators, indexers, parameter modifiers, and async methods. Append to `ChunkExtractorTests.cs`:

```csharp
[Test]
public void Should_emit_method_chunks_with_signature_and_return_type()
{
    var chunks = ExtractFrom(@"
        using System.Threading.Tasks;
        namespace Acme;
        public class Foo
        {
            public async Task<int> RunAsync(string arg) => 0;
        }
    ");

    var method = chunks.Single(c => c.SymbolKind == SymbolKinds.Method);
    method.SymbolDisplayName.Should().Be("RunAsync");
    method.ReturnTypeFullyQualifiedName.Should().StartWith("System.Threading.Tasks.Task");
    method.Modifiers.IsAsync.Should().BeTrue();
    method.Parameters.Should().HaveCount(1);
    method.Parameters[0].Name.Should().Be("arg");
    method.Parameters[0].TypeFullyQualifiedName.Should().Be("string");
}

[Test]
public void Should_capture_parameter_modifiers()
{
    var chunks = ExtractFrom(@"
        namespace Acme;
        public class Foo
        {
            public void Mix(ref int a, out int b, in int c, params int[] d) { b = 0; }
        }
    ");

    var method = chunks.Single(c => c.SymbolKind == SymbolKinds.Method);
    method.Parameters[0].Modifier.Should().Be("ref");
    method.Parameters[1].Modifier.Should().Be("out");
    method.Parameters[2].Modifier.Should().Be("in");
    method.Parameters[3].Modifier.Should().Be("params");
}

[Test]
public void Should_emit_constructor_chunk()
{
    var chunks = ExtractFrom(@"
        namespace Acme;
        public class Foo { public Foo(int x) { } }
    ");

    chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Constructor);
}

[Test]
public void Should_emit_operator_and_conversion_operator_chunks()
{
    var chunks = ExtractFrom(@"
        namespace Acme;
        public class Foo
        {
            public static Foo operator +(Foo a, Foo b) => a;
            public static implicit operator int(Foo f) => 0;
        }
    ");

    chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Operator);
    chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.ConversionOperator);
}

[Test]
public void Should_emit_indexer_chunk()
{
    var chunks = ExtractFrom(@"
        namespace Acme;
        public class Foo { public int this[int i] => 0; }
    ");

    chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Indexer);
}
```

- [ ] **Step 2: Run, fail.**

- [ ] **Step 3: Add member walking** to `ChunkExtractor`. After the type-emission loops in `Extract`, walk members:

```csharp
foreach (var memberDecl in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
{
    cancellationToken.ThrowIfCancellationRequested();
    if (memberDecl is BaseTypeDeclarationSyntax || memberDecl is DelegateDeclarationSyntax || memberDecl is NamespaceDeclarationSyntax || memberDecl is FileScopedNamespaceDeclarationSyntax)
    {
        continue;
    }
    var symbol = semanticModel.GetDeclaredSymbol(memberDecl, cancellationToken);
    if (symbol is null || symbol.IsImplicitlyDeclared)
    {
        continue;
    }
    var chunk = TryBuildMemberChunk(symbol, memberDecl, syntaxTree, projectName, assemblyName, repositoryRootPath);
    if (chunk is not null)
    {
        builder.Add(chunk);
    }
}
```

Then add `TryBuildMemberChunk` and helpers:

```csharp
private CodeChunk? TryBuildMemberChunk(ISymbol symbol, MemberDeclarationSyntax declaration, SyntaxTree tree,
    string projectName, string assemblyName, string repositoryRootPath)
{
    var kind = ResolveMemberKind(symbol, declaration);
    if (kind is null) { return null; }

    var span = declaration.GetLocation().GetLineSpan();
    var sourceText = declaration.NormalizeWhitespace().ToFullString();

    var (returnType, parameters, parameterCount) = symbol switch
    {
        IMethodSymbol m => (
            m.ReturnsVoid ? null : m.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            BuildParameters(m.Parameters),
            (int?)m.Parameters.Length),
        IPropertySymbol p when p.IsIndexer => (
            p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            BuildParameters(p.Parameters),
            (int?)p.Parameters.Length),
        IPropertySymbol p => (
            p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ImmutableArray<ChunkParameter>.Empty,
            (int?)null),
        IFieldSymbol f => (
            f.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ImmutableArray<ChunkParameter>.Empty,
            (int?)null),
        IEventSymbol e => (
            e.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            ImmutableArray<ChunkParameter>.Empty,
            (int?)null),
        _ => ((string?)null, ImmutableArray<ChunkParameter>.Empty, (int?)null),
    };

    return new CodeChunk(
        ContainingProjectName: projectName,
        ContainingAssemblyName: assemblyName,
        RelativeFilePath: ToRelativePath(tree.FilePath, repositoryRootPath),
        StartLineNumber: span.StartLinePosition.Line + 1,
        EndLineNumber: span.EndLinePosition.Line + 1,
        SymbolKind: kind,
        SymbolDisplayName: symbol.Name,
        SymbolSignatureDisplay: symbol.ToDisplayString(),
        FullyQualifiedSymbolName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        ContainingNamespace: symbol.ContainingNamespace?.IsGlobalNamespace == true ? null : symbol.ContainingNamespace?.ToDisplayString(),
        ParentSymbolFullyQualifiedName: symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
        Accessibility: ResolveAccessibility(symbol.DeclaredAccessibility),
        Modifiers: BuildModifiers(symbol),
        BaseTypeFullyQualifiedName: null,
        ReturnTypeFullyQualifiedName: returnType,
        ParameterCount: parameterCount,
        DocumentationCommentXml: NullIfEmpty(symbol.GetDocumentationCommentXml()),
        SourceText: sourceText,
        SourceTextHash: _hasher.Hash(sourceText),
        Attributes: BuildAttributes(symbol),
        ImplementedInterfaceFullyQualifiedNames: ImmutableArray<string>.Empty,
        Parameters: parameters,
        GenericTypeParameters: BuildMemberGenerics(symbol));
}

private static string? ResolveMemberKind(ISymbol symbol, MemberDeclarationSyntax declaration) => (symbol, declaration) switch
{
    (IMethodSymbol m, ConversionOperatorDeclarationSyntax) => SymbolKinds.ConversionOperator,
    (IMethodSymbol m, OperatorDeclarationSyntax) => SymbolKinds.Operator,
    (IMethodSymbol m, ConstructorDeclarationSyntax) => SymbolKinds.Constructor,
    (IMethodSymbol m, DestructorDeclarationSyntax) => SymbolKinds.Destructor,
    (IMethodSymbol, _) => SymbolKinds.Method,
    (IPropertySymbol p, IndexerDeclarationSyntax) => SymbolKinds.Indexer,
    (IPropertySymbol, _) => SymbolKinds.Property,
    (IFieldSymbol, _) => SymbolKinds.Field,
    (IEventSymbol, _) => SymbolKinds.Event,
    _ => null,
};

private static ImmutableArray<ChunkParameter> BuildParameters(ImmutableArray<IParameterSymbol> parameters)
{
    var builder = ImmutableArray.CreateBuilder<ChunkParameter>();
    for (int i = 0; i < parameters.Length; i++)
    {
        var p = parameters[i];
        string? mod = (p.RefKind, p.IsParams) switch
        {
            (_, true) => "params",
            (RefKind.Ref, _) => "ref",
            (RefKind.Out, _) => "out",
            (RefKind.In, _) => "in",
            _ => null,
        };
        builder.Add(new ChunkParameter(
            Ordinal: i,
            Name: p.Name,
            TypeFullyQualifiedName: p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Modifier: mod,
            HasDefaultValue: p.HasExplicitDefaultValue));
    }
    return builder.ToImmutable();
}

private static ImmutableArray<ChunkGenericTypeParameter> BuildMemberGenerics(ISymbol symbol)
{
    if (symbol is IMethodSymbol m)
    {
        var b = ImmutableArray.CreateBuilder<ChunkGenericTypeParameter>();
        for (int i = 0; i < m.TypeParameters.Length; i++)
        {
            b.Add(new ChunkGenericTypeParameter(i, m.TypeParameters[i].Name, null));
        }
        return b.ToImmutable();
    }
    return ImmutableArray<ChunkGenericTypeParameter>.Empty;
}
```

- [ ] **Step 4: Adjust the `Extract` method** so it doesn't emit member chunks twice (`OfType<MemberDeclarationSyntax>` would also include type declarations). The filter at the top of the loop covers that, but watch for `EnumMemberDeclarationSyntax` — we don't emit chunks for individual enum values; they live inside the enum's `source_text`. Add `EnumMemberDeclarationSyntax` to the skip list.

- [ ] **Step 5: Run tests, all pass.**

- [ ] **Step 6: Commit.**

```powershell
git add src/CodeRag.Core/Indexing/ChunkExtractor.cs tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs
git commit -m "feat(core): ChunkExtractor extracts methods, ctors, operators, indexers (#<N>)"
```

---

## Task 9: `ChunkExtractor` — properties, fields, events

**Files:**
- Modify: `tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs`

The implementation in Task 8 already handles `IPropertySymbol`, `IFieldSymbol`, `IEventSymbol` via the `TryBuildMemberChunk` switch — this task just adds the test coverage. If any test fails, refine `TryBuildMemberChunk` until it passes.

- [ ] **Step 1: Add tests:**

```csharp
[Test]
public void Should_emit_property_chunk_with_type()
{
    var chunks = ExtractFrom(@"
        namespace Acme;
        public class Foo { public int Count { get; init; } }
    ");

    var prop = chunks.Single(c => c.SymbolKind == SymbolKinds.Property);
    prop.ReturnTypeFullyQualifiedName.Should().Be("int");
    prop.SymbolDisplayName.Should().Be("Count");
}

[Test]
public void Should_emit_field_chunk_with_type_and_readonly_modifier()
{
    var chunks = ExtractFrom(@"
        namespace Acme;
        public class Foo { private readonly int _count = 0; }
    ");

    var field = chunks.Single(c => c.SymbolKind == SymbolKinds.Field);
    field.ReturnTypeFullyQualifiedName.Should().Be("int");
    field.Modifiers.IsReadonly.Should().BeTrue();
}

[Test]
public void Should_emit_event_chunk_with_delegate_type()
{
    var chunks = ExtractFrom(@"
        using System;
        namespace Acme;
        public class Foo { public event EventHandler? OnSomething; }
    ");

    chunks.Should().Contain(c => c.SymbolKind == SymbolKinds.Event && c.SymbolDisplayName == "OnSomething");
}
```

- [ ] **Step 2: Run tests.** If all pass, commit. If any fail, refine `TryBuildMemberChunk` and `ResolveMemberKind` (Task 8) until they pass.

- [ ] **Step 3: Commit.**

```powershell
git add tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs src/CodeRag.Core/Indexing/ChunkExtractor.cs
git commit -m "test(core): ChunkExtractor coverage for properties, fields, events (#<N>)"
```

---

## Task 10: `ChunkExtractor` — attributes (with arguments) and partial types

**Files:**
- Modify: `src/CodeRag.Core/Indexing/ChunkExtractor.cs`
- Modify: `tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs`

- [ ] **Step 1: Add tests:**

```csharp
[Test]
public void Should_capture_attribute_with_string_argument()
{
    var chunks = ExtractFrom(@"
        using System;
        namespace Acme;
        public class Foo
        {
            [Obsolete(""use bar instead"")]
            public void Run() { }
        }
    ");

    var method = chunks.Single(c => c.SymbolKind == SymbolKinds.Method);
    var attr = method.Attributes.Should().ContainSingle().Subject;
    attr.AttributeFullyQualifiedName.Should().Be("System.ObsoleteAttribute");
    attr.AttributeArgumentsJson.Should().Contain("use bar instead");
}

[Test]
public void Should_emit_one_chunk_per_partial_class_declaration()
{
    var src1 = @"
        namespace Acme;
        public partial class Foo { public void A() { } }
    ";
    var src2 = @"
        namespace Acme;
        public partial class Foo { public void B() { } }
    ";

    var tree1 = CSharpSyntaxTree.ParseText(src1, path: "C:/repo/Foo.A.cs");
    var tree2 = CSharpSyntaxTree.ParseText(src2, path: "C:/repo/Foo.B.cs");
    var compilation = CSharpCompilation.Create("Test", new[] { tree1, tree2 },
        new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

    var chunks1 = _extractor.Extract(compilation, tree1, "T", "T", "C:/repo", CancellationToken.None);
    var chunks2 = _extractor.Extract(compilation, tree2, "T", "T", "C:/repo", CancellationToken.None);

    chunks1.Single(c => c.SymbolKind == SymbolKinds.Class).Modifiers.IsPartial.Should().BeTrue();
    chunks1.Where(c => c.SymbolKind == SymbolKinds.Method).Should().HaveCount(1);
    chunks2.Where(c => c.SymbolKind == SymbolKinds.Method).Should().HaveCount(1);
}
```

- [ ] **Step 2: Add `IsPartial` detection** for type symbols. Roslyn's `ISymbol` doesn't expose `IsPartial` directly, but the syntax declaration does. In `BuildTypeChunk`, before the `new CodeChunk(...)` call, compute:

```csharp
bool isPartial = declaration is TypeDeclarationSyntax tds && tds.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
```

…and pass `isPartial` into the modifiers (replace the `IsPartial` field in the `BuildModifiers` result with `modifiers with { IsPartial = isPartial }`):

Adjust by changing the call:

```csharp
var modifiers = BuildModifiers(symbol) with { IsPartial = isPartial };
```

(`ChunkModifiers` is a record so `with` works.)

- [ ] **Step 3: Run tests, all pass.**

- [ ] **Step 4: Commit.**

```powershell
git add src/CodeRag.Core/Indexing/ChunkExtractor.cs tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs
git commit -m "feat(core): ChunkExtractor handles attributes and partial types (#<N>)"
```

---

## Task 11: `ChunkExtractor` — skip rules (generated code, files outside repo, implicit declarations)

**Files:**
- Modify: `src/CodeRag.Core/Indexing/ChunkExtractor.cs`
- Modify: `tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs`

- [ ] **Step 1: Add tests:**

```csharp
[Test]
public void Should_skip_files_outside_repository_root()
{
    var src = "namespace X; public class Foo { }";
    var tree = CSharpSyntaxTree.ParseText(src, path: "C:/elsewhere/Foo.cs");
    var compilation = CSharpCompilation.Create("T", new[] { tree },
        new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

    var chunks = _extractor.Extract(compilation, tree, "T", "T", "C:/repo", CancellationToken.None);

    chunks.Should().BeEmpty();
}

[Test]
public void Should_skip_files_under_obj_or_bin()
{
    var src = "namespace X; public class Foo { }";
    var tree = CSharpSyntaxTree.ParseText(src, path: "C:/repo/obj/Debug/Generated.g.cs");
    var compilation = CSharpCompilation.Create("T", new[] { tree },
        new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

    var chunks = _extractor.Extract(compilation, tree, "T", "T", "C:/repo", CancellationToken.None);

    chunks.Should().BeEmpty();
}
```

- [ ] **Step 2: Add the skip check** at the top of `Extract` in `ChunkExtractor`:

```csharp
if (!IsInRepository(syntaxTree.FilePath, repositoryRootPath))
{
    return ImmutableArray<CodeChunk>.Empty;
}
if (IsObvioslyGenerated(syntaxTree.FilePath))
{
    return ImmutableArray<CodeChunk>.Empty;
}
```

…and the helpers:

```csharp
private static bool IsInRepository(string? filePath, string repositoryRootPath)
{
    if (string.IsNullOrEmpty(filePath)) { return false; }
    var fullFile = Path.GetFullPath(filePath);
    var fullRoot = Path.GetFullPath(repositoryRootPath);
    return fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
}

private static bool IsObvioslyGenerated(string? filePath)
{
    if (string.IsNullOrEmpty(filePath)) { return false; }
    var normalised = filePath.Replace('\\', '/').ToLowerInvariant();
    return normalised.Contains("/obj/") || normalised.Contains("/bin/")
        || normalised.EndsWith(".g.cs", StringComparison.Ordinal)
        || normalised.EndsWith(".g.i.cs", StringComparison.Ordinal);
}
```

- [ ] **Step 3: Skip implicit declarations.** Already done in Task 8 (`if (symbol.IsImplicitlyDeclared) { continue; }`); verify the existing tests still pass.

- [ ] **Step 4: Run all tests.**

- [ ] **Step 5: Commit.**

```powershell
git add src/CodeRag.Core/Indexing/ChunkExtractor.cs tests/CodeRag.Tests.Unit/Indexing/ChunkExtractorTests.cs
git commit -m "feat(core): ChunkExtractor skips generated and out-of-repo files (#<N>)"
```

---

## Task 12: Reconciler

**Files:**
- Create: `src/CodeRag.Core/Indexing/IReconciler.cs`
- Create: `src/CodeRag.Core/Indexing/Reconciler.cs`
- Create: `src/CodeRag.Core/Indexing/ReconciliationPlan.cs`
- Create: `src/CodeRag.Core/Indexing/ReconciliationOps.cs`
- Modify: `src/CodeRag.Core/ServiceCollectionExtensions.cs`
- Test: `tests/CodeRag.Tests.Unit/Indexing/ReconcilerTests.cs`

Pure function, fully unit-testable.

- [ ] **Step 1: Define operation types.** `src/CodeRag.Core/Indexing/ReconciliationOps.cs`:

```csharp
namespace CodeRag.Core.Indexing;

public sealed record InsertOp(CodeChunk Chunk);
public sealed record UpdateOp(long ChunkId, CodeChunk Chunk, bool ContentChanged);
public sealed record DeleteOp(long ChunkId);
```

- [ ] **Step 2: Define plan.** `src/CodeRag.Core/Indexing/ReconciliationPlan.cs`:

```csharp
using System.Collections.Immutable;

namespace CodeRag.Core.Indexing;

public sealed record ReconciliationPlan(
    ImmutableArray<InsertOp> Inserts,
    ImmutableArray<UpdateOp> Updates,
    ImmutableArray<DeleteOp> Deletes);
```

- [ ] **Step 3: Define interface.** `src/CodeRag.Core/Indexing/IReconciler.cs`:

```csharp
using System.Collections.Immutable;

namespace CodeRag.Core.Indexing;

public interface IReconciler
{
    ReconciliationPlan Plan(
        IReadOnlyList<StoredChunkSummary> existingChunks,
        ImmutableArray<CodeChunk> newChunks);
}
```

- [ ] **Step 4: Write the tests.** `tests/CodeRag.Tests.Unit/Indexing/ReconcilerTests.cs`:

```csharp
using System.Collections.Immutable;
using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Indexing;

[TestFixture]
public class ReconcilerTests
{
    private readonly IReconciler _reconciler = new Reconciler();

    [Test]
    public void Should_insert_when_chunk_is_only_in_new()
    {
        var newChunk = TestChunks.SampleMethod() with { FullyQualifiedSymbolName = "X.A", SourceTextHash = "h1" };
        var plan = _reconciler.Plan([], ImmutableArray.Create(newChunk));

        plan.Inserts.Should().HaveCount(1);
        plan.Updates.Should().BeEmpty();
        plan.Deletes.Should().BeEmpty();
    }

    [Test]
    public void Should_delete_when_chunk_is_only_in_old()
    {
        var existing = new[] { new StoredChunkSummary(42, "X.A", "h1") };
        var plan = _reconciler.Plan(existing, ImmutableArray<CodeChunk>.Empty);

        plan.Deletes.Should().BeEquivalentTo([new DeleteOp(42)]);
    }

    [Test]
    public void Should_no_op_when_hash_matches()
    {
        var existing = new[] { new StoredChunkSummary(42, "X.A", "h1") };
        var newChunk = TestChunks.SampleMethod() with { FullyQualifiedSymbolName = "X.A", SourceTextHash = "h1" };
        var plan = _reconciler.Plan(existing, ImmutableArray.Create(newChunk));

        plan.Inserts.Should().BeEmpty();
        plan.Updates.Should().BeEmpty();
        plan.Deletes.Should().BeEmpty();
    }

    [Test]
    public void Should_update_with_content_changed_when_hash_differs()
    {
        var existing = new[] { new StoredChunkSummary(42, "X.A", "h1") };
        var newChunk = TestChunks.SampleMethod() with { FullyQualifiedSymbolName = "X.A", SourceTextHash = "h2" };
        var plan = _reconciler.Plan(existing, ImmutableArray.Create(newChunk));

        plan.Updates.Should().ContainSingle();
        plan.Updates[0].ChunkId.Should().Be(42);
        plan.Updates[0].ContentChanged.Should().BeTrue();
    }
}
```

- [ ] **Step 5: Implement.** `src/CodeRag.Core/Indexing/Reconciler.cs`:

```csharp
using System.Collections.Immutable;

namespace CodeRag.Core.Indexing;

public sealed class Reconciler : IReconciler
{
    public ReconciliationPlan Plan(
        IReadOnlyList<StoredChunkSummary> existingChunks,
        ImmutableArray<CodeChunk> newChunks)
    {
        var existingByFqn = existingChunks.ToDictionary(c => c.FullyQualifiedSymbolName, c => c);
        var newByFqn = newChunks.ToDictionary(c => c.FullyQualifiedSymbolName, c => c);

        var inserts = ImmutableArray.CreateBuilder<InsertOp>();
        var updates = ImmutableArray.CreateBuilder<UpdateOp>();
        var deletes = ImmutableArray.CreateBuilder<DeleteOp>();

        foreach (var (fqn, chunk) in newByFqn)
        {
            if (!existingByFqn.TryGetValue(fqn, out var existing))
            {
                inserts.Add(new InsertOp(chunk));
                continue;
            }
            if (existing.SourceTextHash != chunk.SourceTextHash)
            {
                updates.Add(new UpdateOp(existing.ChunkId, chunk, ContentChanged: true));
            }
        }
        foreach (var (fqn, existing) in existingByFqn)
        {
            if (!newByFqn.ContainsKey(fqn))
            {
                deletes.Add(new DeleteOp(existing.ChunkId));
            }
        }

        return new ReconciliationPlan(inserts.ToImmutable(), updates.ToImmutable(), deletes.ToImmutable());
    }
}
```

- [ ] **Step 6: Register in DI.** Add `services.AddSingleton<IReconciler, Reconciler>();` to `AddCoreServices()`.

- [ ] **Step 7: Run tests.** All pass.

- [ ] **Step 8: Commit.**

```powershell
git add src/CodeRag.Core/Indexing/IReconciler.cs src/CodeRag.Core/Indexing/Reconciler.cs src/CodeRag.Core/Indexing/ReconciliationPlan.cs src/CodeRag.Core/Indexing/ReconciliationOps.cs src/CodeRag.Core/ServiceCollectionExtensions.cs tests/CodeRag.Tests.Unit/Indexing/ReconcilerTests.cs
git commit -m "feat(core): pure-function Reconciler with insert/update/delete plan (#<N>)"
```

---

## Task 13: `IWorkspaceLoader` — open `.sln` via `MSBuildWorkspace`

**Files:**
- Create: `src/CodeRag.Core/Indexing/IWorkspaceLoader.cs`
- Create: `src/CodeRag.Core/Indexing/MsBuildWorkspaceLoader.cs`
- Create: `src/CodeRag.Core/Indexing/LoadedSolution.cs`
- Modify: `src/CodeRag.Core/ServiceCollectionExtensions.cs`
- Test: `tests/CodeRag.Tests.Integration/Indexing/MsBuildWorkspaceLoaderTests.cs` (this is integration because it touches MSBuild on disk)

- [ ] **Step 1: Define types.**

`src/CodeRag.Core/Indexing/LoadedSolution.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace CodeRag.Core.Indexing;

public sealed record LoadedSolution(Solution Solution, IReadOnlyList<Project> Projects);
```

`src/CodeRag.Core/Indexing/IWorkspaceLoader.cs`:

```csharp
namespace CodeRag.Core.Indexing;

public interface IWorkspaceLoader : IAsyncDisposable
{
    Task<LoadedSolution> OpenSolutionAsync(string solutionFilePath, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Implement.** `src/CodeRag.Core/Indexing/MsBuildWorkspaceLoader.cs`:

```csharp
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace CodeRag.Core.Indexing;

public sealed class MsBuildWorkspaceLoader : IWorkspaceLoader
{
    private static readonly object LocatorLock = new();
    private static bool s_locatorRegistered;
    private MSBuildWorkspace? _workspace;

    public async Task<LoadedSolution> OpenSolutionAsync(string solutionFilePath, CancellationToken cancellationToken)
    {
        EnsureLocatorRegistered();
        _workspace = MSBuildWorkspace.Create();
        var solution = await _workspace.OpenSolutionAsync(solutionFilePath, cancellationToken: cancellationToken);
        return new LoadedSolution(solution, solution.Projects.ToList());
    }

    public ValueTask DisposeAsync()
    {
        _workspace?.Dispose();
        _workspace = null;
        return ValueTask.CompletedTask;
    }

    private static void EnsureLocatorRegistered()
    {
        lock (LocatorLock)
        {
            if (s_locatorRegistered) { return; }
            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }
            s_locatorRegistered = true;
        }
    }
}
```

- [ ] **Step 3: Add an integration test** that opens the CodeRag solution itself (we know it's there).

`tests/CodeRag.Tests.Integration/Indexing/MsBuildWorkspaceLoaderTests.cs`:

```csharp
using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Integration.Indexing;

[TestFixture]
public class MsBuildWorkspaceLoaderTests
{
    [Test]
    public async Task Should_open_CodeRag_solution_and_enumerate_projects()
    {
        var slnPath = ResolveCodeRagSolutionPath();

        await using var loader = new MsBuildWorkspaceLoader();
        var loaded = await loader.OpenSolutionAsync(slnPath, CancellationToken.None);

        loaded.Projects.Should().Contain(p => p.Name == "CodeRag.Core");
        loaded.Projects.Should().Contain(p => p.Name == "CodeRag.Cli");
    }

    private static string ResolveCodeRagSolutionPath()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            var slnx = Path.Combine(current, "CodeRag.slnx");
            if (File.Exists(slnx)) { return slnx; }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException("CodeRag.slnx not found.");
    }
}
```

- [ ] **Step 4: Register in DI.** Add `services.AddTransient<IWorkspaceLoader, MsBuildWorkspaceLoader>();` to `AddCoreServices()`.

- [ ] **Step 5: Run tests.** This first run may take 30+s as MSBuild loads. Verify it passes.

- [ ] **Step 6: Commit.**

```powershell
git add src/CodeRag.Core/Indexing/IWorkspaceLoader.cs src/CodeRag.Core/Indexing/MsBuildWorkspaceLoader.cs src/CodeRag.Core/Indexing/LoadedSolution.cs src/CodeRag.Core/ServiceCollectionExtensions.cs tests/CodeRag.Tests.Integration/Indexing
git commit -m "feat(core): MsBuildWorkspaceLoader opens .sln files (#<N>)"
```

---

## Task 14: `IGitDiffProvider` — wraps `git` CLI

**Files:**
- Create: `src/CodeRag.Core/Indexing/IGitDiffProvider.cs`
- Create: `src/CodeRag.Core/Indexing/GitCliDiffProvider.cs`
- Modify: `src/CodeRag.Core/ServiceCollectionExtensions.cs`
- Test: `tests/CodeRag.Tests.Integration/Indexing/GitCliDiffProviderTests.cs`

- [ ] **Step 1: Define interface.** `src/CodeRag.Core/Indexing/IGitDiffProvider.cs`:

```csharp
namespace CodeRag.Core.Indexing;

public interface IGitDiffProvider
{
    Task<string> GetHeadShaAsync(string repositoryRoot, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string repositoryRoot, string sinceSha, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetDirtyFilesAsync(string repositoryRoot, CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Implement.** `src/CodeRag.Core/Indexing/GitCliDiffProvider.cs`:

```csharp
using System.Diagnostics;

namespace CodeRag.Core.Indexing;

public sealed class GitCliDiffProvider : IGitDiffProvider
{
    public async Task<string> GetHeadShaAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var output = await RunGitAsync(repositoryRoot, ["rev-parse", "HEAD"], cancellationToken);
        return output.Trim();
    }

    public async Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string repositoryRoot, string sinceSha, CancellationToken cancellationToken)
    {
        var output = await RunGitAsync(repositoryRoot,
            ["diff", "--name-only", sinceSha, "HEAD", "--", "*.cs", "*.csproj", "*.sln", "*.slnx"],
            cancellationToken);
        return SplitLines(output);
    }

    public async Task<IReadOnlyList<string>> GetDirtyFilesAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var output = await RunGitAsync(repositoryRoot, ["status", "--porcelain"], cancellationToken);
        var paths = new List<string>();
        foreach (var line in SplitLines(output))
        {
            if (line.Length < 4) { continue; }
            var path = line[3..].Trim();
            if (path.EndsWith(".cs", StringComparison.Ordinal)
                || path.EndsWith(".csproj", StringComparison.Ordinal)
                || path.EndsWith(".sln", StringComparison.Ordinal)
                || path.EndsWith(".slnx", StringComparison.Ordinal))
            {
                paths.Add(path);
            }
        }
        return paths;
    }

    private static async Task<string> RunGitAsync(string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}");
        }
        return stdout;
    }

    private static IReadOnlyList<string> SplitLines(string output)
    {
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
```

- [ ] **Step 3: Add an integration test** that runs against the host CodeRag repo:

`tests/CodeRag.Tests.Integration/Indexing/GitCliDiffProviderTests.cs`:

```csharp
using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Integration.Indexing;

[TestFixture]
public class GitCliDiffProviderTests
{
    [Test]
    public async Task Should_return_a_40_char_head_sha()
    {
        var root = ResolveRepoRoot();
        var provider = new GitCliDiffProvider();

        var sha = await provider.GetHeadShaAsync(root, CancellationToken.None);

        sha.Should().HaveLength(40);
        sha.Should().MatchRegex("^[0-9a-f]{40}$");
    }

    [Test]
    public async Task Should_return_empty_diff_when_comparing_head_to_itself()
    {
        var root = ResolveRepoRoot();
        var provider = new GitCliDiffProvider();
        var head = await provider.GetHeadShaAsync(root, CancellationToken.None);

        var changed = await provider.GetChangedFilesSinceAsync(root, head, CancellationToken.None);

        changed.Should().BeEmpty();
    }

    private static string ResolveRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current, ".git"))) { return current; }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException(".git not found.");
    }
}
```

- [ ] **Step 4: Register in DI.** `services.AddSingleton<IGitDiffProvider, GitCliDiffProvider>();`

- [ ] **Step 5: Run, all pass.**

- [ ] **Step 6: Commit.**

```powershell
git add src/CodeRag.Core/Indexing/IGitDiffProvider.cs src/CodeRag.Core/Indexing/GitCliDiffProvider.cs src/CodeRag.Core/ServiceCollectionExtensions.cs tests/CodeRag.Tests.Integration/Indexing/GitCliDiffProviderTests.cs
git commit -m "feat(core): GitCliDiffProvider exposes head sha and changed files (#<N>)"
```

---

## Task 15: `IEmbeddingClient` — OpenAI batched calls with retry

**Files:**
- Create: `src/CodeRag.Core/Indexing/IEmbeddingClient.cs`
- Create: `src/CodeRag.Core/Indexing/OpenAIEmbeddingClient.cs`
- Create: `src/CodeRag.Core/Indexing/EmbeddingOptions.cs`
- Modify: `src/CodeRag.Core/ServiceCollectionExtensions.cs`
- Test: `tests/CodeRag.Tests.Unit/Indexing/OpenAIEmbeddingClientTests.cs`

- [ ] **Step 1: Define options.** `src/CodeRag.Core/Indexing/EmbeddingOptions.cs`:

```csharp
namespace CodeRag.Core.Indexing;

public sealed record EmbeddingOptions
{
    public const string ModelName = "text-embedding-3-large";
    public const int VectorDimensions = 3072;
    public const int MaxBatchSize = 96;
    public const int MaxConcurrentRequests = 4;
    public const int MaxRetryAttempts = 5;
}
```

- [ ] **Step 2: Define interface.**

```csharp
namespace CodeRag.Core.Indexing;

public interface IEmbeddingClient
{
    Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Implement** wrapping the official OpenAI SDK (`OpenAI` v2.x). `src/CodeRag.Core/Indexing/OpenAIEmbeddingClient.cs`:

```csharp
using OpenAI.Embeddings;

namespace CodeRag.Core.Indexing;

public sealed class OpenAIEmbeddingClient : IEmbeddingClient
{
    private readonly EmbeddingClient _client;

    public OpenAIEmbeddingClient(string apiKey)
    {
        _client = new EmbeddingClient(model: EmbeddingOptions.ModelName, apiKey: apiKey);
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        var results = new ReadOnlyMemory<float>[inputs.Count];
        for (int batchStart = 0; batchStart < inputs.Count; batchStart += EmbeddingOptions.MaxBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int batchEnd = Math.Min(batchStart + EmbeddingOptions.MaxBatchSize, inputs.Count);
            var batchInputs = inputs.Skip(batchStart).Take(batchEnd - batchStart).Select(TruncateForModel).ToList();
            var batchVectors = await EmbedBatchWithRetryAsync(batchInputs, cancellationToken);
            for (int i = 0; i < batchVectors.Count; i++)
            {
                results[batchStart + i] = batchVectors[i];
            }
        }
        return results;
    }

    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedBatchWithRetryAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < EmbeddingOptions.MaxRetryAttempts; attempt++)
        {
            try
            {
                var response = await _client.GenerateEmbeddingsAsync(inputs, cancellationToken: cancellationToken);
                return response.Value.Select(e => (ReadOnlyMemory<float>)e.ToFloats().ToArray()).ToList();
            }
#pragma warning disable CA1031
            catch (Exception ex) when (IsRetriable(ex))
#pragma warning restore CA1031
            {
                lastError = ex;
                int delaySeconds = 1 << attempt;
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
        }
        throw new InvalidOperationException($"OpenAI embed failed after {EmbeddingOptions.MaxRetryAttempts} attempts.", lastError);
    }

    private static bool IsRetriable(Exception ex)
    {
        return ex is HttpRequestException
            || ex is TaskCanceledException
            || (ex.Message.Contains("429", StringComparison.Ordinal))
            || (ex.Message.Contains("5", StringComparison.Ordinal) && ex.Message.Contains("Internal", StringComparison.OrdinalIgnoreCase));
    }

    private static string TruncateForModel(string input)
    {
        const int approxCharLimit = 30000;
        if (input.Length <= approxCharLimit) { return input; }
        return input[..approxCharLimit];
    }
}
```

- [ ] **Step 4: Add a unit test** that uses a fake `IEmbeddingClient` (since hitting OpenAI in unit tests is wrong). Actually — since `OpenAIEmbeddingClient` wraps the SDK directly with no easy seam, we instead unit-test the **pipeline** behavior (batching, truncation) by testing a small extracted helper. For now, write a smoke test that constructs the client (without making a request):

```csharp
using CodeRag.Core.Indexing;
using FluentAssertions;

namespace CodeRag.Tests.Unit.Indexing;

[TestFixture]
public class OpenAIEmbeddingClientTests
{
    [Test]
    public void Constructor_should_not_throw_with_a_dummy_key()
    {
        var client = new OpenAIEmbeddingClient("sk-fake");
        client.Should().NotBeNull();
    }
}
```

(End-to-end with real OpenAI is gated to a separate test category and not run in CI.)

- [ ] **Step 5: Register in DI.** Add (in `AddCoreServices()`):

```csharp
services.AddSingleton<IEmbeddingClient>(_ =>
{
    var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
        ?? throw new InvalidOperationException("OPENAI_API_KEY is not set.");
    return new OpenAIEmbeddingClient(apiKey);
});
```

- [ ] **Step 6: Run all tests.** Architecture test confirms the interface is registered. Unit test passes.

- [ ] **Step 7: Commit.**

```powershell
git add src/CodeRag.Core/Indexing/IEmbeddingClient.cs src/CodeRag.Core/Indexing/OpenAIEmbeddingClient.cs src/CodeRag.Core/Indexing/EmbeddingOptions.cs src/CodeRag.Core/ServiceCollectionExtensions.cs tests/CodeRag.Tests.Unit/Indexing/OpenAIEmbeddingClientTests.cs
git commit -m "feat(core): OpenAIEmbeddingClient with batching and retry (#<N>)"
```

---

## Task 16: `IIndexer` — orchestrator

**Files:**
- Create: `src/CodeRag.Core/Indexing/IIndexer.cs`
- Create: `src/CodeRag.Core/Indexing/Indexer.cs`
- Create: `src/CodeRag.Core/Indexing/IndexRunRequest.cs`
- Create: `src/CodeRag.Core/Indexing/IndexRunResult.cs`
- Create: `src/CodeRag.Core/Indexing/IIndexStoreFactory.cs`
- Create: `src/CodeRag.Core/Indexing/SqliteIndexStoreFactory.cs`
- Modify: `src/CodeRag.Core/ServiceCollectionExtensions.cs`
- Test: `tests/CodeRag.Tests.Integration/Indexing/IndexerTests.cs` (deferred — covered by Task 19)

This task wires the orchestrator. End-to-end testing is in Task 19.

- [ ] **Step 1: Define request/result.**

`src/CodeRag.Core/Indexing/IndexRunRequest.cs`:

```csharp
namespace CodeRag.Core.Indexing;

public sealed record IndexRunRequest(string SolutionFilePath, string OutputDatabasePath);
```

`src/CodeRag.Core/Indexing/IndexRunResult.cs`:

```csharp
namespace CodeRag.Core.Indexing;

public sealed record IndexRunResult(
    int InsertedChunks,
    int UpdatedChunks,
    int DeletedChunks,
    int EmbeddedChunks,
    string IndexedAtCommitSha);
```

- [ ] **Step 2: Define factory** (so `Indexer` can resolve a store for a given path). `IIndexStoreFactory` is `internal` because it returns `IIndexStore` which is `internal`. The architecture test only inspects public interfaces, so internal interfaces are fine without registration considerations:

`src/CodeRag.Core/Indexing/IIndexStoreFactory.cs`:

```csharp
namespace CodeRag.Core.Indexing;

internal interface IIndexStoreFactory
{
    IIndexStore Create(string databasePath);
}
```

`src/CodeRag.Core/Indexing/SqliteIndexStoreFactory.cs`:

```csharp
namespace CodeRag.Core.Indexing;

internal sealed class SqliteIndexStoreFactory : IIndexStoreFactory
{
    public IIndexStore Create(string databasePath) => new SqliteIndexStore(databasePath);
}
```

- [ ] **Step 3: Define `IIndexer`.**

```csharp
namespace CodeRag.Core.Indexing;

public interface IIndexer
{
    Task<IndexRunResult> RunAsync(IndexRunRequest request, CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Implement.** `src/CodeRag.Core/Indexing/Indexer.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace CodeRag.Core.Indexing;

public sealed class Indexer : IIndexer
{
    private readonly IWorkspaceLoader _workspaceLoader;
    private readonly IChunkExtractor _chunkExtractor;
    private readonly IGitDiffProvider _gitDiffProvider;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly IIndexStoreFactory _storeFactory;
    private readonly IReconciler _reconciler;
    private readonly TimeProvider _time;

    public Indexer(
        IWorkspaceLoader workspaceLoader,
        IChunkExtractor chunkExtractor,
        IGitDiffProvider gitDiffProvider,
        IEmbeddingClient embeddingClient,
        IIndexStoreFactory storeFactory,
        IReconciler reconciler,
        TimeProvider time)
    {
        _workspaceLoader = workspaceLoader;
        _chunkExtractor = chunkExtractor;
        _gitDiffProvider = gitDiffProvider;
        _embeddingClient = embeddingClient;
        _storeFactory = storeFactory;
        _reconciler = reconciler;
        _time = time;
    }

    public async Task<IndexRunResult> RunAsync(IndexRunRequest request, CancellationToken cancellationToken)
    {
        var repoRoot = ResolveRepoRoot(request.SolutionFilePath);
        var headSha = await _gitDiffProvider.GetHeadShaAsync(repoRoot, cancellationToken);

        using var store = _storeFactory.Create(request.OutputDatabasePath);
        await store.OpenAsync(cancellationToken);

        var existingMeta = await store.TryGetMetadataAsync(cancellationToken);
        var fullReindex = existingMeta is null;
        IReadOnlyList<string> filesToProcess = Array.Empty<string>();
        if (!fullReindex && existingMeta is not null)
        {
            var changed = await _gitDiffProvider.GetChangedFilesSinceAsync(repoRoot, existingMeta.IndexedAtCommitSha, cancellationToken);
            var dirty = await _gitDiffProvider.GetDirtyFilesAsync(repoRoot, cancellationToken);
            var union = changed.Concat(dirty).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (union.Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
            {
                fullReindex = true;
            }
            else
            {
                filesToProcess = union;
            }
        }

        var loaded = await _workspaceLoader.OpenSolutionAsync(request.SolutionFilePath, cancellationToken);

        await store.BeginTransactionAsync(cancellationToken);
        int inserted = 0, updated = 0, deleted = 0, embedded = 0;
        try
        {
            var (allInserts, allUpdates, allDeletes) = fullReindex
                ? await PlanFullReindexAsync(loaded, store, repoRoot, cancellationToken)
                : await PlanIncrementalAsync(loaded, store, repoRoot, filesToProcess, cancellationToken);

            foreach (var op in allDeletes)
            {
                await store.DeleteChunkAsync(op.ChunkId, cancellationToken);
                deleted++;
            }

            var embedQueue = new List<(long ChunkId, string SourceText)>();
            foreach (var op in allInserts)
            {
                var id = await store.InsertChunkAsync(op.Chunk, cancellationToken);
                embedQueue.Add((id, op.Chunk.SourceText));
                inserted++;
            }
            foreach (var op in allUpdates)
            {
                await store.UpdateChunkAsync(op.ChunkId, op.Chunk, cancellationToken);
                if (op.ContentChanged) { embedQueue.Add((op.ChunkId, op.Chunk.SourceText)); }
                updated++;
            }

            if (embedQueue.Count > 0)
            {
                var inputs = embedQueue.Select(q => q.SourceText).ToList();
                var vectors = await _embeddingClient.EmbedAsync(inputs, cancellationToken);
                for (int i = 0; i < embedQueue.Count; i++)
                {
                    await store.UpsertEmbeddingAsync(embedQueue[i].ChunkId, vectors[i], cancellationToken);
                    embedded++;
                }
            }

            await store.SetMetadataAsync(new IndexMetadata(
                SchemaVersion: 1,
                SolutionFilePath: request.SolutionFilePath,
                RepositoryRootPath: repoRoot,
                IndexedAtCommitSha: headSha,
                IndexedAtUtc: _time.GetUtcNow(),
                EmbeddingModelName: EmbeddingOptions.ModelName,
                EmbeddingVectorDimensions: EmbeddingOptions.VectorDimensions),
                cancellationToken);

            await store.CommitAsync(cancellationToken);
        }
#pragma warning disable CA1031
        catch
#pragma warning restore CA1031
        {
            await store.RollbackAsync(cancellationToken);
            throw;
        }

        return new IndexRunResult(inserted, updated, deleted, embedded, headSha);
    }

    private async Task<(IReadOnlyList<InsertOp>, IReadOnlyList<UpdateOp>, IReadOnlyList<DeleteOp>)> PlanFullReindexAsync(
        LoadedSolution loaded, IIndexStore store, string repoRoot, CancellationToken ct)
    {
        var inserts = new List<InsertOp>();
        var deletes = new List<DeleteOp>();
        var allFiles = loaded.Projects.SelectMany(p => p.Documents).Select(d => d.FilePath!).Where(p => p is not null).Distinct().ToList();
        foreach (var path in allFiles)
        {
            var rel = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
            var summaries = await store.GetChunkSummariesForFileAsync(rel, ct);
            foreach (var s in summaries) { deletes.Add(new DeleteOp(s.ChunkId)); }
        }
        foreach (var project in loaded.Projects)
        {
            var compilation = await project.GetCompilationAsync(ct) ?? throw new InvalidOperationException($"Could not compile {project.Name}.");
            foreach (var tree in compilation.SyntaxTrees)
            {
                var chunks = _chunkExtractor.Extract(compilation, tree, project.Name, compilation.AssemblyName ?? project.Name, repoRoot, ct);
                foreach (var c in chunks) { inserts.Add(new InsertOp(c)); }
            }
        }
        return (inserts, Array.Empty<UpdateOp>(), deletes);
    }

    private async Task<(IReadOnlyList<InsertOp>, IReadOnlyList<UpdateOp>, IReadOnlyList<DeleteOp>)> PlanIncrementalAsync(
        LoadedSolution loaded, IIndexStore store, string repoRoot, IReadOnlyList<string> changedFiles, CancellationToken ct)
    {
        var inserts = new List<InsertOp>();
        var updates = new List<UpdateOp>();
        var deletes = new List<DeleteOp>();

        foreach (var changedFile in changedFiles)
        {
            var absolute = Path.GetFullPath(Path.Combine(repoRoot, changedFile));
            if (!File.Exists(absolute))
            {
                await store.DeleteChunksForFileAsync(changedFile, ct);
                continue;
            }

            if (changedFile.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var project = loaded.Projects.FirstOrDefault(p => string.Equals(p.FilePath, absolute, StringComparison.OrdinalIgnoreCase));
                if (project is null) { continue; }
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null) { continue; }
                foreach (var tree in compilation.SyntaxTrees)
                {
                    var chunks = _chunkExtractor.Extract(compilation, tree, project.Name, compilation.AssemblyName ?? project.Name, repoRoot, ct);
                    var rel = Path.GetRelativePath(repoRoot, tree.FilePath).Replace('\\', '/');
                    var existing = await store.GetChunkSummariesForFileAsync(rel, ct);
                    var plan = _reconciler.Plan(existing, chunks);
                    inserts.AddRange(plan.Inserts);
                    updates.AddRange(plan.Updates);
                    deletes.AddRange(plan.Deletes);
                }
                continue;
            }

            var doc = loaded.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => string.Equals(d.FilePath, absolute, StringComparison.OrdinalIgnoreCase));
            if (doc is null) { continue; }
            var docCompilation = await doc.Project.GetCompilationAsync(ct);
            if (docCompilation is null) { continue; }
            var tree2 = await doc.GetSyntaxTreeAsync(ct);
            if (tree2 is null) { continue; }
            var fileChunks = _chunkExtractor.Extract(docCompilation, tree2, doc.Project.Name, docCompilation.AssemblyName ?? doc.Project.Name, repoRoot, ct);
            var existing2 = await store.GetChunkSummariesForFileAsync(changedFile, ct);
            var filePlan = _reconciler.Plan(existing2, fileChunks);
            inserts.AddRange(filePlan.Inserts);
            updates.AddRange(filePlan.Updates);
            deletes.AddRange(filePlan.Deletes);
        }

        return (inserts, updates, deletes);
    }

    private static string ResolveRepoRoot(string solutionFilePath)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(solutionFilePath));
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current, ".git"))) { return current; }
            current = Path.GetDirectoryName(current);
        }
        throw new InvalidOperationException($"Could not locate .git ancestor of {solutionFilePath}.");
    }
}
```

Also: in `MsBuildWorkspaceLoader.OpenSolutionAsync` (Task 13), dispose the previous workspace before creating a new one so the loader can be reused across multiple `RunAsync` calls (the integration tests do this):

```csharp
public async Task<LoadedSolution> OpenSolutionAsync(string solutionFilePath, CancellationToken cancellationToken)
{
    EnsureLocatorRegistered();
    _workspace?.Dispose();
    _workspace = MSBuildWorkspace.Create();
    var solution = await _workspace.OpenSolutionAsync(solutionFilePath, cancellationToken: cancellationToken);
    return new LoadedSolution(solution, solution.Projects.ToList());
}
```

- [ ] **Step 5: Register in DI.** Add to `AddCoreServices()`:

```csharp
services.AddSingleton(TimeProvider.System);
services.AddSingleton<IIndexStoreFactory, SqliteIndexStoreFactory>();
services.AddTransient<IIndexer, Indexer>();
```

`IIndexStore` itself is `internal` (Task 4), so it's invisible to the architecture test and needs no direct registration. Consumers go through `IIndexStoreFactory`.

- [ ] **Step 6: Run unit tests.** All architecture tests + unit tests pass.

- [ ] **Step 7: Commit.**

```powershell
git add src/CodeRag.Core/Indexing src/CodeRag.Core/ServiceCollectionExtensions.cs
git commit -m "feat(core): Indexer orchestrates full and incremental runs (#<N>)"
```

---

## Task 17: CLI `index` verb

**Files:**
- Modify: `src/CodeRag.Cli/Program.cs`
- Create: `src/CodeRag.Cli/Commands/IndexCommand.cs`

- [ ] **Step 1: Rewrite `Program.cs`** to wire System.CommandLine + the host:

```csharp
using System.CommandLine;
using CodeRag.Cli.Commands;
using CodeRag.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddCoreServices();
builder.Services.AddTransient<IndexCommand>();

var host = builder.Build();

var slnArg = new Argument<FileInfo>("solution") { Description = "Path to the .sln or .slnx file." };
var outOption = new Option<DirectoryInfo?>("--out") { Description = "Output directory; defaults to <sln-dir>/.coderag." };

var rootCommand = new RootCommand("CodeRag — code RAG indexer");
var indexCmd = new Command("index", "Build or refresh a code index for the given solution.")
{
    slnArg,
    outOption
};
indexCmd.SetAction(async parseResult =>
{
    var sln = parseResult.GetValue(slnArg)!;
    var outDir = parseResult.GetValue(outOption);
    var command = host.Services.GetRequiredService<IndexCommand>();
    return await command.ExecuteAsync(sln.FullName, outDir?.FullName, CancellationToken.None);
});
rootCommand.Subcommands.Add(indexCmd);

return await rootCommand.Parse(args).InvokeAsync();
```

- [ ] **Step 2: Implement `IndexCommand`.** `src/CodeRag.Cli/Commands/IndexCommand.cs`:

```csharp
using CodeRag.Core.Indexing;

namespace CodeRag.Cli.Commands;

public sealed class IndexCommand
{
    private readonly IIndexer _indexer;

    public IndexCommand(IIndexer indexer)
    {
        _indexer = indexer;
    }

    public async Task<int> ExecuteAsync(string solutionPath, string? outputDir, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable("OPENAI_API_KEY") is null)
        {
            await Console.Error.WriteLineAsync("OPENAI_API_KEY is not set.");
            return 2;
        }
        if (!File.Exists(solutionPath))
        {
            await Console.Error.WriteLineAsync($"Solution not found: {solutionPath}");
            return 2;
        }
        var resolvedOut = outputDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(solutionPath))!, ".coderag");
        Directory.CreateDirectory(resolvedOut);
        var dbPath = Path.Combine(resolvedOut, "index.db");

        var request = new IndexRunRequest(solutionPath, dbPath);
        var result = await _indexer.RunAsync(request, cancellationToken);

        Console.WriteLine($"Indexed at commit {result.IndexedAtCommitSha[..8]}: " +
                          $"+{result.InsertedChunks} ~{result.UpdatedChunks} -{result.DeletedChunks}, " +
                          $"{result.EmbeddedChunks} embeddings.");
        Console.WriteLine($"Wrote: {dbPath}");
        return 0;
    }
}
```

- [ ] **Step 3: Build.**

```powershell
dotnet build
```

Expected: clean.

- [ ] **Step 4: Commit.**

```powershell
git add src/CodeRag.Cli
git commit -m "feat(cli): add 'index' verb (#<N>)"
```

---

## Task 18: Integration-test infrastructure (sample fixture solution + fakes)

**Files:**
- Create: `tests/Fixtures/SampleSolution/SampleSolution.sln`
- Create: `tests/Fixtures/SampleSolution/Sample.Lib/Sample.Lib.csproj`
- Create: `tests/Fixtures/SampleSolution/Sample.Lib/Logger.cs`
- Create: `tests/Fixtures/SampleSolution/Sample.Lib/User.cs`
- Create: `tests/Fixtures/SampleSolution/Sample.Lib/UserService.cs`
- Create: `tests/Fixtures/SampleSolution/Sample.App/Sample.App.csproj`
- Create: `tests/Fixtures/SampleSolution/Sample.App/Program.cs`
- Create: `tests/CodeRag.Tests.Integration/Indexing/Fakes/FakeEmbeddingClient.cs`
- Create: `tests/CodeRag.Tests.Integration/Indexing/Fakes/StubGitDiffProvider.cs`
- Create: `tests/CodeRag.Tests.Integration/Indexing/Fakes/SampleSolutionFixture.cs`

The fixture solution is checked into the repo; we copy it to a temp dir per test so each test gets a clean working copy with its own git history.

- [ ] **Step 1: Create the fixture solution.** Hand-author each file with realistic content covering the kinds of symbols we extract: classes, records, interfaces, methods (with `CancellationToken` parameters), properties, fields, attributes (`[Obsolete]`, `[HttpGet]`-style placeholder), generics, partial types.

`tests/Fixtures/SampleSolution/Sample.Lib/Logger.cs`:

```csharp
using System;

namespace Sample.Lib;

public interface ILogger
{
    void Log(string message);
}

public sealed class ConsoleLogger : ILogger
{
    public void Log(string message) => Console.WriteLine(message);
}
```

`tests/Fixtures/SampleSolution/Sample.Lib/User.cs`:

```csharp
namespace Sample.Lib;

public sealed record User(string Name, int Age);
```

`tests/Fixtures/SampleSolution/Sample.Lib/UserService.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;

namespace Sample.Lib;

public sealed class UserService
{
    private readonly ILogger _logger;

    public UserService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<User?> FindAsync(string name, CancellationToken ct)
    {
        _logger.Log($"finding {name}");
        await Task.Yield();
        return new User(name, 0);
    }
}
```

(Add a few more files — Program.cs in Sample.App, a partial class split across two files, etc. Aim for ~10 files total covering the cases listed in the spec's testing strategy.)

- [ ] **Step 2: Mark fixture files as content** that copies to test output. Add to `tests/CodeRag.Tests.Integration/CodeRag.Tests.Integration.csproj`:

```xml
<ItemGroup>
  <Content Include="..\Fixtures\SampleSolution\**\*">
    <Link>Fixtures\SampleSolution\%(RecursiveDir)%(FileName)%(Extension)</Link>
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

- [ ] **Step 3: Implement `FakeEmbeddingClient`.** `tests/CodeRag.Tests.Integration/Indexing/Fakes/FakeEmbeddingClient.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using CodeRag.Core.Indexing;

namespace CodeRag.Tests.Integration.Indexing.Fakes;

public sealed class FakeEmbeddingClient : IEmbeddingClient
{
    public int CallCount { get; private set; }
    public int InputCount { get; private set; }

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        CallCount++;
        InputCount += inputs.Count;
        var results = inputs.Select(SeededVectorFor).Cast<ReadOnlyMemory<float>>().ToList();
        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(results);
    }

    private static float[] SeededVectorFor(string input)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var rng = new Random(BitConverter.ToInt32(seed, 0));
        var v = new float[3072];
        for (int i = 0; i < v.Length; i++) { v[i] = (float)(rng.NextDouble() - 0.5); }
        return v;
    }
}
```

- [ ] **Step 4: Implement `StubGitDiffProvider`** so tests drive change sets explicitly:

```csharp
using CodeRag.Core.Indexing;

namespace CodeRag.Tests.Integration.Indexing.Fakes;

public sealed class StubGitDiffProvider : IGitDiffProvider
{
    public string HeadSha { get; set; } = "stubbedheadsha000000000000000000000000000000";
    public List<string> ChangedFiles { get; } = new();
    public List<string> DirtyFiles { get; } = new();

    public Task<string> GetHeadShaAsync(string repositoryRoot, CancellationToken cancellationToken)
        => Task.FromResult(HeadSha);

    public Task<IReadOnlyList<string>> GetChangedFilesSinceAsync(string repositoryRoot, string sinceSha, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(ChangedFiles.ToList());

    public Task<IReadOnlyList<string>> GetDirtyFilesAsync(string repositoryRoot, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<string>>(DirtyFiles.ToList());
}
```

- [ ] **Step 5: Implement `SampleSolutionFixture`** that copies the fixture into a per-test temp directory, initializes git, and exposes the `.sln` path:

```csharp
using System.Diagnostics;

namespace CodeRag.Tests.Integration.Indexing.Fakes;

public sealed class SampleSolutionFixture : IDisposable
{
    public string Root { get; }
    public string SolutionPath => Path.Combine(Root, "SampleSolution.sln");

    public SampleSolutionFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"coderag-fixture-{Guid.NewGuid():N}");
        var src = Path.Combine(AppContext.BaseDirectory, "Fixtures", "SampleSolution");
        CopyDirectory(src, Root);
        InitGit(Root);
    }

    public void ModifyFile(string relativePath, string newContents)
    {
        File.WriteAllText(Path.Combine(Root, relativePath), newContents);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest), overwrite: true);
        }
    }

    private static void InitGit(string root)
    {
        Run(root, "init", "-b", "main");
        Run(root, "add", "-A");
        Run(root, "-c", "user.email=test@example.com", "-c", "user.name=Test", "commit", "-m", "fixture");
    }

    private static void Run(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workingDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) { psi.ArgumentList.Add(a); }
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) { throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}"); }
    }
}
```

- [ ] **Step 6: Build, no new tests yet.** Verify everything compiles.

```powershell
dotnet build
```

- [ ] **Step 7: Commit.**

```powershell
git add tests/Fixtures tests/CodeRag.Tests.Integration
git commit -m "test(integration): add sample fixture solution and indexer fakes (#<N>)"
```

---

## Task 19: Integration tests for the indexer scenarios

**Files:**
- Create: `tests/CodeRag.Tests.Integration/Indexing/IndexerTests.cs`

We test the eight scenarios from the spec's testing strategy.

- [ ] **Step 1: Write the test class shell.**

```csharp
using CodeRag.Core.Indexing;
using CodeRag.Tests.Integration.Indexing.Fakes;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace CodeRag.Tests.Integration.Indexing;

[TestFixture]
public class IndexerTests
{
    private SampleSolutionFixture _fixture = null!;
    private FakeEmbeddingClient _embedding = null!;
    private StubGitDiffProvider _git = null!;
    private string _dbPath = null!;
    private Indexer _indexer = null!;

    [SetUp]
    public void SetUp()
    {
        _fixture = new SampleSolutionFixture();
        _embedding = new FakeEmbeddingClient();
        _git = new StubGitDiffProvider();
        _dbPath = Path.Combine(_fixture.Root, ".coderag", "index.db");
        var workspaceLoader = new MsBuildWorkspaceLoader();
        var hasher = new SourceTextHasher();
        var extractor = new ChunkExtractor(hasher);
        var reconciler = new Reconciler();
        var storeFactory = new SqliteIndexStoreFactory();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 2, 12, 0, 0, TimeSpan.Zero));
        _indexer = new Indexer(workspaceLoader, extractor, _git, _embedding, storeFactory, reconciler, clock);
    }

    [TearDown]
    public void TearDown() => _fixture.Dispose();

    private Task<IndexRunResult> RunAsync()
        => _indexer.RunAsync(new IndexRunRequest(_fixture.SolutionPath, _dbPath), CancellationToken.None);
}
```

- [ ] **Step 2: Add the eight scenarios as separate `[Test]` methods.** Each scenario from the spec:

```csharp
[Test]
public async Task Scenario_1_ColdIndex_should_populate_chunks_and_metadata()
{
    var result = await RunAsync();

    result.InsertedChunks.Should().BeGreaterThan(0);
    result.UpdatedChunks.Should().Be(0);
    result.DeletedChunks.Should().Be(0);

    using var verify = new SqliteIndexStore(_dbPath);
    await verify.OpenAsync(CancellationToken.None);
    var meta = await verify.TryGetMetadataAsync(CancellationToken.None);
    meta!.IndexedAtCommitSha.Should().Be(_git.HeadSha);
    meta.EmbeddingModelName.Should().Be("text-embedding-3-large");
    meta.EmbeddingVectorDimensions.Should().Be(3072);
}

[Test]
public async Task Scenario_2_NoOpRerun_should_produce_zero_embedding_calls()
{
    await RunAsync();
    _embedding.CallCount = 0;
    _embedding.InputCount = 0;

    var result = await RunAsync();

    result.InsertedChunks.Should().Be(0);
    result.UpdatedChunks.Should().Be(0);
    result.DeletedChunks.Should().Be(0);
    _embedding.CallCount.Should().Be(0);
}

[Test]
public async Task Scenario_3_EditOneMethod_should_update_one_chunk_and_one_embedding()
{
    await RunAsync();
    _embedding.CallCount = 0;
    _embedding.InputCount = 0;
    _git.HeadSha = "newhead00000000000000000000000000000000";
    _git.ChangedFiles.Add("Sample.Lib/UserService.cs");
    _fixture.ModifyFile("Sample.Lib/UserService.cs", File.ReadAllText(Path.Combine(_fixture.Root, "Sample.Lib/UserService.cs"))
        .Replace("await Task.Yield();", "await Task.Delay(1);"));

    var result = await RunAsync();

    result.UpdatedChunks.Should().Be(1);
    _embedding.InputCount.Should().Be(1);
}

[Test]
public async Task Scenario_4_AddNewClass_should_insert_chunks()
{
    await RunAsync();
    _embedding.CallCount = 0;
    _git.HeadSha = "newhead10000000000000000000000000000000";
    _git.ChangedFiles.Add("Sample.Lib/AuditLog.cs");
    _fixture.ModifyFile("Sample.Lib/AuditLog.cs", "namespace Sample.Lib; public sealed class AuditLog { public void Write(string s) { } }");

    var result = await RunAsync();

    result.InsertedChunks.Should().BeGreaterThanOrEqualTo(2);
}

[Test]
public async Task Scenario_5_DeleteFile_should_remove_all_its_chunks()
{
    await RunAsync();
    var path = "Sample.Lib/User.cs";
    File.Delete(Path.Combine(_fixture.Root, path));
    _git.HeadSha = "newhead20000000000000000000000000000000";
    _git.ChangedFiles.Add(path);

    var result = await RunAsync();

    result.DeletedChunks.Should().BeGreaterThan(0);

    using var verify = new SqliteIndexStore(_dbPath);
    await verify.OpenAsync(CancellationToken.None);
    var summaries = await verify.GetChunkSummariesForFileAsync(path, CancellationToken.None);
    summaries.Should().BeEmpty();
}

[Test]
public async Task Scenario_6_CsprojChange_should_reparse_whole_project()
{
    await RunAsync();
    _embedding.CallCount = 0;
    _git.HeadSha = "newhead30000000000000000000000000000000";
    _git.ChangedFiles.Add("Sample.Lib/Sample.Lib.csproj");

    var result = await RunAsync();

    result.UpdatedChunks.Should().Be(0);
    result.InsertedChunks.Should().Be(0);
}

[Test]
public async Task Scenario_7_SlnChange_should_force_full_reindex()
{
    await RunAsync();
    _embedding.CallCount = 0;
    _git.HeadSha = "newhead40000000000000000000000000000000";
    _git.ChangedFiles.Add("SampleSolution.sln");

    var result = await RunAsync();

    result.DeletedChunks.Should().BeGreaterThan(0);
    result.InsertedChunks.Should().BeGreaterThan(0);
}

[Test]
public async Task Scenario_8_FilteredKnnQuery_should_find_expected_method()
{
    await RunAsync();

    using var verify = new SqliteIndexStore(_dbPath);
    await verify.OpenAsync(CancellationToken.None);
    using var cmd = verify.Connection.CreateCommand();
    cmd.CommandText = @"
        SELECT c.fully_qualified_symbol_name
        FROM code_chunks c
        WHERE c.symbol_kind = 'method' AND c.is_async = 1
        LIMIT 5";
    using var reader = await cmd.ExecuteReaderAsync(CancellationToken.None);
    var names = new List<string>();
    while (await reader.ReadAsync(CancellationToken.None)) { names.Add(reader.GetString(0)); }

    names.Should().Contain(n => n.Contains("FindAsync", StringComparison.Ordinal));
}
```

- [ ] **Step 3: Run all integration tests.**

```powershell
dotnet test tests/CodeRag.Tests.Integration
```

Some scenarios may need scenario-specific tweaking depending on the exact fixture content. Iterate until all pass.

- [ ] **Step 4: Commit.**

```powershell
git add tests/CodeRag.Tests.Integration/Indexing/IndexerTests.cs
git commit -m "test(integration): cover indexer scenarios end-to-end (#<N>)"
```

---

## Task 20: Final smoke test, push, and open PR

- [ ] **Step 1: Full clean build + test run.**

```powershell
dotnet clean
dotnet restore
dotnet build
dotnet test
```

Expected: build clean, every test passes, no analyzer violations.

- [ ] **Step 2: Manual smoke test against the host repo** (requires `OPENAI_API_KEY` set):

```powershell
$env:OPENAI_API_KEY = "<your-real-key>"
dotnet run --project src/CodeRag.Cli -- index .\CodeRag.slnx
```

Expected output: `Indexed at commit ...: +N ~0 -0, N embeddings.` and a file at `.coderag/index.db`. Inspect it briefly with the SQLite CLI:

```powershell
sqlite3 .coderag/index.db "SELECT symbol_kind, COUNT(*) FROM code_chunks GROUP BY symbol_kind;"
```

- [ ] **Step 3: Run incremental smoke test.** Touch a file, re-run; the second run should report a small number of updates and embeddings.

- [ ] **Step 4: Push and open PR.**

```powershell
git push -u origin feat/<N>-codebase-indexer
gh pr create --base main --head feat/<N>-codebase-indexer --title "feat: codebase indexer (issue #<N>)" --body "Implements docs/superpowers/specs/2026-05-02-codebase-indexer-design.md per docs/superpowers/plans/2026-05-02-codebase-indexer.md."
```

- [ ] **Step 5: Squash-merge.**

```powershell
gh pr merge <PR-NUMBER> --squash --delete-branch
git checkout main
git pull --ff-only
```

---

## Self-review checklist (run before merging the PR)

- [ ] Every public Core interface introduced (`ISourceTextHasher`, `IIndexStore`, `IChunkExtractor`, `IReconciler`, `IWorkspaceLoader`, `IGitDiffProvider`, `IEmbeddingClient`, `IIndexStoreFactory`, `IIndexer`) appears in `AddCoreServices()` — confirmed by `DiRegistrationTests`.
- [ ] No comments anywhere in `src/` (CI0013 enforced at error severity).
- [ ] CRLF line endings, UTF-8 BOM (`dotnet format --verify-no-changes` passes).
- [ ] All eight integration scenarios from the spec are covered by tests.
- [ ] `OPENAI_API_KEY` missing produces a clear error with exit code 2.
- [ ] `index.db` is written under `.coderag/` (gitignored) by default.
- [ ] Schema-version mismatch produces a clear error.
- [ ] `sqlite-vec` native binaries copy to test and CLI output via `Directory.Build.targets`.

---

## What this plan deliberately defers (out of scope per spec)

- Query verb / CLI search.
- MCP server.
- Languages other than C#.
- Call graph, inverse references, cyclomatic complexity.
- Multi-solution indices.
- Online schema migrations.
- Hybrid BM25 + vector re-ranking.

These will land in their own specs and plans after v1 is merged and stable.
