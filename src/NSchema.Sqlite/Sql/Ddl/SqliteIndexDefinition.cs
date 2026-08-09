using NSchema.Model.Indexes;

namespace NSchema.Sqlite.Sql.Ddl;

internal sealed record SqliteIndexDefinition(
    bool IsUnique,
    IReadOnlyList<IndexColumn> Columns,
    string? Predicate);
