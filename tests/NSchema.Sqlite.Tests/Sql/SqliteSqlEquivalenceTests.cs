using Microsoft.Extensions.DependencyInjection;
using NSchema.Diff.Plugins;
using NSchema.Sqlite.Sql;

namespace NSchema.Sqlite.Tests.Sql;

public sealed class SqliteSqlEquivalenceTests
{
    [Fact]
    public void ValidatesTypeNames_IsFalse()
        // SQLite accepts any type name, so type-reachability has nothing to check.
        => new SqliteSqlEquivalence().ValidatesTypeNames.ShouldBeFalse();

    [Fact]
    public async Task UseSqlite_RegistersTheEquivalence()
    {
        // Arrange — wire the provider exactly as a host does.
        var builder = NSchemaApplication.CreateBuilder();
        builder.UseSqlite("Data Source=:memory:");
        await using var services = builder.Services.BuildServiceProvider();

        // Act
        var equivalence = services.GetService<SqlEquivalence>();

        // Assert
        equivalence.ShouldBeOfType<SqliteSqlEquivalence>();
    }
}
