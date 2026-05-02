using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
}
