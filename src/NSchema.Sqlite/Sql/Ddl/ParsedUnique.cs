namespace NSchema.Sqlite.Sql.Ddl;

internal sealed record ParsedUnique(string? Name, IReadOnlyList<string> Columns);
