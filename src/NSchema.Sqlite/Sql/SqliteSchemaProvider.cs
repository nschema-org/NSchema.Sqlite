using System.Data.Common;
using NSchema.Schema;
using NSchema.Schema.Model;
using NSchema.Schema.Model.Columns;
using NSchema.Schema.Model.Constraints;
using NSchema.Schema.Model.Indexes;
using NSchema.Schema.Model.Schemas;
using NSchema.Schema.Model.Tables;
using NSchema.Schema.Model.Views;

namespace NSchema.Sqlite.Sql;

/// <summary>
/// Reads a live Sqlite database into an NSchema <see cref="DatabaseSchema"/>.
/// </summary>
internal sealed class SqliteSchemaProvider(SqliteConnectionSource source) : ISchemaProvider
{
    private const string SchemaName = "main";

    public async ValueTask<DatabaseSchema> GetSchema(string[]? schemaNames = null, CancellationToken cancellationToken = default)
    {
        // Sqlite has one primary database, surfaced as 'main'. A scope that explicitly excludes it sees nothing.
        if (schemaNames is { Length: > 0 } && !schemaNames.Contains(SchemaName, StringComparer.OrdinalIgnoreCase))
        {
            return new DatabaseSchema([]);
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var objects = await ReadMaster(connection, cancellationToken);

        var indexesByTable = objects
            .Where(o => o is { Type: "index", Sql: not null })
            .GroupBy(o => o.TableName)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var tables = new List<Table>();
        foreach (var table in objects.Where(o => o.Type == "table").OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            tables.Add(await BuildTable(connection, table, indexesByTable.GetValueOrDefault(table.Name, []), cancellationToken));
        }

        var views = objects
            .Where(o => o is { Type: "view", Sql: not null })
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .Select(v => new View(v.Name, SqliteDdl.ExtractViewBody(v.Sql!)))
            .ToList();

        return new DatabaseSchema([new SchemaDefinition(SchemaName, Tables: tables, Views: views)]);
    }

    // ── sqlite_master ──────────────────────────────────────────────────────────

    private sealed record MasterObject(string Type, string Name, string TableName, string? Sql);

    private static async Task<List<MasterObject>> ReadMaster(DbConnection connection, CancellationToken ct)
    {
        var rows = new List<MasterObject>();
        await using var command = connection.CreateCommand();
        // Internal objects (the sqlite_* family, e.g. sqlite_sequence and auto-indexes) are not part of the
        // declared schema. Auto-indexes that back PRIMARY KEY / UNIQUE constraints also have a NULL sql and are
        // filtered out where indexes are consumed, so unique constraints surface as constraints, not indexes.
        command.CommandText = """
            SELECT type, name, tbl_name, sql
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MasterObject(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    // ── Tables ───────────────────────────────────────────────────────────────

    private static async Task<Table> BuildTable(DbConnection connection, MasterObject table, List<MasterObject> indexObjects, CancellationToken ct)
    {
        var definition = table.Sql is not null ? SqliteDdl.ParseCreateTable(table.Sql) : null;
        var generated = definition?.GeneratedExpressions ?? new Dictionary<string, string>();

        var columns = new List<Column>();
        var primaryKeyColumns = new List<(string Name, int Position)>();
        await foreach (var row in ReadColumns(connection, table.Name, ct))
        {
            columns.Add(new Column(
                row.Name,
                ParseType(row.Type),
                IsNullable: !row.NotNull,
                IsIdentity: false,
                DefaultExpression: row.Default,
                OldName: null,
                Comment: null,
                IdentityOptions: null,
                GeneratedExpression: generated.GetValueOrDefault(row.Name)));

            if (row.PrimaryKeyPosition > 0)
            {
                primaryKeyColumns.Add((row.Name, row.PrimaryKeyPosition));
            }
        }

        var primaryKey = BuildPrimaryKey(table.Name, definition, primaryKeyColumns);

        var foreignKeys = (definition?.ForeignKeys ?? [])
            .Select(fk => new ForeignKey(
                fk.Name ?? $"fk_{table.Name}_{string.Join("_", fk.Columns)}",
                fk.Columns, SchemaName, fk.ReferencedTable, fk.ReferencedColumns, fk.OnDelete, fk.OnUpdate))
            .ToList();

        var uniqueConstraints = (definition?.UniqueConstraints ?? [])
            .Select(uq => new UniqueConstraint(uq.Name ?? $"uq_{table.Name}_{string.Join("_", uq.Columns)}", uq.Columns))
            .ToList();

        var checkConstraints = (definition?.CheckConstraints ?? [])
            .Select((ck, i) => new CheckConstraint(ck.Name ?? $"ck_{table.Name}_{i + 1}", ck.Expression))
            .ToList();

        var indexes = indexObjects
            .Select(o => BuildIndex(o.Name, o.Sql!))
            .Where(idx => idx is not null)
            .Select(idx => idx!)
            .ToList();

        return new Table(
            table.Name,
            PrimaryKey: primaryKey,
            Columns: columns,
            ForeignKeys: foreignKeys,
            UniqueConstraints: uniqueConstraints,
            CheckConstraints: checkConstraints,
            Indexes: indexes);
    }

    private static PrimaryKey? BuildPrimaryKey(string tableName, SqliteTableDefinition? definition, List<(string Name, int Position)> pragmaColumns)
    {
        // The declared form is authoritative for the name and column order. PRAGMA is the fallback for a primary key
        // the parser missed (e.g. an oddly-formatted inline `INTEGER PRIMARY KEY` in an imported database).
        if (definition?.PrimaryKey is { } parsed)
        {
            return new PrimaryKey(parsed.Name ?? $"pk_{tableName}", parsed.Columns);
        }

        if (pragmaColumns.Count == 0)
        {
            return null;
        }

        var columns = pragmaColumns.OrderBy(c => c.Position).Select(c => c.Name).ToList();
        return new PrimaryKey($"pk_{tableName}", columns);
    }

    private static TableIndex? BuildIndex(string name, string sql)
    {
        var parsed = SqliteDdl.ParseCreateIndex(sql);
        return parsed is null
            ? null
            : new TableIndex(name, parsed.Columns, parsed.IsUnique, Comment: null, parsed.Predicate);
    }

    // ── Columns (PRAGMA table_xinfo) ─────────────────────────────────────────────

    private readonly record struct ColumnRow(string Name, string Type, bool NotNull, string? Default, int PrimaryKeyPosition);

    private static async IAsyncEnumerable<ColumnRow> ReadColumns(
        DbConnection connection, string tableName, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // table_xinfo includes generated columns; `hidden` is 0 for ordinary columns and 2/3 for generated ones,
        // while 1 marks a genuinely hidden (virtual-table) column, which is not part of the declared schema.
        command.CommandText = $"PRAGMA table_xinfo({QuoteLiteral(tableName)})";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var hidden = reader.GetInt32(6);
            if (hidden == 1)
            {
                continue;
            }

            yield return new ColumnRow(
                Name: reader.GetString(1),
                Type: reader.GetString(2),
                NotNull: reader.GetInt32(3) != 0,
                Default: reader.IsDBNull(4) ? null : reader.GetString(4),
                PrimaryKeyPosition: reader.GetInt32(5));
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // Sqlite stores a column's declared type verbatim; the generator writes NSchema's canonical type string, so
    // SqlType.Parse reverses it exactly. An untyped column (legal in Sqlite) maps to BLOB affinity's nearest model.
    private static SqlType ParseType(string declaredType) =>
        string.IsNullOrWhiteSpace(declaredType) ? SqlType.VarBinary() : SqlType.Parse(declaredType);

    // PRAGMA arguments cannot be parameterized, so the (trusted, database-sourced) table name is embedded as a
    // single-quoted string literal with embedded quotes doubled.
    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
