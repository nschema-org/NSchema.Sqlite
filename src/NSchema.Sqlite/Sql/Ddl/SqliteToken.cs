namespace NSchema.Sqlite.Sql.Ddl;

internal readonly record struct SqliteToken(SqliteTokenKind Kind, string Text, bool Quoted = false)
{
    public bool IsWord(string keyword) => Kind == SqliteTokenKind.Word && !Quoted && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
}
