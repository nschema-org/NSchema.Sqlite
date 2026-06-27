using NSchema.Configuration;
using NSchema.Plugins;
using NSchema.Sql;

namespace NSchema.Sqlite.Tests;

/// <summary>
/// Pins <see cref="SqlitePlugin"/>'s block parsing, environment-override precedence, and validation. Pure
/// unit tests — no Docker. The <c>NSCHEMA_SQLITE_CONNECTION_STRING</c> variable is snapshotted and cleared so
/// a developer's ambient environment cannot make the outcome non-deterministic.
/// </summary>
public sealed class SqlitePluginTests : IDisposable
{
    private const string EnvConnectionString = "NSCHEMA_SQLITE_CONNECTION_STRING";

    private readonly string? _savedEnv = Environment.GetEnvironmentVariable(EnvConnectionString);
    private readonly SqlitePlugin _sut = new();

    public SqlitePluginTests() => Environment.SetEnvironmentVariable(EnvConnectionString, null);

    public void Dispose() => Environment.SetEnvironmentVariable(EnvConnectionString, _savedEnv);

    [Fact]
    public void Label_IsSqlite() => _sut.Label.ShouldBe("sqlite");

    [Fact]
    public void GetScaffoldTemplate_ReturnsProviderBlock()
        => _sut.GetScaffoldTemplate(new ScaffoldContext()).ShouldContain("PROVIDER sqlite");

    [Fact]
    public void GetScaffoldTemplate_WithVersion_PinsIt()
        => _sut.GetScaffoldTemplate(new ScaffoldContext { Version = "9.9.9" }).ShouldContain("version           = '9.9.9',");

    [Fact]
    public void GetSampleSchema_UsesTheMainSchema()
    {
        // SQLite exposes everything under 'main' and has no CREATE SCHEMA — the sample declares its table there.
        var schema = _sut.GetSampleSchema();

        schema.ShouldContain("CREATE TABLE main.widgets");
    }

    [Fact]
    public void Configure_ValidConnectionString_SucceedsAndRegistersProvider()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block(("connection_string", ConfigValue.OfString("Data Source=app.db")));

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        builder.Services.ShouldContain(d => d.ServiceType == typeof(ISqlGenerator));
    }

    [Fact]
    public void Configure_MissingConnectionString_FailsWithRequiredError()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block();

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("connection_string is required"));
    }

    [Fact]
    public void Configure_UnknownAttribute_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block(
            ("connection_string", ConfigValue.OfString("Data Source=app.db")),
            ("nonsense", ConfigValue.OfString("x")));

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.Contains("unknown attribute 'nonsense'"));
    }

    [Fact]
    public void Configure_EnvironmentConnectionString_SatisfiesOmittedBlockAttribute()
    {
        // Arrange
        Environment.SetEnvironmentVariable(EnvConnectionString, "Data Source=env.db");
        var builder = NSchemaApplication.CreateBuilder();
        var block = Block();

        // Act
        var result = _sut.Configure(builder, block);

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    private static ConfigBlock Block(params (string Key, ConfigValue Value)[] attributes)
        => new("provider", "sqlite", attributes.ToDictionary(a => a.Key, a => a.Value));
}
