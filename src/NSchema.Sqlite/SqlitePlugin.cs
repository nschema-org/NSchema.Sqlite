using System.ComponentModel.DataAnnotations;
using NSchema.Configuration.Plugins;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Schemas;
using NSchema.Model.Tables;
using NSchema.Plugins;
using NSchema.Project.Nsql;
using NSchema.Project.Nsql.Syntax.Settings;

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
    public NsqlDocument GetScaffoldTemplate(ScaffoldContext context) =>
        new([SettingsStatement.Database(DiagnosticSource)
            .WithSetting("connection_string", $"Data Source={context.Answer("file", "app.db")}")
            .WithDocComment(
                "A local SQLite database file. The NSCHEMA_DATABASE_CONNECTION_STRING environment\nvariable overrides the value below.")]);

    /// <inheritdoc />
    public NsqlDocument GetSampleSchema() =>
        NsqlDocument.From(
            new Database
            {
                Schemas =
                [
                    new Schema
                    {
                        Name = "main",
                        Tables =
                        {
                            new Table
                            {
                                Name = "widgets",
                                Columns =
                                {
                                    new Column { Name = "id", Type = SqlType.BigInt },
                                    new Column { Name = "name", Type = SqlType.Text, IsNullable = true },
                                },
                                PrimaryKey = new PrimaryKey { Name = "widgets_pkey", ColumnNames = ["id"] },
                            },
                        },
                    },
                ],
            },
            // 'main' always exists, so it is declared into rather than created.
            declareSchemas: false);

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
