using Microsoft.Data.Sqlite;

namespace SoftPilot.Infrastructure.State;

public sealed class SqliteStateStore : IStateStore
{
    private static int _providerInitialized;
    private readonly string _connectionString;

    public SqliteStateStore(IInstallationLayout layout)
    {
        if (Interlocked.Exchange(ref _providerInitialized, 1) == 0)
        {
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(layout.DataDirectory, "softpilot.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false,
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS runtime_installations (
                kind            INTEGER NOT NULL,
                version         TEXT NOT NULL,
                architecture    INTEGER NOT NULL,
                install_path    TEXT NOT NULL,
                installed_at    TEXT NOT NULL,
                is_current      INTEGER NOT NULL DEFAULT 0,
                deleted_at      TEXT NULL,
                trash_path      TEXT NULL,
                PRIMARY KEY (kind, version)
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_runtime_current
                ON runtime_installations(kind)
                WHERE is_current = 1 AND deleted_at IS NULL;

            CREATE TABLE IF NOT EXISTS operations (
                id              TEXT PRIMARY KEY,
                name            TEXT NOT NULL,
                kind            INTEGER NULL,
                version         TEXT NULL,
                status          INTEGER NOT NULL,
                started_at      TEXT NOT NULL,
                completed_at    TEXT NULL,
                error           TEXT NULL
            );

            PRAGMA user_version = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RuntimeInstallation>> GetInstallationsAsync(
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT kind, version, architecture, install_path, installed_at, is_current, deleted_at, trash_path
            FROM runtime_installations
            {(includeDeleted ? string.Empty : "WHERE deleted_at IS NULL")}
            ORDER BY kind, installed_at DESC;
            """;

        var result = new List<RuntimeInstallation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadInstallation(reader));
        }

        return result;
    }

    public async Task<RuntimeInstallation?> FindInstallationAsync(
        RuntimeKind kind,
        string version,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT kind, version, architecture, install_path, installed_at, is_current, deleted_at, trash_path
            FROM runtime_installations
            WHERE kind = $kind AND version = $version {(includeDeleted ? string.Empty : "AND deleted_at IS NULL")}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$version", version);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadInstallation(reader) : null;
    }

    public async Task UpsertInstallationAsync(RuntimeInstallation installation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runtime_installations
                (kind, version, architecture, install_path, installed_at, is_current, deleted_at, trash_path)
            VALUES
                ($kind, $version, $architecture, $install_path, $installed_at, $is_current, $deleted_at, $trash_path)
            ON CONFLICT(kind, version) DO UPDATE SET
                architecture = excluded.architecture,
                install_path = excluded.install_path,
                installed_at = excluded.installed_at,
                is_current = excluded.is_current,
                deleted_at = excluded.deleted_at,
                trash_path = excluded.trash_path;
            """;
        AddInstallationParameters(command, installation);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetCurrentAsync(RuntimeKind kind, string? version, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var clear = connection.CreateCommand();
        clear.Transaction = (SqliteTransaction)transaction;
        clear.CommandText = "UPDATE runtime_installations SET is_current = 0 WHERE kind = $kind;";
        clear.Parameters.AddWithValue("$kind", (int)kind);
        await clear.ExecuteNonQueryAsync(cancellationToken);

        if (version is not null)
        {
            var set = connection.CreateCommand();
            set.Transaction = (SqliteTransaction)transaction;
            set.CommandText = """
                UPDATE runtime_installations
                SET is_current = 1
                WHERE kind = $kind AND version = $version AND deleted_at IS NULL;
                """;
            set.Parameters.AddWithValue("$kind", (int)kind);
            set.Parameters.AddWithValue("$version", version);
            if (await set.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new RuntimeNotFoundException(kind, version);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public Task MarkDeletedAsync(
        RuntimeKind kind,
        string version,
        DateTimeOffset deletedAt,
        string trashPath,
        CancellationToken cancellationToken = default) =>
        ExecuteInstallationUpdateAsync(
            """
            UPDATE runtime_installations
            SET is_current = 0, deleted_at = $deleted_at, trash_path = $trash_path
            WHERE kind = $kind AND version = $version AND deleted_at IS NULL;
            """,
            kind,
            version,
            cancellationToken,
            ("$deleted_at", deletedAt.ToString("O")),
            ("$trash_path", trashPath));

    public Task RestoreAsync(
        RuntimeKind kind,
        string version,
        string installPath,
        CancellationToken cancellationToken = default) =>
        ExecuteInstallationUpdateAsync(
            """
            UPDATE runtime_installations
            SET install_path = $install_path, deleted_at = NULL, trash_path = NULL
            WHERE kind = $kind AND version = $version AND deleted_at IS NOT NULL;
            """,
            kind,
            version,
            cancellationToken,
            ("$install_path", installPath));

    public async Task DeleteInstallationAsync(RuntimeKind kind, string version, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM runtime_installations WHERE kind = $kind AND version = $version;";
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$version", version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddOperationAsync(OperationRecord operation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO operations (id, name, kind, version, status, started_at, completed_at, error)
            VALUES ($id, $name, $kind, $version, $status, $started_at, $completed_at, $error);
            """;
        command.Parameters.AddWithValue("$id", operation.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", operation.Name);
        command.Parameters.AddWithValue("$kind", DbValue(operation.Kind is null ? null : (int)operation.Kind.Value));
        command.Parameters.AddWithValue("$version", DbValue(operation.Version));
        command.Parameters.AddWithValue("$status", (int)operation.Status);
        command.Parameters.AddWithValue("$started_at", operation.StartedAt.ToString("O"));
        command.Parameters.AddWithValue("$completed_at", DbValue(operation.CompletedAt?.ToString("O")));
        command.Parameters.AddWithValue("$error", DbValue(operation.Error));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteOperationAsync(
        Guid id,
        OperationStatus status,
        string? error = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE operations
            SET status = $status, completed_at = $completed_at, error = $error
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$completed_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$error", DbValue(error));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationRecord>> GetOperationsAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, kind, version, status, started_at, completed_at, error
            FROM operations
            ORDER BY started_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<OperationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadOperation(reader));
        }

        return result;
    }

    public async Task<OperationRecord?> FindOperationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, kind, version, status, started_at, completed_at, error
            FROM operations WHERE id = $id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadOperation(reader) : null;
    }

    private async Task ExecuteInstallationUpdateAsync(
        string sql,
        RuntimeKind kind,
        string version,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$version", version);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new RuntimeNotFoundException(kind, version);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static RuntimeInstallation ReadInstallation(SqliteDataReader reader) => new(
        (RuntimeKind)reader.GetInt32(0),
        reader.GetString(1),
        (RuntimeArchitecture)reader.GetInt32(2),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.GetBoolean(5),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static OperationRecord ReadOperation(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : (RuntimeKind)reader.GetInt32(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        (OperationStatus)reader.GetInt32(4),
        DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static void AddInstallationParameters(SqliteCommand command, RuntimeInstallation installation)
    {
        command.Parameters.AddWithValue("$kind", (int)installation.Kind);
        command.Parameters.AddWithValue("$version", installation.Version);
        command.Parameters.AddWithValue("$architecture", (int)installation.Architecture);
        command.Parameters.AddWithValue("$install_path", installation.InstallPath);
        command.Parameters.AddWithValue("$installed_at", installation.InstalledAt.ToString("O"));
        command.Parameters.AddWithValue("$is_current", installation.IsCurrent);
        command.Parameters.AddWithValue("$deleted_at", DbValue(installation.DeletedAt?.ToString("O")));
        command.Parameters.AddWithValue("$trash_path", DbValue(installation.TrashPath));
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;
}
