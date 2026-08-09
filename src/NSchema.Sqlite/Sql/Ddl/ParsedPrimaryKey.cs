namespace NSchema.Sqlite.Sql.Ddl;

internal sealed record ParsedPrimaryKey(string? Name, IReadOnlyList<string> Columns);
