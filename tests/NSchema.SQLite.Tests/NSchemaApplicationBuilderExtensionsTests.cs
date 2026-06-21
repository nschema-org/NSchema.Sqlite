using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace NSchema.SQLite.Tests;

/// <summary>
/// Covers the service registrations <see cref="NSchemaApplicationBuilderExtensions.UseSqliteSchema(NSchemaApplicationBuilder, string)"/>
/// makes. The generator/provider tests drive the SQL and introspection directly, so they never go through DI; these
/// tests guard the wiring the host (the CLI) relies on — in particular the <see cref="DbDataSource"/> the core's SQL
/// executor needs to apply a plan, whose absence made <c>apply</c> fail with "no database connection is configured".
/// </summary>
public sealed class NSchemaApplicationBuilderExtensionsTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"nschema_sqlite_{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_databasePath}";

    public void Dispose()
    {
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

    [Fact]
    public async Task UseSqliteSchema_RegistersADbDataSource_TheExecutorCanOpenConnectionsFrom()
    {
        // Arrange — wire the provider exactly as a host does.
        var builder = NSchemaApplication.CreateBuilder();
        builder.UseSqliteSchema(ConnectionString);
        await using var services = builder.Services.BuildServiceProvider();

        // Act — the core SqlExecutor resolves a DbDataSource to apply a plan; without one, apply throws.
        var dataSource = services.GetService<DbDataSource>();

        // Assert — it is registered, and it opens a real SQLite connection against the configured database.
        dataSource.ShouldNotBeNull();
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        connection.ShouldBeOfType<SqliteConnection>();
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public void UseSqliteSchema_ExposesOneConnectionSourceUnderBothFacets()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        builder.UseSqliteSchema(ConnectionString);
        using var services = builder.Services.BuildServiceProvider();

        // Act — the schema provider reads through SqliteConnectionSource; the executor writes through DbDataSource.
        var source = services.GetService<SqliteConnectionSource>();
        var dataSource = services.GetService<DbDataSource>();

        // Assert — both resolve to the single instance, so reads and writes share one connection seam.
        source.ShouldNotBeNull();
        dataSource.ShouldBeSameAs(source);
    }
}
