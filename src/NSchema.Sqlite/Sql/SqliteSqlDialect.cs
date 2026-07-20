using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Indexes;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;
using NSchema.Plan.Backends;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;

namespace NSchema.Sqlite.Sql;

/// <summary>
/// The Sqlite <see cref="SqlDialect"/>. Objects are qualified as <c>"schema"."name"</c> (the schema is always
/// <c>main</c>), which is valid Sqlite.
/// </summary>
/// <remarks>
/// Sqlite's surface is small, so a great deal is rejected rather than half-supported:
/// <list type="bullet">
/// <item>Foreign keys, unique constraints and check constraints cannot be added to a table after the fact
/// (<c>ALTER TABLE</c> only does ADD/DROP/RENAME COLUMN and RENAME TABLE). A new table carries them inline in
/// its <c>CREATE TABLE</c>; adding one to an <em>existing</em> table is an error.</item>
/// <item>In-place column changes (type, nullability, default, generated expression) and constraint add/drops on an
/// existing table would each require a full table rebuild, which is not implemented; they are errors.</item>
/// <item>Features Sqlite has no equivalent for (schemas other than <c>main</c>, sequences, enums, domains, composite
/// types, routines, grants, materialized views) are errors.</item>
/// <item>Comments are skipped with a warning — Sqlite has no <c>COMMENT ON</c>.</item>
/// </list>
/// </remarks>
internal sealed class SqliteSqlDialect : SqlDialect
{
    private const string MainSchema = "main";

    /// <inheritdoc />
    protected override string Name => "Sqlite";

    // A Sqlite foreign key references a table in the same database, so the referenced name is emitted unqualified
    // (a schema-qualified REFERENCES target is a syntax error).
    protected override string ForeignKeyTarget(ForeignKey key) => Quote(key.References.Name);

    // ── Schemas (exactly one, the implicit 'main': it can be neither created nor dropped) ──

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> CreateSchema(CreateSchema action) =>
        action.SchemaName.Value == MainSchema ? Statements() : NotSupported("schemas other than 'main'");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> DropSchema(DropSchema action) =>
        action.SchemaName.Value == MainSchema ? Statements() : NotSupported("schemas other than 'main'");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> RenameSchema(RenameSchema action) =>
        NotSupported("schemas other than 'main'");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> SetSchemaComment(SetSchemaComment action) => Skipped(action);

    // ── Tables ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> CreateTable(CreateTable action)
    {
        var table = action.Table;

        if (table.ExclusionConstraints.Count > 0)
        {
            return NotSupported("exclusion constraints");
        }

        if (table.Columns.FirstOrDefault(c => c.IsIdentity) is { } identity)
        {
            return IdentityColumn(identity);
        }

        // Unlike a server database, Sqlite cannot ALTER TABLE ADD CONSTRAINT, so every table constraint is created
        // inline here; the linearizer folds a new table's constraint adds into CREATE TABLE, and only indexes
        // arrive as separate actions (Sqlite does support CREATE INDEX).
        var parts = table.Columns.Select(ColumnDef)
            .Concat(InlineConstraintClauses(table))
            .ToList();

        return Statement($"""
            CREATE TABLE {Qualify(action.SchemaName, table.Name)} (
                {string.Join(",\n    ", parts)}
            )
            """);
    }

    // DropTable, RenameTable: the base class's standard SQL is valid Sqlite.

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> AddPrimaryKey(AddPrimaryKey action) =>
        RequiresRebuild("add a primary key to an existing table");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> DropPrimaryKey(DropPrimaryKey action) =>
        RequiresRebuild("drop a primary key");

    // A new table inlines its foreign keys, unique and check constraints (see CreateTable), so the linearizer
    // never emits an Add* for one. These overrides therefore only ever see an existing table, which Sqlite cannot
    // ALTER to add a constraint.

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> AddForeignKey(AddForeignKey action) =>
        RequiresRebuild("add a foreign key to an existing table");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> DropForeignKey(DropForeignKey action) =>
        RequiresRebuild("drop a foreign key");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> AddUniqueConstraint(AddUniqueConstraint action) =>
        RequiresRebuild("add a unique constraint to an existing table");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> DropUniqueConstraint(DropUniqueConstraint action) =>
        RequiresRebuild("drop a unique constraint");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> AddCheckConstraint(AddCheckConstraint action) =>
        RequiresRebuild("add a check constraint to an existing table");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> DropCheckConstraint(DropCheckConstraint action) =>
        RequiresRebuild("drop a check constraint");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> GrantTablePrivileges(GrantTablePrivileges action) =>
        NotSupported("grants");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> RevokeTablePrivileges(RevokeTablePrivileges action) =>
        NotSupported("grants");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> GrantSchemaUsage(GrantSchemaUsage action) =>
        NotSupported("grants");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> RevokeSchemaUsage(RevokeSchemaUsage action) =>
        NotSupported("grants");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> SetTableComment(SetTableComment action) => Skipped(action);

