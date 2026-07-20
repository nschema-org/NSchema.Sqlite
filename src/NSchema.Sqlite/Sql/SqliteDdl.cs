using NSchema.Model;
using NSchema.Model.Indexes;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;

namespace NSchema.Sqlite.Sql;

// Sqlite's PRAGMAs do not expose constraint names, check expressions, generated-column expressions, or the
// columns of an expression index. The only place those survive is the original CREATE statement, which Sqlite
// stores verbatim in `sqlite_master.sql`. This is a focused parser over that text: a small tokenizer (which
// collapses any balanced `(...)` run into a single token, so nested parens, type facets and comma-separated
// lists inside an expression never confuse the top level) plus per-statement extractors.
//
// It is deliberately tolerant — anything it cannot interpret is skipped rather than throwing — because the goal
// is to recover the author's names and expressions, not to fully validate Sqlite syntax. Column facts
// (type/nullability/default) come from PRAGMA table_xinfo instead, so this never has to parse a DEFAULT value.

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

internal readonly record struct SqliteToken(SqliteTokenKind Kind, string Text, bool Quoted = false)
{
    public bool IsWord(string keyword) => Kind == SqliteTokenKind.Word && !Quoted && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
}

internal sealed record ParsedPrimaryKey(string? Name, IReadOnlyList<string> Columns);

internal sealed record ParsedForeignKey(
    string? Name,
    IReadOnlyList<string> Columns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    ReferentialAction OnDelete,
    ReferentialAction OnUpdate);

internal sealed record ParsedUnique(string? Name, IReadOnlyList<string> Columns);

internal sealed record ParsedCheck(string? Name, string Expression);

internal sealed record SqliteTableDefinition(
    ParsedPrimaryKey? PrimaryKey,
    IReadOnlyList<ParsedForeignKey> ForeignKeys,
    IReadOnlyList<ParsedUnique> UniqueConstraints,
    IReadOnlyList<ParsedCheck> CheckConstraints,
    IReadOnlyDictionary<string, string> GeneratedExpressions);

internal sealed record SqliteIndexDefinition(
    bool IsUnique,
    IReadOnlyList<IndexColumn> Columns,
    string? Predicate);

internal sealed record ParsedTrigger(
    TriggerTiming Timing,
    TriggerEvent Events,
    IReadOnlyList<string> UpdateOfColumns,
    bool ForEachRow,
    string? When,
    string Body);

internal static class SqliteDdl
{
    // ── Tokenizer ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tokenizes a span of SQL. A balanced <c>(...)</c> run becomes one <see cref="SqliteTokenKind.Parens"/> token
    /// (its inner text); identifiers in any of Sqlite's quoting styles (<c>"" `` []</c>) become unquoted
    /// <see cref="SqliteTokenKind.Word"/> tokens flagged <c>Quoted</c>; string literals are kept raw.
    /// </summary>
    public static List<SqliteToken> Tokenize(string sql)
    {
        var tokens = new List<SqliteToken>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '-' when i + 1 < sql.Length && sql[i + 1] == '-':
                    while (i < sql.Length && sql[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                case '/' when i + 1 < sql.Length && sql[i + 1] == '*':
                    i += 2;
                    while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                    {
                        i++;
                    }

                    i += 2;
                    continue;
                case '(':
                    tokens.Add(new SqliteToken(SqliteTokenKind.Parens, ReadBalancedParens(sql, ref i)));
                    continue;
                case '"' or '`':
                    tokens.Add(new SqliteToken(SqliteTokenKind.Word, ReadDelimited(sql, ref i, c, c), Quoted: true));
                    continue;
                case '[':
                    tokens.Add(new SqliteToken(SqliteTokenKind.Word, ReadDelimited(sql, ref i, '[', ']'), Quoted: true));
                    continue;
                case '\'':
                    tokens.Add(new SqliteToken(SqliteTokenKind.String, ReadStringLiteral(sql, ref i)));
                    continue;
            }

            if (IsWordChar(c))
            {
                var start = i;
                while (i < sql.Length && IsWordChar(sql[i]))
                {
                    i++;
                }

                tokens.Add(new SqliteToken(SqliteTokenKind.Word, sql[start..i]));
                continue;
            }

            tokens.Add(new SqliteToken(SqliteTokenKind.Symbol, c.ToString()));
            i++;
        }

        return tokens;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    // Reads the balanced run starting at the '(' under the cursor and returns the text between the outer
    // parentheses, leaving the cursor just past the matching ')'. Quotes and string literals inside are honoured
    // so that a ')' or '(' within them does not affect depth.
    private static string ReadBalancedParens(string sql, ref int i)
    {
        var depth = 0;
        var start = i + 1;
        while (i < sql.Length)
        {
            var c = sql[i];
            switch (c)
            {
                case '\'':
                    ReadStringLiteral(sql, ref i);
                    continue;
                case '"' or '`':
                    ReadDelimited(sql, ref i, c, c);
                    continue;
                case '[':
                    ReadDelimited(sql, ref i, '[', ']');
                    continue;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        var inner = sql[start..i];
                        i++;
                        return inner;
                    }
                    break;
            }

            i++;
        }

        return sql[start..]; // unbalanced; return the rest tolerantly
    }

