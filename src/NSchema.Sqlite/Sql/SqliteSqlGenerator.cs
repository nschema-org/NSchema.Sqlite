using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.CompositeTypes;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Domains;
using NSchema.Plan.Model.Enums;
using NSchema.Plan.Model.Extensions;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Sequence;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;
using NSchema.Schema.Model.Columns;
using NSchema.Schema.Model.Indexes;
using NSchema.Schema.Model.Tables;
using NSchema.Schema.Model.Triggers;
using NSchema.Sql;
using NSchema.Sql.Model;

namespace NSchema.Sqlite.Sql;

/// <summary>
/// Translates an NSchema <see cref="MigrationPlan"/> into Sqlite DDL. Objects are qualified as
/// <c>"schema"."name"</c> (the schema is always <c>main</c>), which is valid Sqlite.
/// </summary>
/// <remarks>
/// Sqlite's surface is small, so a great deal is rejected rather than half-supported:
/// <list type="bullet">
/// <item>Foreign keys, unique constraints and check constraints cannot be added to a table after the fact
/// (<c>ALTER TABLE</c> only does ADD/DROP/RENAME COLUMN and RENAME TABLE). They are therefore inlined into
/// <c>CREATE TABLE</c> for a table created in the same plan, and the separate <c>Add*</c> actions the linearizer
/// emits for that table are folded away. The same action against an <em>existing</em> table throws.</item>
/// <item>In-place column changes (type, nullability, default, generated expression) and constraint add/drops on an
/// existing table would each require a full table rebuild, which is not implemented; they throw.</item>
/// <item>Features Sqlite has no equivalent for (schemas other than <c>main</c>, sequences, enums, domains, composite
/// types, routines, grants, materialized views, triggers) throw.</item>
/// <item>Comments are no-ops — Sqlite has no <c>COMMENT ON</c>.</item>
/// </list>
/// </remarks>
internal sealed class SqliteSqlGenerator : ISqlGenerator
{
    public SqlPlan Generate(MigrationPlan plan)
    {
        var preDeploymentStatements = plan.PreDeploymentScripts.Select(s => new SqlStatement(s.Sql, s.RunOutsideTransaction));
        var postDeploymentStatements = plan.PostDeploymentScripts.Select(s => new SqlStatement(s.Sql, s.RunOutsideTransaction));

        // Tables created in this plan: their foreign keys, unique and check constraints are inlined into the
        // CREATE TABLE (Sqlite can't ALTER TABLE ADD CONSTRAINT), so the separate Add* actions the linearizer emits
        // for these tables are folded away rather than emitted.
        var createdTables = plan.Actions
            .OfType<CreateTable>()
            .Select(t => (t.SchemaName, t.Table.Name))
            .ToHashSet();

        var statements = plan.Actions.SelectMany(action => GenerateStatements(action, createdTables)).ToList();
        List<SqlStatement> allStatements = [.. preDeploymentStatements, .. statements, .. postDeploymentStatements];
        return new SqlPlan(allStatements);
    }

    // ── SQL generation ────────────────────────────────────────────────────────

    private static IEnumerable<SqlStatement> GenerateStatements(MigrationAction action, HashSet<(string, string)> createdTables) => action switch
    {
        // A new table inlines its foreign keys, unique and check constraints (see BuildCreateTable), so the
        // linearizer's separate Add* actions for that table are dropped. The same action against a table that is not
        // being created in this plan is an unsupported in-place ALTER.
        AddForeignKey x => FoldedOrUnsupported(createdTables, x.SchemaName, x.TableName, "add a foreign key to an existing table"),
        AddUniqueConstraint x => FoldedOrUnsupported(createdTables, x.SchemaName, x.TableName, "add a unique constraint to an existing table"),
        AddCheckConstraint x => FoldedOrUnsupported(createdTables, x.SchemaName, x.TableName, "add a check constraint to an existing table"),

        // A plain view body change arrives as CreateView (the core relies on CREATE OR REPLACE); Sqlite has none, so
        // an idempotent DROP precedes the CREATE. This serves a fresh add too (the DROP is a no-op).
        CreateView { View.IsMaterialized: true } => throw Unsupported("materialized views"),
        CreateView x =>
        [
            new SqlStatement($"DROP VIEW IF EXISTS {Qualify(x.SchemaName, x.View.Name)}"),
            new SqlStatement($"CREATE VIEW {Qualify(x.SchemaName, x.View.Name)} AS {x.View.Body}"),
        ],

        // Sqlite has no COMMENT ON, so every comment change is a no-op. (A consequence is that a desired schema
        // carrying doc comments will show those changes as perpetually pending; this is a documented limitation.)
        SetSchemaComment or SetTableComment or SetColumnComment or SetConstraintComment or SetIndexComment
            or SetViewComment or SetTriggerComment or SetSequenceComment or SetEnumComment or SetDomainComment
            or SetCompositeTypeComment or SetRoutineComment or SetExtensionComment => [],

        // Sqlite has exactly one schema, the implicit 'main': it can be neither created nor dropped, so those actions
        // against it are no-ops. A fresh plan against an empty database emits CreateSchema("main"), and a destroy
        // emits DropSchema("main") alongside the explicit table drops. Any other schema name is genuinely unsupported
        // and falls through to the throw below.
        CreateSchema { SchemaName: "main" } or DropSchema { SchemaName: "main" } => [],

        _ => [new SqlStatement(GenerateSql(action))],
    };