    // ── Columns (only ADD / DROP / RENAME are native to Sqlite) ─────────────────

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> AddColumn(AddColumn action) =>
        action.Column.IsIdentity
            ? IdentityColumn(action.Column)
            : Statement($"ALTER TABLE {Qualify(action.Table)} ADD COLUMN {ColumnDef(action.Column)}");

    // DropColumn, RenameColumn: the base class's standard SQL is valid Sqlite.

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> AlterColumn(AlterColumn action) =>
        RequiresRebuild("change a column");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> SetColumnDefault(SetColumnDefault action) =>
        RequiresRebuild("change a column's default");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> SetColumnGenerated(SetColumnGenerated action) =>
        RequiresRebuild("change a column's generated expression");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> AlterIdentitySequence(AlterIdentitySequence action) =>
        NotSupported("identity columns");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> SetColumnComment(SetColumnComment action) => Skipped(action);

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> SetConstraintComment(SetConstraintComment action) => Skipped(action);

    // ── Indexes ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> CreateIndex(CreateIndex action)
    {
        var index = action.Index;
        if (index.Method is not null)
        {
            return Error($"Sqlite indexes have no access method (USING) — index '{index.Name}' specifies '{index.Method}'.");
        }

        if (index.Include.Count > 0)
        {
            return Error($"Sqlite indexes do not support INCLUDE columns — index '{index.Name}'.");
        }

        // Sqlite has no NULLS FIRST/LAST in an index, so a non-default null ordering is rejected.
        if (index.Columns.Any(c => c.Nulls != IndexNulls.Default))
        {
            return Error("Sqlite indexes do not support NULLS FIRST / NULLS LAST ordering.");
        }

        var keys = string.Join(", ", index.Columns.Select(IndexKeyText));
        var unique = index.IsUnique ? "UNIQUE " : "";
        var sql = $"CREATE {unique}INDEX {Qualify(action.Table.Schema, index.Name)} ON {Quote(action.Table.Name)} ({keys})";
        return Statement(index.Predicate is { } predicate ? $"{sql} WHERE {predicate}" : sql);
    }

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> DropIndex(DropIndex action) =>
        Statement($"DROP INDEX {Qualify(action.Index.Schema, action.Index.Member)}");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> SetIndexComment(SetIndexComment action) => Skipped(action);

    // A plain column key is quoted; an expression key is parenthesised and verbatim. ASC/DESC is emitted only
    // when explicit.
    private string IndexKeyText(IndexColumn column)
    {
        var key = column.Expression is { } expression ? $"({expression})" : Quote(column.Column!);
        var sort = column.Sort switch
        {
            IndexSort.Ascending => " ASC",
            IndexSort.Descending => " DESC",
            _ => "",
        };
        return $"{key}{sort}";
    }

    // ── Views ───────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> CreateView(CreateView action)
    {
        if (action.View.IsMaterialized)
        {
            return NotSupported("materialized views");
        }

        // A plain view body change arrives as CreateView (the core relies on CREATE OR REPLACE); Sqlite has none,
        // so an idempotent DROP precedes the CREATE. This serves a fresh add too (the DROP is a no-op).
        return Statements(
            new SqlStatement($"DROP VIEW IF EXISTS {Qualify(action.SchemaName, action.View.Name)}"),
            new SqlStatement($"CREATE VIEW {Qualify(action.SchemaName, action.View.Name)} AS {action.View.Body}"));
    }

    // DropView: the base class's standard SQL is valid Sqlite, and it already rejects materialized views.

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> RenameView(RenameView action) =>
        // Sqlite has no ALTER VIEW ... RENAME, and the rename action does not carry the body needed to recreate it.
        NotSupported("renaming a view (drop and recreate it instead)");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> SetViewComment(SetViewComment action) => Skipped(action);