    private static string ReadDelimited(string sql, ref int i, char open, char close)
    {
        i++; // opening delimiter
        var sb = new System.Text.StringBuilder();
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == close)
            {
                // A doubled closing delimiter is an escaped delimiter (Sqlite allows "" and ]] in this position).
                if (i + 1 < sql.Length && sql[i + 1] == close && open == close)
                {
                    sb.Append(close);
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string ReadStringLiteral(string sql, ref int i)
    {
        var start = i;
        i++; // opening quote
        while (i < sql.Length)
        {
            if (sql[i] == '\'')
            {
                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    i += 2;
                    continue;
                }

                i++;
                break;
            }

            i++;
        }

        return sql[start..i];
    }

    /// <summary>Splits a token list on top-level commas (nested parens are already single tokens).</summary>
    private static List<List<SqliteToken>> SplitOnCommas(List<SqliteToken> tokens)
    {
        var items = new List<List<SqliteToken>>();
        var current = new List<SqliteToken>();
        foreach (var token in tokens)
        {
            if (token is { Kind: SqliteTokenKind.Symbol, Text: "," })
            {
                items.Add(current);
                current = [];
            }
            else
            {
                current.Add(token);
            }
        }

        if (current.Count > 0)
        {
            items.Add(current);
        }

        return items;
    }

    // ── CREATE TABLE ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the constraint and generated-column information out of a <c>CREATE TABLE</c> statement. Returns
    /// <see langword="null"/> when no table body is found.
    /// </summary>
    public static SqliteTableDefinition? ParseCreateTable(string sql)
    {
        var tokens = Tokenize(sql);
        var bodyIndex = tokens.FindIndex(t => t.Kind == SqliteTokenKind.Parens);
        if (bodyIndex < 0)
        {
            return null;
        }

        ParsedPrimaryKey? primaryKey = null;
        var foreignKeys = new List<ParsedForeignKey>();
        var uniques = new List<ParsedUnique>();
        var checks = new List<ParsedCheck>();
        var generated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in SplitOnCommas(Tokenize(tokens[bodyIndex].Text)))
        {
            if (item.Count == 0)
            {
                continue;
            }

            // A named constraint: CONSTRAINT <name> <kind> … . Strip the prefix and remember the name.
            string? name = null;
            var rest = item;
            if (item[0].IsWord("CONSTRAINT") && item.Count >= 2)
            {
                name = item[1].Text;
                rest = item.GetRange(2, item.Count - 2);
            }

            if (rest.Count == 0)
            {
                continue;
            }

            if (rest[0].IsWord("PRIMARY"))
            {
                if (FirstParens(rest) is { } columns)
                {
                    primaryKey = new ParsedPrimaryKey(name, ParseColumnList(columns));
                }
            }
            else if (rest[0].IsWord("UNIQUE"))
            {
                if (FirstParens(rest) is { } columns)
                {
                    uniques.Add(new ParsedUnique(name, ParseColumnList(columns)));
                }
            }
            else if (rest[0].IsWord("CHECK"))
            {
                if (FirstParens(rest) is { } expr)
                {
                    checks.Add(new ParsedCheck(name, expr.Trim()));
                }
            }
            else if (rest[0].IsWord("FOREIGN"))
            {
                if (ParseForeignKey(name, rest) is { } fk)
                {
                    foreignKeys.Add(fk);
                }
            }
            else if (!IsConstraintKeyword(rest[0]))
            {
                // A column definition. The only thing PRAGMA cannot give us is the generated-column expression and
                // any column-level constraints (rare in NSchema-emitted SQL, but honoured for imported databases).
                ParseColumnItem(rest, ref primaryKey, foreignKeys, uniques, checks, generated);
            }
        }

        return new SqliteTableDefinition(primaryKey, foreignKeys, uniques, checks, generated);
    }

