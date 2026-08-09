namespace NSchema.Sqlite.Sql.Ddl;

internal sealed record SqliteTableDefinition(
    ParsedPrimaryKey? PrimaryKey,
    IReadOnlyList<ParsedForeignKey> ForeignKeys,
    IReadOnlyList<ParsedUnique> UniqueConstraints,
    IReadOnlyList<ParsedCheck> CheckConstraints,
    IReadOnlyDictionary<string, string> GeneratedExpressions);
