using NSchema.Plugins;

namespace NSchema.Sqlite;

/// <summary>
/// The NSchema plugin manifest for the SQLite provider.
/// </summary>
public sealed class SqlitePlugin : INSchemaDatabasePlugin
{
    private const string DiagnosticSource = "sqlite";
    private const string EnvConnectionString = "NSCHEMA_SQLITE_CONNECTION_STRING";

    /// <inheritdoc />
    public string GetScaffoldTemplate(ScaffoldContext context) =>
        $"""
        DATABASE sqlite (
          -- A local SQLite database file. The {EnvConnectionString} environment
          -- variable overrides the value below.
          connection_string = 'Data Source=app.db'
        );
        """;

    /// <inheritdoc />
    public string GetSampleSchema() =>
        """
        -- SQLite surfaces every object under the single 'main' schema, so declare tables
        -- there and omit CREATE SCHEMA ('main' always exists).
        CREATE TABLE main.widgets (
          id   bigint NOT NULL,
          name text,
          CONSTRAINT widgets_pkey PRIMARY KEY (id)
        );
        """;

    /// <inheritdoc />
    public Result Configure(NSchemaApplicationBuilder builder, PluginConfig config)
    {
        var bound = config.Bind<SqliteSettings>();
        var diagnostics = bound.Diagnostics.ToList();

        var connectionString = Environment.GetEnvironmentVariable(EnvConnectionString) ?? bound.Value?.ConnectionString;
        if (string.IsNullOrEmpty(connectionString))
        {
            diagnostics.Add(Diagnostic.Error(DiagnosticSource,
                $"DATABASE sqlite: connection_string is required. Set it via the {EnvConnectionString} environment variable or the statement attribute."));
        }

        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            return Result.From(diagnostics);
        }

        builder.UseSqlite(connectionString!);
        return Result.From(diagnostics);
    }

    /// <summary>
    /// The settings the <c>DATABASE</c> statement binds onto.
    /// </summary>
    private sealed record SqliteSettings
    {
        public string? ConnectionString { get; init; }
    }
}