    private static void ParseColumnItem(
        List<SqliteToken> item,
        ref ParsedPrimaryKey? primaryKey,
        List<ParsedForeignKey> foreignKeys,
        List<ParsedUnique> uniques,
        List<ParsedCheck> checks,
        Dictionary<string, string> generated)
    {
        var columnName = item[0].Text;
        for (var i = 1; i < item.Count; i++)
        {
            if (item[i].IsWord("GENERATED") || (item[i].IsWord("AS") && item[i] is { Quoted: false }))
            {
                // GENERATED ALWAYS AS (expr) [STORED|VIRTUAL], or the shorthand `AS (expr)`.
                var parens = item.Skip(i).FirstOrDefault(t => t.Kind == SqliteTokenKind.Parens);
                if (parens.Kind == SqliteTokenKind.Parens)
                {
                    generated[columnName] = parens.Text.Trim();
                }
            }
            else if (item[i].IsWord("PRIMARY"))
            {
                primaryKey ??= new ParsedPrimaryKey(null, [columnName]);
            }
            else if (item[i].IsWord("UNIQUE"))
            {
                uniques.Add(new ParsedUnique(null, [columnName]));
            }
            else if (item[i].IsWord("CHECK") && i + 1 < item.Count && item[i + 1].Kind == SqliteTokenKind.Parens)
            {
                checks.Add(new ParsedCheck(null, item[i + 1].Text.Trim()));
            }
            else if (item[i].IsWord("REFERENCES"))
            {
                if (ParseColumnReferences(columnName, item, i) is { } fk)
                {
                    foreignKeys.Add(fk);
                }
            }
        }
    }

    private static ParsedForeignKey? ParseForeignKey(string? name, List<SqliteToken> rest)
    {
        // FOREIGN KEY (cols) REFERENCES table (refCols) [ON DELETE ..] [ON UPDATE ..]
        var localColumns = FirstParens(rest);
        var referencesIndex = rest.FindIndex(t => t.IsWord("REFERENCES"));
        if (localColumns is null || referencesIndex < 0)
        {
            return null;
        }

        return BuildForeignKey(name, ParseColumnList(localColumns), rest, referencesIndex);
    }

    private static ParsedForeignKey? ParseColumnReferences(string columnName, List<SqliteToken> item, int referencesIndex) =>
        BuildForeignKey(null, [columnName], item, referencesIndex);

    private static ParsedForeignKey? BuildForeignKey(string? name, IReadOnlyList<string> localColumns, List<SqliteToken> tokens, int referencesIndex)
    {
        // The referenced table follows REFERENCES; a schema-qualified name (schema.table) keeps the table part.
        var cursor = referencesIndex + 1;
        if (cursor >= tokens.Count || tokens[cursor].Kind != SqliteTokenKind.Word)
        {
            return null;
        }

        var referencedTable = tokens[cursor].Text;
        cursor++;
        if (cursor + 1 < tokens.Count && tokens[cursor] is { Kind: SqliteTokenKind.Symbol, Text: "." })
        {
            referencedTable = tokens[cursor + 1].Text;
            cursor += 2;
        }

        var referencedColumns = cursor < tokens.Count && tokens[cursor].Kind == SqliteTokenKind.Parens
            ? ParseColumnList(tokens[cursor].Text)
            : [];

        var (onDelete, onUpdate) = ParseReferentialActions(tokens, cursor);
        return new ParsedForeignKey(name, localColumns, referencedTable, referencedColumns, onDelete, onUpdate);
    }

    private static (ReferentialAction OnDelete, ReferentialAction OnUpdate) ParseReferentialActions(List<SqliteToken> tokens, int from)
    {
        var onDelete = ReferentialAction.NoAction;
        var onUpdate = ReferentialAction.NoAction;
        for (var i = from; i < tokens.Count; i++)
        {
            if (!tokens[i].IsWord("ON") || i + 1 >= tokens.Count)
            {
                continue;
            }

            var action = ParseAction(tokens, i + 2);
            if (tokens[i + 1].IsWord("DELETE"))
            {
                onDelete = action;
            }
            else if (tokens[i + 1].IsWord("UPDATE"))
            {
                onUpdate = action;
            }
        }

        return (onDelete, onUpdate);
    }

    private static ReferentialAction ParseAction(List<SqliteToken> tokens, int i)
    {
        if (i >= tokens.Count)
        {
            return ReferentialAction.NoAction;
        }

        if (tokens[i].IsWord("CASCADE"))
        {
            return ReferentialAction.Cascade;
        }

        if (tokens[i].IsWord("SET") && i + 1 < tokens.Count)
        {
            return tokens[i + 1].IsWord("NULL") ? ReferentialAction.SetNull
                : tokens[i + 1].IsWord("DEFAULT") ? ReferentialAction.SetDefault
                : ReferentialAction.NoAction;
        }

        return ReferentialAction.NoAction; // NO ACTION, RESTRICT
    }

