using NSchema.Configuration;
using NSchema.Plugins;

namespace NSchema.Sqlite;

/// <summary>
/// The NSchema plugin manifest for the SQLite provider.
/// </summary>
public sealed class SqlitePlugin : INSchemaProviderPlugin
{
    private const string EnvConnectionString = "NSCHEMA_SQLITE_CONNECTION_STRING";

    private const string Template =
        """
        PROVIDER sqlite (
          connection_string = 'Data Source=app.db'
        );
        """;

    /// <inheritdoc />
    public string Label => "sqlite";

    /// <inheritdoc />
    public string GetScaffoldTemplate(ScaffoldContext context) => Template;

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
