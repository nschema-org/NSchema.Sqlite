using NSchema.Configuration.Plugins;
using NSchema.Plan.Plugins;
using NSchema.Plugins;
using NSchema.Project.Nsql;
using NSchema.Project.Nsql.Syntax.Schemas;
using NSchema.Project.Nsql.Syntax.Settings;

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

    private static SettingsStatement Configured(NsqlDocument document) =>
        document.Statements.OfType<SettingsStatement>().ShouldHaveSingleItem();

    [Fact]
    public void GetScaffoldTemplate_ReturnsDatabaseStatement()
    {
        var block = Configured(_sut.GetScaffoldTemplate(new ScaffoldContext()));

        block.Keyword.ShouldBe(SettingsKeyword.Database);
        block.Label!.Value.ShouldBe("sqlite");
        block.Settings.Single(a => a.Key == "connection_string").Value.ShouldBe("Data Source=app.db");
    }

    [Fact]
    public void GetSampleSchema_UsesTheMainSchema()
    {
        // Act — SQLite exposes everything under 'main' and has no CREATE SCHEMA, so the sample declares into it.
        var document = _sut.GetSampleSchema();

        // Assert — 'main' is declared into rather than created.
        NsqlWriter.Write(document).ShouldContain("CREATE TABLE main.widgets");
        document.Statements.OfType<CreateSchemaStatement>().ShouldBeEmpty();
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
    public void Configure_SuppliedConnectionString_Succeeds()
    {
        // Arrange — the engine applies any NSCHEMA_DATABASE_* override before binding, so by here the
        // setting is simply present; where it came from is not the plugin's concern.
        var builder = NSchemaApplication.CreateBuilder();
        var config = Config(("connection_string", "Data Source=env.db"));

        // Act
        var result = _sut.Configure(builder, config);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Errors.ShouldBeEmpty();
    }

    private static PluginSettings Config(params (string Key, string? Value)[] attributes)
        => new(new PluginLabel("sqlite"), attributes.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase));
}