    // ── CREATE INDEX ──────────────────────────────────────────────────────────

    /// <summary>Parses a <c>CREATE INDEX</c> statement, returning <see langword="null"/> on anything unexpected.</summary>
    public static SqliteIndexDefinition? ParseCreateIndex(string sql)
    {
        var tokens = Tokenize(sql);
        var isUnique = tokens.Any(t => t.IsWord("UNIQUE"));
        var onIndex = tokens.FindIndex(t => t.IsWord("ON"));
        if (onIndex < 0)
        {
            return null;
        }

        // CREATE [UNIQUE] INDEX name ON table (columns) [WHERE predicate]
        var columnsToken = tokens.Skip(onIndex).FirstOrDefault(t => t.Kind == SqliteTokenKind.Parens);
        if (columnsToken.Kind != SqliteTokenKind.Parens)
        {
            return null;
        }

        var columns = ParseIndexColumns(columnsToken.Text);

        string? predicate = null;
        var whereIndex = tokens.FindIndex(onIndex, t => t.IsWord("WHERE"));
        if (whereIndex >= 0)
        {
            predicate = ExtractAfterKeyword(sql, "WHERE");
        }

        return new SqliteIndexDefinition(isUnique, columns, predicate);
    }

    private static List<IndexColumn> ParseIndexColumns(string inner)
    {
        var columns = new List<IndexColumn>();
        foreach (var item in SplitOnCommas(Tokenize(inner)))
        {
            if (item.Count == 0)
            {
                continue;
            }

            var sort = item.Any(t => t.IsWord("DESC")) ? IndexSort.Descending
                : item.Any(t => t.IsWord("ASC")) ? IndexSort.Ascending
                : IndexSort.Default;

            // A parenthesised key (or anything that is not a single bare/quoted identifier) is an expression.
            if (item[0].Kind == SqliteTokenKind.Parens)
            {
                columns.Add(new IndexColumn(Expression: new SqlText(item[0].Text.Trim()), Sort: sort));
            }
            else
            {
                columns.Add(new IndexColumn(new SqlIdentifier(item[0].Text), Sort: sort));
            }
        }

        return columns;
    }

    // ── CREATE VIEW ─────────────────────────────────────────────────────────────

    /// <summary>Extracts a view's body — everything after the first top-level <c>AS</c> — or the input if none is found.</summary>
    public static string ExtractViewBody(string sql) => ExtractAfterKeyword(sql, "AS") ?? sql.Trim();

    // ── CREATE TRIGGER ────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a <c>CREATE TRIGGER</c> statement (the verbatim text Sqlite keeps in <c>sqlite_master.sql</c>) into its
    /// timing, event, optional <c>WHEN</c> condition, and inline <c>BEGIN … END</c> body. Returns <see langword="null"/>
    /// on anything that does not look like a trigger. Like the rest of this parser it is deliberately tolerant; the
    /// trigger's name and table come from <c>sqlite_master</c>, so only the parts PRAGMAs can't supply are recovered here.
    /// </summary>
    public static ParsedTrigger? ParseCreateTrigger(string sql)
    {
        // The body is the top-level BEGIN … END; everything before it is the header (timing / event / WHEN).
        var beginIndex = TopLevelWordIndex(sql, "BEGIN");
        if (beginIndex < 0)
        {
            return null;
        }

        var body = sql[beginIndex..].Trim();
        var header = sql[..beginIndex];

        // A WHEN condition (if present) runs from the WHEN keyword to the body; lift it out of the header so the
        // event scan below doesn't see anything inside it.
        string? when = null;
        var whenIndex = TopLevelWordIndex(header, "WHEN");
        if (whenIndex >= 0)
        {
            when = StripEnclosingParens(header[(whenIndex + "WHEN".Length)..].Trim());
            header = header[..whenIndex];
        }

        var tokens = Tokenize(header);
        var (events, updateOfColumns) = ParseTriggerEvents(tokens);
        if (events == TriggerEvent.None)
        {
            return null;
        }

        return new ParsedTrigger(ParseTriggerTiming(tokens), events, updateOfColumns,
            ContainsSequence(tokens, "FOR", "EACH", "ROW"), when, body);
    }

