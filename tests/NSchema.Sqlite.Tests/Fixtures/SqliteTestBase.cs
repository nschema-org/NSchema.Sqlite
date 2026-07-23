using Microsoft.Data.Sqlite;
using NSchema.Model;
using NSchema.Plan.Model;
using NSchema.Sqlite.Sql;

namespace NSchema.Sqlite.Tests.Fixtures;

/// <summary>
/// Base for tests that exercise the dialect and introspector against a real Sqlite database. Sqlite is in-process,
/// so each test gets its own private temp-file database (no Docker, no container) and the generated DDL is run
/// against it directly. A temp file — rather than <c>:memory:</c> — is used so the introspector, which opens its own
/// connection, sees the same database.
/// </summary>
public abstract class SqliteTestBase : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"nschema_sqlite_{Guid.NewGuid():N}.db");
    private SqliteConnection _connection = null!;

    private protected string ConnectionString => $"Data Source={_databasePath}";
    private protected SqliteSqlDialect Dialect { get; } = new();

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

    /// <summary>Renders each action through the dialect and runs every statement (each on its own command, as the executor would).</summary>
    private protected async Task Apply(params MigrationAction[] actions)
    {
        foreach (var action in actions)
        {
            var rendered = Dialect.Generate(action);
            rendered.IsSuccess.ShouldBeTrue(string.Join("; ", rendered.Diagnostics.Select(d => d.Message)));
            foreach (var statement in rendered.Require())
            {
                await Exec(statement.Sql.Value);
            }
        }
    }

    /// <summary>Introspects the live database through the provider, scoped to the <c>main</c> schema.</summary>
    private protected async Task<Database> Introspect()
    {
        var introspector = new SqliteDatabaseIntrospector(new SqliteConnectionSource(ConnectionString));
        return await introspector.GetDatabase(PlanningScope.To(new SchemaAddress("main")), TestContext.Current.CancellationToken);
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
