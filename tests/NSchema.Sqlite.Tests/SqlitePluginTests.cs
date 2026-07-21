using NSchema.Configuration.Plugins;
using NSchema.Plan.Backends;
using NSchema.Plugins;

namespace NSchema.Sqlite.Tests;

/// <summary>
/// Pins <see cref="SqlitePlugin"/>'s configuration binding, environment-override precedence, and validation. Pure
/// unit tests — no Docker. The <c>NSCHEMA_SQLITE_CONNECTION_STRING</c> variable is snapshotted and cleared so
/// a developer's ambient environment cannot make the outcome non-deterministic.
/// </summary>
[Collection("sqlite-environment")]
public sealed class SqlitePluginTests : IDisposable
{
    private const string EnvConnectionString = "NSCHEMA_SQLITE_CONNECTION_STRING";

    private readonly string? _savedEnv = Environment.GetEnvironmentVariable(EnvConnectionString);
    private readonly SqlitePlugin _sut = new();

    public SqlitePluginTests() => Environment.SetEnvironmentVariable(EnvConnectionString, null);

    public void Dispose() => Environment.SetEnvironmentVariable(EnvConnectionString, _savedEnv);

    [Fact]
    public void GetScaffoldTemplate_ReturnsDatabaseStatement()
        => _sut.GetScaffoldTemplate(new ScaffoldContext()).ShouldContain("DATABASE sqlite");

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
        var config = Config(("connection_string", "Data Source=app.db"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
        builder.Services.ShouldContain(d => d.ServiceType == typeof(SqlDialect));
    }

    [Fact]
    public void Configure_MissingConnectionString_FailsWithRequiredError()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config();

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("connection_string is required"));
    }

    [Fact]
    public void Configure_UnknownAttribute_Fails()
    {
        // Arrange
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(
            ("connection_string", "Data Source=app.db"),
            ("nonsense", "x"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsFailure.ShouldBeTrue();
        result.Errors.ShouldContain(e => e.Message.Contains("nonsense", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Configure_EnvironmentConnectionString_SatisfiesOmittedAttribute()
    {
        // Arrange
        Environment.SetEnvironmentVariable(EnvConnectionString, "Data Source=env.db");
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config();

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    private static PluginSettings Config(params (string Key, string? Value)[] attributes)
        => new(new PluginLabel("sqlite"), attributes.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));
}