    // Sqlite's default when no timing keyword is present is BEFORE; an NSchema-generated trigger always states one.
    private static TriggerTiming ParseTriggerTiming(List<SqliteToken> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].IsWord("AFTER"))
            {
                return TriggerTiming.After;
            }

            if (tokens[i].IsWord("INSTEAD") && i + 1 < tokens.Count && tokens[i + 1].IsWord("OF"))
            {
                return TriggerTiming.InsteadOf;
            }

            if (tokens[i].IsWord("BEFORE"))
            {
                return TriggerTiming.Before;
            }
        }

        return TriggerTiming.Before;
    }

    // A Sqlite trigger fires on exactly one event. `UPDATE OF a, b` lists the columns (unparenthesised) up to ON.
    private static (TriggerEvent Events, IReadOnlyList<string> UpdateOfColumns) ParseTriggerEvents(List<SqliteToken> tokens)
    {
        var updateOf = new List<string>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].IsWord("INSERT"))
            {
                return (TriggerEvent.Insert, updateOf);
            }

            if (tokens[i].IsWord("DELETE"))
            {
                return (TriggerEvent.Delete, updateOf);
            }

            if (tokens[i].IsWord("UPDATE"))
            {
                if (i + 1 < tokens.Count && tokens[i + 1].IsWord("OF"))
                {
                    for (var j = i + 2; j < tokens.Count && !tokens[j].IsWord("ON"); j++)
                    {
                        if (tokens[j].Kind == SqliteTokenKind.Word)
                        {
                            updateOf.Add(tokens[j].Text);
                        }
                    }
                }

                return (TriggerEvent.Update, updateOf);
            }
        }

        return (TriggerEvent.None, updateOf);
    }

    private static bool ContainsSequence(List<SqliteToken> tokens, params string[] words)
    {
        for (var i = 0; i + words.Length <= tokens.Count; i++)
        {
            var matched = true;
            for (var j = 0; j < words.Length; j++)
            {
                if (!tokens[i + j].IsWord(words[j]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    // ── Shared helpers ──────────────────────────────────────────────────────────

    // Returns the verbatim source text following the first top-level occurrence of the keyword, trimmed.
    private static string? ExtractAfterKeyword(string sql, string keyword)
    {
        var index = TopLevelWordIndex(sql, keyword);
        return index < 0 ? null : sql[(index + keyword.Length)..].Trim();
    }

    // Returns the start offset of the first top-level occurrence of the keyword (case-insensitive, ignoring matches
    // inside quotes/strings/parens), or -1. Used to split a view body, an index predicate, and a trigger header/body.
    private static int TopLevelWordIndex(string sql, string keyword)
    {
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            switch (c)
            {
                case '\'':
                    ReadStringLiteral(sql, ref i);
                    continue;
                case '"' or '`':
                    ReadDelimited(sql, ref i, c, c);
                    continue;
                case '[':
                    ReadDelimited(sql, ref i, '[', ']');
                    continue;
                case '(':
                    ReadBalancedParens(sql, ref i);
                    continue;
            }

            if (IsWordChar(c))
            {
                var start = i;
                while (i < sql.Length && IsWordChar(sql[i]))
                {
                    i++;
                }

                if (string.Equals(sql[start..i], keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return start;
                }

                continue;
            }

            i++;
        }

        return -1;
    }

    // Strips every parenthesis pair that fully encloses the whole expression (Sqlite's WHEN is captured paren-stripped,
    // so a generated `WHEN (expr)` round-trips back to `expr`). Inner grouping parens are left intact.
    private static string? StripEnclosingParens(string value)
    {
        value = value.Trim();
        while (value.Length >= 2 && value[0] == '(' && Encloses(value))
        {
            value = value[1..^1].Trim();
        }

        return value.Length == 0 ? null : value;
    }

    private static bool Encloses(string value)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            depth += value[i] switch { '(' => 1, ')' => -1, _ => 0 };
            if (depth == 0)
            {
                return i == value.Length - 1;
            }
        }

        return false;
    }

    private static bool IsConstraintKeyword(SqliteToken token) =>
        token.IsWord("PRIMARY") || token.IsWord("FOREIGN") || token.IsWord("UNIQUE") || token.IsWord("CHECK");

    /// <summary>Returns the inner text of the first parenthesised group in the token list, or null if there is none.</summary>
    private static string? FirstParens(List<SqliteToken> tokens)
    {
        foreach (var token in tokens)
        {
            if (token.Kind == SqliteTokenKind.Parens)
            {
                return token.Text;
            }
        }

        return null;
    }

    /// <summary>Parses a parenthesised column list's inner text into bare column names (dropping COLLATE/ASC/DESC).</summary>
    private static List<string> ParseColumnList(string inner)
    {
        var names = new List<string>();
        foreach (var item in SplitOnCommas(Tokenize(inner)))
        {
            if (item.Count > 0 && item[0].Kind == SqliteTokenKind.Word)
            {
                names.Add(item[0].Text);
            }
        }

        return names;
    }
}