    private static IEnumerable<SqlStatement> FoldedOrUnsupported(HashSet<(string, string)> createdTables, string schema, string table, string operation) =>
        createdTables.Contains((schema, table)) ? [] : throw RequiresRebuild(operation);

    private static string GenerateSql(MigrationAction action) => action switch
    {
        // ── Tables ──────────────────────────────────────────────────────────────
        CreateTable x => BuildCreateTable(x),
        DropTable x => $"DROP TABLE {Qualify(x.SchemaName, x.TableName)}",
        RenameTable x => $"ALTER TABLE {Qualify(x.SchemaName, x.OldName)} RENAME TO \"{x.NewName}\"",

        // ── Columns (only ADD / DROP / RENAME are native to Sqlite) ──────────────
        AddColumn x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} ADD COLUMN {BuildColumnDef(x.Column)}",
        DropColumn x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} DROP COLUMN \"{x.ColumnName}\"",
        RenameColumn x => $"ALTER TABLE {Qualify(x.SchemaName, x.TableName)} RENAME COLUMN \"{x.OldName}\" TO \"{x.NewName}\"",
        AlterColumnType => throw RequiresRebuild("change a column's type"),
        AlterColumnNullability => throw RequiresRebuild("change a column's nullability"),
        SetColumnDefault => throw RequiresRebuild("change a column's default"),
        SetColumnGenerated => throw RequiresRebuild("change a column's generated expression"),
        AlterIdentitySequence => throw Unsupported("identity columns"),

        // ── Constraints on an existing table (all need a rebuild) ────────────────
        AddPrimaryKey => throw RequiresRebuild("add a primary key to an existing table"),
        DropPrimaryKey => throw RequiresRebuild("drop a primary key"),
        DropForeignKey => throw RequiresRebuild("drop a foreign key"),
        DropUniqueConstraint => throw RequiresRebuild("drop a unique constraint"),
        DropCheckConstraint => throw RequiresRebuild("drop a check constraint"),
        AddExclusionConstraint or DropExclusionConstraint => throw Unsupported("exclusion constraints"),

        // ── Indexes ───────────────────────────────────────────────────────────────
        CreateIndex x => BuildCreateIndex(x),
        DropIndex x => $"DROP INDEX {Qualify(x.SchemaName, x.IndexName)}",

        // ── Views ───────────────────────────────────────────────────────────────
        DropView { IsMaterialized: true } => throw Unsupported("materialized views"),
        DropView x => $"DROP VIEW {Qualify(x.SchemaName, x.ViewName)}",
        // Sqlite has no ALTER VIEW ... RENAME, and the rename action does not carry the body needed to recreate it.
        RenameView => throw Unsupported("renaming a view (drop and recreate it instead)"),

        // ── Triggers (inline body; Sqlite has no CREATE OR REPLACE, so a change is a drop + recreate) ──
        CreateTrigger x => BuildCreateTrigger(x),
        DropTrigger x => $"DROP TRIGGER {Qualify(x.SchemaName, x.TriggerName)}",

        // ── Comments: Sqlite has no COMMENT ON, so these are no-ops elsewhere; reaching here means a stray
        //    comment action was routed through the single-statement path, which should never happen.

        // ── Features with no Sqlite equivalent ───────────────────────────────────
        CreateSchema or DropSchema or RenameSchema or GrantSchemaUsage or RevokeSchemaUsage =>
            throw Unsupported("schemas other than 'main'"),
        CreateSequence or DropSequence or RenameSequence or AlterSequence => throw Unsupported("sequences"),
        CreateEnum or DropEnum or RenameEnum or AddEnumValue => throw Unsupported("enum types"),
        CreateDomain or DropDomain or RenameDomain or RecreateDomain or AlterDomainDefault or AlterDomainNotNull or AddDomainCheck or DropDomainCheck =>
            throw Unsupported("domains"),
        CreateCompositeType or DropCompositeType or RenameCompositeType or AddCompositeField or DropCompositeField or AlterCompositeFieldType =>
            throw Unsupported("composite types"),
        CreateRoutine or DropRoutine or RecreateRoutine or RenameRoutine => throw Unsupported("functions and procedures"),
        CreateExtension or DropExtension or AlterExtension => throw Unsupported("extensions"),
        GrantTablePrivileges or RevokeTablePrivileges => throw Unsupported("grants"),

        _ => throw new ArgumentOutOfRangeException(nameof(action), $"Unhandled action type: {action.GetType().Name}"),
    };

    private static string BuildCreateTable(CreateTable x)
    {
        var table = x.Table;
        if (table.ExclusionConstraints.Count > 0)
        {
            throw Unsupported("exclusion constraints");
        }

        var parts = table.Columns.Select(BuildColumnDef).ToList();

        // Unlike a server database, Sqlite cannot ALTER TABLE ADD CONSTRAINT, so every table constraint is created
        // inline here — the primary key, then unique and check constraints, then foreign keys. Only indexes arrive
        // as separate actions (Sqlite does support CREATE INDEX). See GenerateStatements / the linearizer.
        if (table.PrimaryKey is { } pk)
        {
            parts.Add($"CONSTRAINT \"{pk.Name}\" PRIMARY KEY ({ColList(pk.ColumnNames)})");
        }

        foreach (var unique in table.UniqueConstraints)
        {
            parts.Add($"CONSTRAINT \"{unique.Name}\" UNIQUE ({ColList(unique.ColumnNames)})");
        }

        foreach (var check in table.CheckConstraints)
        {
            parts.Add($"CONSTRAINT \"{check.Name}\" CHECK ({check.Expression})");
        }

        foreach (var foreignKey in table.ForeignKeys)
        {
            parts.Add(BuildInlineForeignKey(foreignKey));
        }

        return $"""
            CREATE TABLE {Qualify(x.SchemaName, table.Name)} (
                {string.Join(",\n    ", parts)}
            )
            """;
    }

    // A Sqlite foreign key references a table in the same database, so the referenced name is emitted unqualified
    // (a schema-qualified REFERENCES target is a syntax error). A NO ACTION rule is the engine default and is
    // omitted so it round-trips clean against PRAGMA foreign_key_list.
    private static string BuildInlineForeignKey(ForeignKey fk)
    {
        var onDelete = fk.OnDelete == ReferentialAction.NoAction ? "" : $" ON DELETE {ToReferentialAction(fk.OnDelete)}";
        var onUpdate = fk.OnUpdate == ReferentialAction.NoAction ? "" : $" ON UPDATE {ToReferentialAction(fk.OnUpdate)}";
        return $"CONSTRAINT \"{fk.Name}\" FOREIGN KEY ({ColList(fk.ColumnNames)}) REFERENCES \"{fk.ReferencedTable}\" ({ColList(fk.ReferencedColumnNames)}){onDelete}{onUpdate}";
    }

    private static string BuildColumnDef(Column col)
    {
        if (col.IsIdentity)
        {
            throw new NotSupportedException(
                $"Sqlite does not support identity columns (column '{col.Name}'). Model an auto-incrementing key as an INTEGER primary key (a rowid alias) instead.");
        }

        var type = ToSqliteType(col.Type);
        var nullable = col.IsNullable ? "" : " NOT NULL";
        // A generated column is mutually exclusive with a default (the core's structural policy enforces this).
        var def = col is { DefaultExpression: { } d, GeneratedExpression: null } ? $" DEFAULT {d}" : "";
        var generated = col.GeneratedExpression is { } g ? $" GENERATED ALWAYS AS ({g}) STORED" : "";
        return $"\"{col.Name}\" {type}{nullable}{def}{generated}";
    }

    private static string BuildCreateIndex(CreateIndex x)
    {
        var idx = x.Index;
        if (idx.Method is not null)
        {
            throw new NotSupportedException($"Sqlite indexes have no access method (USING) — index '{idx.Name}' specifies '{idx.Method}'.");
        }

        if (idx.Include.Count > 0)
        {
            throw new NotSupportedException($"Sqlite indexes do not support INCLUDE columns — index '{idx.Name}'.");
        }

        var keys = string.Join(", ", idx.Columns.Select(IndexKeyText));
        var unique = idx.IsUnique ? "UNIQUE " : "";
        var sql = $"CREATE {unique}INDEX {Qualify(x.SchemaName, idx.Name)} ON \"{x.TableName}\" ({keys})";
        return idx.Predicate is { } predicate ? $"{sql} WHERE {predicate}" : sql;
    }

    // A plain column key is quoted; an expression key is parenthesised and verbatim. ASC/DESC is emitted only when
    // explicit. Sqlite has no NULLS FIRST/LAST in an index, so a non-default null ordering is rejected.
    private static string IndexKeyText(IndexColumn col)
    {
        if (col.Nulls != IndexNulls.Default)
        {
            throw new NotSupportedException("Sqlite indexes do not support NULLS FIRST / NULLS LAST ordering.");
        }

        var key = col.IsExpression ? $"({col.Expression})" : $"\"{col.Expression}\"";
        var sort = col.Sort switch
        {
            IndexSort.Ascending => " ASC",
            IndexSort.Descending => " DESC",
            _ => "",
        };
        return $"{key}{sort}";
    }

    // ── Triggers ──────────────────────────────────────────────────────────────────

    // CREATE TRIGGER "main"."name" {BEFORE|AFTER} {event} ON "table" [FOR EACH ROW] [WHEN (expr)] <body>. Sqlite triggers
    // run an inline body (there are no functions to call), fire on a single event, and INSTEAD OF is for views only —
    // facets the model carries but Sqlite cannot express are rejected loudly. The body is the verbatim BEGIN … END block.
    private static string BuildCreateTrigger(CreateTrigger x)
    {
        var trigger = x.Trigger;
        if (trigger.Body is not { } body)
        {
            throw new NotSupportedException(
                $"Sqlite triggers run an inline body, but trigger '{trigger.Name}' has none (it calls a function). Sqlite has no stored functions; declare it with an AS $$ … $$ body.");
        }

        if (trigger.Timing == TriggerTiming.InsteadOf)
        {
            throw Unsupported("INSTEAD OF triggers (Sqlite supports them only on views, and NSchema attaches triggers to tables)");
        }

        if (trigger.Events.HasFlag(TriggerEvent.Truncate))
        {
            throw Unsupported("TRUNCATE triggers");
        }

        if (trigger.Events is not (TriggerEvent.Insert or TriggerEvent.Update or TriggerEvent.Delete))
        {
            throw new NotSupportedException(
                $"Sqlite triggers fire on a single event, but trigger '{trigger.Name}' lists more than one. Declare a separate trigger per event.");
        }

        var timing = trigger.Timing == TriggerTiming.Before ? "BEFORE" : "AFTER";
        var forEachRow = trigger.Level == TriggerLevel.Row ? " FOR EACH ROW" : "";
        var when = trigger.When is { } w ? $" WHEN ({w})" : "";
        return $"CREATE TRIGGER {Qualify(x.SchemaName, trigger.Name)} {timing} {TriggerEventText(trigger)} ON \"{x.TableName}\"{forEachRow}{when} {body}";
    }

    // The single fired event. UPDATE may be narrowed to columns (written unparenthesised, as Sqlite expects).
    private static string TriggerEventText(Trigger trigger)
    {
        if (trigger.Events.HasFlag(TriggerEvent.Insert))
        {
            return "INSERT";
        }

        if (trigger.Events.HasFlag(TriggerEvent.Delete))
        {
            return "DELETE";
        }

        return trigger.UpdateOfColumns.Count > 0 ? $"UPDATE OF {ColList(trigger.UpdateOfColumns)}" : "UPDATE";
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    // Sqlite stores a column's declared type verbatim and applies type affinity by name, so emitting NSchema's
    // canonical type string (e.g. "bigint", "varchar(255)", "decimal(18,2)") lets the introspector parse it straight
    // back with SqlType.Parse — no information is lost to affinity collapse.
    private static string ToSqliteType(SqlType type) => type.ToString();

    private static string Qualify(string schema, string name) => $"\"{schema}\".\"{name}\"";

    private static string ColList(IReadOnlyList<string> columns) =>
        string.Join(", ", columns.Select(c => $"\"{c}\""));

    private static string ToReferentialAction(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION",
    };

    private static NotSupportedException Unsupported(string feature) =>
        new($"Sqlite does not support {feature}.");

    private static NotSupportedException RequiresRebuild(string operation) =>
        new($"Sqlite cannot {operation} in place; this requires rebuilding the table, which NSchema.Sqlite does not support. Recreate the table instead.");
}
