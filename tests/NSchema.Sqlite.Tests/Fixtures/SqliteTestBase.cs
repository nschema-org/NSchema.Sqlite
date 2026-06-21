using Microsoft.Data.Sqlite;
using NSchema.Plan.Model;
using NSchema.Schema.Model;
using NSchema.Sqlite.Sql;

namespace NSchema.Sqlite.Tests.Fixtures;

/// <summary>
/// Base for tests that exercise the generator and provider against a real Sqlite database. Sqlite is in-process,
/// so each test gets its own private temp-file database (no Docker, no container) and the generated DDL is run
/// against it directly. A temp file — rather than <c>:memory:</c> — is used so the provider, which opens its own
/// connection, sees the same database.
/// </summary>
public abstract class SqliteTestBase : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"nschema_sqlite_{Guid.NewGuid():N}.db");
    private SqliteConnection _connection = null!;

    private protected string ConnectionString => $"Data Source={_databasePath}";
    private protected SqliteSqlGenerator Generator { get; } = new();

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection(ConnectionString);
        await _connection.OpenAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A lingering OS lock on Windows is harmless — the temp file is collected eventually.
        }
    }

    /// <summary>Generates SQL for the actions and runs every statement (each on its own connection, as the executor would).</summary>
    private protected async Task Apply(params MigrationAction[] actions)
    {
        var plan = Generator.Generate(new MigrationPlan(actions, [], []));
        foreach (var statement in plan.Statements)
        {
            await Exec(statement.Sql);
        }
    }

    /// <summary>Introspects the live database through the provider, scoped to the <c>main</c> schema.</summary>
    private protected async Task<DatabaseSchema> Introspect()
    {
        var provider = new SqliteSchemaProvider(new SqliteConnectionSource(ConnectionString));
        return await provider.GetSchema(["main"], TestContext.Current.CancellationToken);
    }

    private protected async Task Exec(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private protected async Task<T> Scalar<T>(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (T)Convert.ChangeType(result, typeof(T))!;
    }
}
