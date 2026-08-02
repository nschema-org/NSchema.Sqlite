using NSchema.Project.Domain.Directives;
using NSchema.Project.Policies;

namespace NSchema.Sqlite;

/// <summary>
/// Rejects a declaration of the schema SQLite provides.
/// </summary>
internal sealed class SqliteProvidedSchemaPolicy : IProjectPolicy
{
    private const string Source = "sqlite";

    internal const string Provided = "main";

    /// <inheritdoc />
    public IEnumerable<Diagnostic> Validate(ProjectDefinition project) => project.Database.Schemas
        .Where(schema => !schema.IsImplicit && schema.Name == Provided)
        .Select(schema => Diagnostic.Error(Source, "provided-schema-declared", $"SQLite provides the '{schema.Name}' schema, so it will be ignored."));
}
