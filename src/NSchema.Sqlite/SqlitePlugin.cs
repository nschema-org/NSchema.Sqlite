using NSchema.Configuration;
using NSchema.Plugins;

namespace NSchema.Sqlite;

/// <summary>
/// The NSchema plugin manifest for the SQLite provider.
/// </summary>
public sealed class SqlitePlugin : INSchemaProviderPlugin
{
    private const string EnvConnectionString = "NSCHEMA_SQLITE_CONNECTION_STRING";

    /// <inheritdoc />
    public string Label => "sqlite";

    /// <inheritdoc />
    public string GetScaffoldTemplate(ScaffoldContext context)
    {
        var lines = new List<string> { "PROVIDER sqlite (" };
        if (context.Version is { } version)
        {
            lines.Add($"  version           = '{version}',");
        }

        lines.Add($"  -- A local SQLite database file. The {EnvConnectionString} environment");
        lines.Add("  -- variable overrides the value below.");
        lines.Add("  connection_string = 'Data Source=app.db'");
        lines.Add(");");
        return string.Join("\n", lines);
    }

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
    public PluginConfigureResult Configure(NSchemaApplicationBuilder builder, ConfigBlock block)
    {
        var errors = new List<string>();
        var connectionString = "";

        foreach (var (key, value) in block.Attributes)
        {
            switch (key.ToLowerInvariant())
            {
                case "connection_string":
                    connectionString = value.AsString();
                    break;
                default:
                    errors.Add($"PROVIDER sqlite: unknown attribute '{key}'.");
                    break;
            }
        }

        connectionString = Environment.GetEnvironmentVariable(EnvConnectionString) ?? connectionString;

        if (string.IsNullOrEmpty(connectionString))
        {
            errors.Add($"PROVIDER sqlite: connection_string is required. Set it via the {EnvConnectionString} environment variable or the block attribute.");
        }

        if (errors.Count > 0)
        {
            return PluginConfigureResult.Failure([.. errors]);
        }

        builder.UseSqliteSchema(connectionString);
        return PluginConfigureResult.Success;
    }
}
