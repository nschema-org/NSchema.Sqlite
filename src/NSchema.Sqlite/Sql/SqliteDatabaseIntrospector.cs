using System.Data.Common;
using NSchema.Deployment.Backends;
using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Constraints;
using NSchema.Model.Indexes;
using NSchema.Model.Schemas;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;
using NSchema.Model.Views;

namespace NSchema.Sqlite.Sql;

/// <summary>
/// Reads a live Sqlite database into an NSchema <see cref="Database"/>.
/// </summary>
internal sealed class SqliteDatabaseIntrospector(SqliteConnectionSource source) : IDatabaseIntrospector
{
    private const string SchemaName = "main";

    public async ValueTask<Database> GetDatabase(PlanningScope scope, CancellationToken cancellationToken = default)
    {
        // Sqlite has one primary database, surfaced as 'main'. A scope that explicitly excludes it sees nothing.
        // (The scope is an optimization hint, so the case-insensitive match may over-return; the engine re-applies
        // the scope after every read.)
        if (!scope.IsUnscoped && !scope.SchemaNames.Any(s => string.Equals(s.Value, SchemaName, StringComparison.OrdinalIgnoreCase)))
        {
            return new Database();
        }

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        var objects = await ReadMaster(connection, cancellationToken);

        var indexesByTable = objects
            .Where(o => o is { Type: "index", Sql: not null })
            .GroupBy(o => o.TableName)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var triggersByTable = objects
            .Where(o => o is { Type: "trigger", Sql: not null })
            .GroupBy(o => o.TableName)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var tables = new List<Table>();
        foreach (var table in objects.Where(o => o.Type == "table").OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            tables.Add(await BuildTable(connection, table,
                indexesByTable.GetValueOrDefault(table.Name, []),
                triggersByTable.GetValueOrDefault(table.Name, []),
                cancellationToken));
        }

        var views = objects
            .Where(o => o is { Type: "view", Sql: not null })
            .OrderBy(o => o.Name, StringComparer.Ordinal)
            .Select(v => new View { Name = v.Name, Body = SqliteDdl.ExtractViewBody(v.Sql!) })
            .ToList();

        return new Database
        {
            Schemas = [new Schema { Name = SchemaName, Tables = [.. tables], Views = [.. views] }],
        };
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

    private static async Task<Table> BuildTable(DbConnection connection, MasterObject table, List<MasterObject> indexObjects, List<MasterObject> triggerObjects, CancellationToken ct)
    {
        var definition = table.Sql is not null ? SqliteDdl.ParseCreateTable(table.Sql) : null;
        var generated = definition?.GeneratedExpressions ?? new Dictionary<string, string>();

        var columns = new List<Column>();
        var primaryKeyColumns = new List<(string Name, int Position)>();
        await foreach (var row in ReadColumns(connection, table.Name, ct))
        {
            columns.Add(new Column
            {
                Name = row.Name,
                Type = ParseType(row.Type),
                IsNullable = !row.NotNull,
                DefaultExpression = row.Default,
                GeneratedExpression = generated.GetValueOrDefault(row.Name),
            });

            if (row.PrimaryKeyPosition > 0)
            {
                primaryKeyColumns.Add((row.Name, row.PrimaryKeyPosition));
            }
        }

        var foreignKeys = (definition?.ForeignKeys ?? [])
            .Select(fk => new ForeignKey
            {
                Name = fk.Name ?? $"fk_{table.Name}_{string.Join("_", fk.Columns)}",
                ColumnNames = [.. fk.Columns.Select(c => new SqlIdentifier(c))],
                References = new ObjectAddress(new SqlIdentifier(SchemaName), new SqlIdentifier(fk.ReferencedTable)),
                ReferencedColumnNames = [.. fk.ReferencedColumns.Select(c => new SqlIdentifier(c))],
                OnDelete = fk.OnDelete,
                OnUpdate = fk.OnUpdate,
            });

        var uniqueConstraints = (definition?.UniqueConstraints ?? [])
            .Select(uq => new UniqueConstraint
            {
                Name = uq.Name ?? $"uq_{table.Name}_{string.Join("_", uq.Columns)}",
                ColumnNames = [.. uq.Columns.Select(c => new SqlIdentifier(c))],
            });

        var checkConstraints = (definition?.CheckConstraints ?? [])
            .Select((ck, i) => new CheckConstraint
            {
                Name = ck.Name ?? $"ck_{table.Name}_{i + 1}",
                Expression = ck.Expression,
            });

        var indexes = indexObjects
            .Select(o => BuildIndex(o.Name, o.Sql!))
            .OfType<TableIndex>();

        var triggers = triggerObjects
            .Select(o => BuildTrigger(o.Name, o.Sql!))
            .OfType<Trigger>();

        return new Table
        {
            Name = table.Name,
            PrimaryKey = BuildPrimaryKey(table.Name, definition, primaryKeyColumns),
            Columns = [.. columns],
            ForeignKeys = [.. foreignKeys],
            UniqueConstraints = [.. uniqueConstraints],
            CheckConstraints = [.. checkConstraints],
            Indexes = [.. indexes],
            Triggers = [.. triggers],
        };
    }

    // Sqlite triggers are inline-body (no function), single-event, and BEFORE/AFTER on a table. The name and table come
    // from sqlite_master; the timing, event, optional WHEN and the BEGIN … END body are recovered from the stored SQL.
    private static Trigger? BuildTrigger(string name, string sql)
    {
        var parsed = SqliteDdl.ParseCreateTrigger(sql);
        return parsed is null
            ? null
            : new Trigger
            {
                Name = name,
                Timing = parsed.Timing,
                Events = parsed.Events,
                Level = parsed.ForEachRow ? TriggerLevel.Row : TriggerLevel.Statement,
                UpdateOfColumns = [.. parsed.UpdateOfColumns.Select(c => new SqlIdentifier(c))],
                When = parsed.When,
                Body = parsed.Body,
            };
    }

    private static PrimaryKey? BuildPrimaryKey(string tableName, SqliteTableDefinition? definition, List<(string Name, int Position)> pragmaColumns)
    {
        // The declared form is authoritative for the name and column order. PRAGMA is the fallback for a primary key
        // the parser missed (e.g. an oddly-formatted inline `INTEGER PRIMARY KEY` in an imported database).
        if (definition?.PrimaryKey is { } parsed)
        {
            return new PrimaryKey
            {
                Name = parsed.Name ?? $"pk_{tableName}",
                ColumnNames = [.. parsed.Columns.Select(c => new SqlIdentifier(c))],
            };
        }

        if (pragmaColumns.Count == 0)
        {
            return null;
        }

        return new PrimaryKey
        {
            Name = $"pk_{tableName}",
            ColumnNames = [.. pragmaColumns.OrderBy(c => c.Position).Select(c => new SqlIdentifier(c.Name))],
        };
    }

    private static TableIndex? BuildIndex(string name, string sql)
    {
        var parsed = SqliteDdl.ParseCreateIndex(sql);
        return parsed is null
            ? null
            : new TableIndex
            {
                Name = name,
                Columns = [.. parsed.Columns],
                IsUnique = parsed.IsUnique,
                Predicate = parsed.Predicate,
            };
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

    // Sqlite stores a column's declared type verbatim; the dialect writes NSchema's canonical type string, so
    // SqlType.Parse reverses it exactly. An untyped column (legal in Sqlite) maps to BLOB affinity's nearest model.
    private static SqlType ParseType(string declaredType) =>
        string.IsNullOrWhiteSpace(declaredType) ? SqlType.VarBinary() : SqlType.Parse(declaredType);

    // PRAGMA arguments cannot be parameterized, so the (trusted, database-sourced) table name is embedded as a
    // single-quoted string literal with embedded quotes doubled.
    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";
}
