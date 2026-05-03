using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CodeRag.Core.Indexing;

internal sealed class SqliteIndexStore : IIndexStore
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _databasePath;
    private SqliteConnection? _connection;
    private SqliteTransaction? _transaction;

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

    async Task<IReadOnlyList<StoredChunkSummary>> IIndexStore.GetChunkSummariesForFileAsync(string relativeFilePath, CancellationToken cancellationToken)
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

    async Task IIndexStore.DeleteChunkAsync(long chunkId, CancellationToken cancellationToken)
    {
        await DeleteEmbeddingRowAsync(chunkId, cancellationToken);
        await DeleteChildRowsAsync(chunkId, cancellationToken);
        await DeleteChunkRowAsync(chunkId, cancellationToken);
    }

    async Task IIndexStore.DeleteChunksForFileAsync(string relativeFilePath, CancellationToken cancellationToken)
    {
        IIndexStore self = this;
        var summaries = await self.GetChunkSummariesForFileAsync(relativeFilePath, cancellationToken);
        foreach (var summary in summaries)
        {
            await self.DeleteChunkAsync(summary.ChunkId, cancellationToken);
        }
    }

    private async Task DeleteEmbeddingRowAsync(long chunkId, CancellationToken cancellationToken)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "DELETE FROM chunk_embeddings WHERE rowid = $id";
        cmd.Parameters.AddWithValue("$id", chunkId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteChunkRowAsync(long chunkId, CancellationToken cancellationToken)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "DELETE FROM code_chunks WHERE chunk_id = $id";
        cmd.Parameters.AddWithValue("$id", chunkId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

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
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return await ProjectChunkAsync(chunkId, reader, cancellationToken);
    }

    private async Task<CodeChunk> ProjectChunkAsync(long chunkId, Microsoft.Data.Sqlite.SqliteDataReader reader, CancellationToken cancellationToken)
    {
        var modifiers = ReadModifiersFromReader(reader);
        var children = await ReadChildCollectionsAsync(chunkId, cancellationToken);
        return await BuildChunkAsync(reader, modifiers, children, cancellationToken);
    }

    private static ChunkModifiers ReadModifiersFromReader(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new ChunkModifiers(
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
    }

    private async Task<ChunkChildCollections> ReadChildCollectionsAsync(long chunkId, CancellationToken cancellationToken)
    {
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
        return new ChunkChildCollections(attributes, interfaces, parameters, generics);
    }

    private static async Task<CodeChunk> BuildChunkAsync(
        Microsoft.Data.Sqlite.SqliteDataReader reader,
        ChunkModifiers modifiers,
        ChunkChildCollections children,
        CancellationToken cancellationToken)
    {
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
            ContainingNamespace: await reader.IsDBNullAsync(9, cancellationToken) ? null : reader.GetString(9),
            ParentSymbolFullyQualifiedName: await reader.IsDBNullAsync(10, cancellationToken) ? null : reader.GetString(10),
            Accessibility: reader.GetString(11),
            Modifiers: modifiers,
            BaseTypeFullyQualifiedName: await reader.IsDBNullAsync(24, cancellationToken) ? null : reader.GetString(24),
            ReturnTypeFullyQualifiedName: await reader.IsDBNullAsync(25, cancellationToken) ? null : reader.GetString(25),
            ParameterCount: await reader.IsDBNullAsync(26, cancellationToken) ? null : reader.GetInt32(26),
            DocumentationCommentXml: await reader.IsDBNullAsync(27, cancellationToken) ? null : reader.GetString(27),
            SourceText: reader.GetString(28),
            SourceTextHash: reader.GetString(29),
            Attributes: children.Attributes.ToImmutableArray(),
            ImplementedInterfaceFullyQualifiedNames: children.Interfaces.ToImmutableArray(),
            Parameters: children.Parameters.ToImmutableArray(),
            GenericTypeParameters: children.Generics.ToImmutableArray());
    }

    private sealed record ChunkChildCollections(
        List<ChunkAttribute> Attributes,
        List<string> Interfaces,
        List<ChunkParameter> Parameters,
        List<ChunkGenericTypeParameter> Generics);

    internal async Task<bool> HasEmbeddingAsync(long chunkId, CancellationToken cancellationToken)
    {
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = "SELECT 1 FROM chunk_embeddings WHERE rowid = $id";
        cmd.Parameters.AddWithValue("$id", chunkId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    public void Dispose()
    {
        if (_transaction is not null)
        {
            _transaction.Dispose();
            _transaction = null;
        }
        if (_connection is not null)
        {
            SqliteConnection.ClearPool(_connection);
            _connection.Dispose();
            _connection = null;
        }
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

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "SQL is loaded from an embedded resource shipped with this assembly; no user input flows in.")]
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

    private static byte[] FloatArrayToBlob(ReadOnlySpan<float> floats)
    {
        var bytes = new byte[floats.Length * sizeof(float)];
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
        cmd.Parameters.AddWithValue("$pc", c.ParameterCount.HasValue ? c.ParameterCount.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$doc", (object?)c.DocumentationCommentXml ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$src", c.SourceText);
        cmd.Parameters.AddWithValue("$hash", c.SourceTextHash);
    }

    private async Task InsertChildRowsAsync(long chunkId, CodeChunk c, CancellationToken cancellationToken)
    {
        await InsertAttributesAsync(chunkId, c.Attributes, cancellationToken);
        await InsertImplementedInterfacesAsync(chunkId, c.ImplementedInterfaceFullyQualifiedNames, cancellationToken);
        await InsertMethodParametersAsync(chunkId, c.Parameters, cancellationToken);
        await InsertGenericTypeParametersAsync(chunkId, c.GenericTypeParameters, cancellationToken);
    }

    private async Task InsertAttributesAsync(long chunkId, ImmutableArray<ChunkAttribute> attributes, CancellationToken cancellationToken)
    {
        foreach (var attr in attributes)
        {
            await using var cmd = Connection.CreateCommand();
            cmd.Transaction = _transaction;
            cmd.CommandText = "INSERT INTO chunk_attributes(chunk_id, attribute_fully_qualified_name, attribute_arguments_json) VALUES ($id, $name, $args)";
            cmd.Parameters.AddWithValue("$id", chunkId);
            cmd.Parameters.AddWithValue("$name", attr.AttributeFullyQualifiedName);
            cmd.Parameters.AddWithValue("$args", (object?)attr.AttributeArgumentsJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task InsertImplementedInterfacesAsync(long chunkId, ImmutableArray<string> interfaces, CancellationToken cancellationToken)
    {
        foreach (var iface in interfaces)
        {
            await using var cmd = Connection.CreateCommand();
            cmd.Transaction = _transaction;
            cmd.CommandText = "INSERT INTO chunk_implemented_interfaces(chunk_id, interface_fully_qualified_name) VALUES ($id, $name)";
            cmd.Parameters.AddWithValue("$id", chunkId);
            cmd.Parameters.AddWithValue("$name", iface);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task InsertMethodParametersAsync(long chunkId, ImmutableArray<ChunkParameter> parameters, CancellationToken cancellationToken)
    {
        foreach (var p in parameters)
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
    }

    private async Task InsertGenericTypeParametersAsync(long chunkId, ImmutableArray<ChunkGenericTypeParameter> generics, CancellationToken cancellationToken)
    {
        foreach (var g in generics)
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

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table names are hard-coded in this method; no user input flows in.")]
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

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "SQL is provided by internal callers; no user input flows in.")]
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

    public async Task<IReadOnlyList<QueryHit>> Search(
        ReadOnlyMemory<float> queryVector,
        QueryFilters filters,
        int topK,
        CancellationToken cancellationToken)
    {
        var oversample = Math.Max(topK * 20, 200);
        await using var cmd = Connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = SearchSql;
        BindSearchParameters(cmd, queryVector, filters, topK, oversample);
        return await ReadSearchResults(cmd, cancellationToken);
    }

    private static void BindSearchParameters(
        SqliteCommand cmd,
        ReadOnlyMemory<float> queryVector,
        QueryFilters filters,
        int topK,
        int oversample)
    {
        cmd.Parameters.AddWithValue("$query_vector", FloatArrayToBlob(queryVector.Span));
        cmd.Parameters.AddWithValue("$oversample", oversample);
        cmd.Parameters.AddWithValue("$top_k", topK);
        cmd.Parameters.AddWithValue("$symbol_kind", (object?)filters.SymbolKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$project", (object?)filters.ContainingProjectName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$namespace", (object?)filters.ContainingNamespace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is_async", filters.IsAsync.HasValue ? (filters.IsAsync.Value ? 1 : 0) : DBNull.Value);
    }

    private static async Task<IReadOnlyList<QueryHit>> ReadSearchResults(SqliteCommand cmd, CancellationToken cancellationToken)
    {
        var results = new List<QueryHit>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new QueryHit(
                ChunkId: reader.GetInt64(0),
                RelativeFilePath: reader.GetString(1),
                LineStart: reader.GetInt32(2),
                LineEnd: reader.GetInt32(3),
                FullyQualifiedSymbolName: reader.GetString(4),
                SymbolKind: reader.GetString(5),
                Distance: reader.GetDouble(6),
                SourceText: reader.GetString(7)));
        }
        return results;
    }

    private const string SearchSql = @"WITH candidates AS (
    SELECT rowid, distance
    FROM chunk_embeddings
    WHERE embedding MATCH $query_vector
    ORDER BY distance
    LIMIT $oversample
)
SELECT c.chunk_id,
       c.relative_file_path,
       c.start_line_number,
       c.end_line_number,
       c.fully_qualified_symbol_name,
       c.symbol_kind,
       cand.distance,
       c.source_text
FROM candidates cand
JOIN code_chunks c ON c.chunk_id = cand.rowid
WHERE ($symbol_kind IS NULL OR c.symbol_kind = $symbol_kind)
  AND ($project IS NULL OR c.containing_project_name = $project)
  AND ($namespace IS NULL OR c.containing_namespace = $namespace)
  AND ($is_async IS NULL OR c.is_async = $is_async)
ORDER BY cand.distance
LIMIT $top_k";
}
