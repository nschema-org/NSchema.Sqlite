namespace NSchema.Sqlite.Sql.Ddl;

internal enum SqliteTokenKind
{
    /// <summary>
    /// A bareword (keyword, number, or unquoted identifier) or an unquoted-here quoted identifier.
    /// </summary>
    Word,

    /// <summary>
    /// A single-quoted string literal, captured raw including its quotes.
    /// </summary>
    String,

    /// <summary>
    /// A balanced parenthesised run, captured as its inner text (without the outer parentheses).
    /// </summary>
    Parens,

    /// <summary>
    /// A single punctuation character (comma, dot, operator, …).
    /// </summary>
    Symbol,
}