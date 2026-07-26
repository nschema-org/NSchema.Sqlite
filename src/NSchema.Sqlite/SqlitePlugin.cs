using System.ComponentModel.DataAnnotations;
using NSchema.Configuration.Plugins;
using NSchema.Plugins;
using NSchema.Project.Nsql;
using NSchema.Project.Nsql.Syntax;
using NSchema.Project.Nsql.Syntax.Settings;
using NSchema.Project.Nsql.Tokens;

namespace NSchema.Sqlite;

/// <summary>
/// The NSchema plugin manifest for the SQLite provider.
/// </summary>
public sealed class SqlitePlugin : INSchemaDatabasePlugin
{
    private const string DiagnosticSource = "sqlite";

    /// <inheritdoc />
    /// <remarks>The file, rather than the connection string it goes into — that is all SQLite needs.</remarks>
    public IReadOnlyList<ScaffoldPrompt> GetScaffoldPrompts(ScaffoldContext context) =>
    [
        new() { Key = "file", Label = "Database file", Default = "app.db" },
    ];

    /// <inheritdoc />
    public SettingsStatement GetScaffoldTemplate(ScaffoldContext context) =>
        new(SettingsKeyword.Database, Identifier.Synthetic(DiagnosticSource), new SeparatedSyntaxList<Setting>(
        [
            new Setting("connection_string", $"Data Source={context.Answer("file", "app.db")}"),
        ]))
        {
            DocComment = new Token(
                TokenKind.DocComment,
                "A local SQLite database file. The NSCHEMA_DATABASE_CONNECTION_STRING environment\nvariable overrides the value below.",
                SourcePosition.None),
        };

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
    public Result Configure(NSchemaApplicationBuilder builder, PluginSettings settings)
    {
        var bound = settings.Get<SqliteSettings>();
        if (bound.Value is not { } options)
        {
            return Result.From(bound.Diagnostics);
        }

        // The engine has already applied any NSCHEMA_DATABASE_* override, so the bound value is the final one.
        if (options.ConnectionString is not { } connectionString)
        {
            return Result.From(bound.Diagnostics);
        }

        builder.UseSqlite(connectionString);
        return Result.From(bound.Diagnostics);
    }

    /// <summary>
    /// The settings the <c>DATABASE</c> statement binds onto.
    /// </summary>
    private sealed record SqliteSettings
    {
        [Required(ErrorMessage = "DATABASE sqlite: connection_string is required. Set it in the statement, or supply NSCHEMA_DATABASE_CONNECTION_STRING.")]
        public string? ConnectionString { get; init; }
    }
}