    // ── Triggers (inline body; Sqlite has no CREATE OR REPLACE, so a change is a drop + recreate) ──

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> CreateTrigger(CreateTrigger action)
    {
        // CREATE TRIGGER "main"."name" {BEFORE|AFTER} {event} ON "table" [FOR EACH ROW] [WHEN (expr)] <body>.
        // Sqlite triggers run an inline body (there are no functions to call), fire on a single event, and
        // INSTEAD OF is for views only — facets the model carries but Sqlite cannot express are rejected loudly.
        // The body is the verbatim BEGIN … END block.
        var trigger = action.Trigger;
        if (trigger.Body is not { } body)
        {
            return Error(
                $"Sqlite triggers run an inline body, but trigger '{trigger.Name}' has none (it calls a function). Sqlite has no stored functions; declare it with an AS $$ … $$ body.");
        }

        if (trigger.Timing == TriggerTiming.InsteadOf)
        {
            return NotSupported("INSTEAD OF triggers (Sqlite supports them only on views, and NSchema attaches triggers to tables)");
        }

        if (trigger.Events.HasFlag(TriggerEvent.Truncate))
        {
            return NotSupported("TRUNCATE triggers");
        }

        if (trigger.Events is not (TriggerEvent.Insert or TriggerEvent.Update or TriggerEvent.Delete))
        {
            return Error(
                $"Sqlite triggers fire on a single event, but trigger '{trigger.Name}' lists more than one. Declare a separate trigger per event.");
        }

        var timing = trigger.Timing == TriggerTiming.Before ? "BEFORE" : "AFTER";
        var forEachRow = trigger.Level == TriggerLevel.Row ? " FOR EACH ROW" : "";
        var when = trigger.When is { } w ? $" WHEN ({w})" : "";
        return Statement(
            $"CREATE TRIGGER {Qualify(action.Table.Schema, trigger.Name)} {timing} {TriggerEventText(trigger)} ON {Quote(action.Table.Name)}{forEachRow}{when} {body}");
    }

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> DropTrigger(DropTrigger action) =>
        Statement($"DROP TRIGGER {Qualify(action.Trigger.Schema, action.Trigger.Member)}");

    /// <inheritdoc />
    protected override Result<IReadOnlyList<SqlStatement>> SetTriggerComment(SetTriggerComment action) => Skipped(action);

    // The single fired event. UPDATE may be narrowed to columns (written unparenthesised, as Sqlite expects).
    private string TriggerEventText(Trigger trigger)
    {
        if (trigger.Events.HasFlag(TriggerEvent.Insert))
        {
            return "INSERT";
        }

        if (trigger.Events.HasFlag(TriggerEvent.Delete))
        {
            return "DELETE";
        }

        return trigger.UpdateOfColumns.Count > 0 ? $"UPDATE OF {ColumnList(trigger.UpdateOfColumns)}" : "UPDATE";
    }

    // Everything else — sequences, enums, domains, composite types, routines, extensions and their comments —
    // keeps the base class's Unsupported rendering: Sqlite has no equivalent for any of them.

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private string ColumnDef(Column column)
    {
        // Sqlite stores a column's declared type verbatim and applies type affinity by name, so emitting NSchema's
        // canonical type string (e.g. "bigint", "varchar(255)") lets the introspector parse it straight back with
        // SqlType.Parse — no information is lost to affinity collapse.
        var type = column.Type.ToString();
        var nullable = column.IsNullable ? "" : " NOT NULL";
        // A generated column is mutually exclusive with a default (the core's structural policy enforces this).
        var def = column is { DefaultExpression: { } d, GeneratedExpression: null } ? $" DEFAULT {d}" : "";
        var generated = column.GeneratedExpression is { } g ? $" GENERATED ALWAYS AS ({g}) STORED" : "";
        return $"{Quote(column.Name)} {type}{nullable}{def}{generated}";
    }

    private static Result<IReadOnlyList<SqlStatement>> IdentityColumn(Column column) =>
        Error($"Sqlite does not support identity columns (column '{column.Name}'). Model an auto-incrementing key as an INTEGER primary key (a rowid alias) instead.");

    private static Result<IReadOnlyList<SqlStatement>> NotSupported(string feature) =>
        Error($"Sqlite does not support {feature}.");

    private static Result<IReadOnlyList<SqlStatement>> RequiresRebuild(string operation) =>
        Error($"Sqlite cannot {operation} in place; this requires rebuilding the table, which NSchema.Sqlite does not support. Recreate the table instead.");

    private static Result<IReadOnlyList<SqlStatement>> Error(string message) =>
        Diagnostic.Error("sqlite-dialect", message);
}
