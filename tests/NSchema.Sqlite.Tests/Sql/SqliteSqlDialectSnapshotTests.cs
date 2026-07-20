using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Constraints;
using NSchema.Model.Enums;
using NSchema.Model.Indexes;
using NSchema.Model.Routines;
using NSchema.Model.Scripts;
using NSchema.Model.Sequences;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;
using NSchema.Model.Views;
using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Enums;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Scripts;
using NSchema.Plan.Model.Sequences;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;
using NSchema.Sqlite.Sql;

namespace NSchema.Sqlite.Tests.Sql;

/// <summary>
/// Snapshot tests for <see cref="SqliteSqlDialect"/>: they assert on the exact SQL text the dialect renders
/// (no database required), plus any diagnostics riding the rendering. Snapshots live alongside this file as
/// <c>*.verified.txt</c>; review and commit them when the rendered SQL intentionally changes. Behaviour that
/// depends on a real database (round-trips through introspection) lives in <see cref="SqliteSqlDialectTests"/>.
/// </summary>
public sealed class SqliteSqlDialectSnapshotTests
{
    private const string Schema = "main";

    /// <summary>Renders each action in order through a fresh dialect and snapshots the statements and diagnostics.</summary>
    private static Task VerifyRendering(params MigrationAction[] actions)
    {
        var dialect = new SqliteSqlDialect();
        var statements = new List<object>();
        var diagnostics = new List<string>();
        foreach (var action in actions)
        {
            var rendered = dialect.Generate(action);
            diagnostics.AddRange(rendered.Diagnostics.Select(d => $"{d.Severity}: {d.Message}"));
            if (rendered.Value is { } sql)
            {
                statements.AddRange(sql.Select(s => s.RunOutsideTransaction
                    ? (object)new { Sql = s.Sql.Value, RunOutsideTransaction = true }
                    : s.Sql.Value));
            }
        }

        return Verify(new { statements, diagnostics });
    }

    // ── Tables ──────────────────────────────────────────────────────────────────

    [Fact]
    public Task CreateTable_WithColumnsAndPrimaryKey() => VerifyRendering(
        new CreateTable(Schema, new Table
        {
            Name = "users",
            PrimaryKey = new PrimaryKey { Name = "pk_users", ColumnNames = ["id"] },
            Columns =
            [
                new Column { Name = "id", Type = SqlType.BigInt },
                new Column { Name = "email", Type = SqlType.VarChar(255) },
                new Column { Name = "created_at", Type = SqlType.DateTimeOffset, DefaultExpression = "CURRENT_TIMESTAMP" },
                new Column { Name = "notes", Type = SqlType.Text, IsNullable = true },
            ],
        }));

    [Fact]
    public Task CreateTable_WithAllConstraints_InlinesThemAndFoldsSeparateAddActions()
    {
        // The linearizer creates a table with only columns + PK inline, then emits the foreign keys, unique and
        // check constraints as separate Add* actions. Sqlite can't ALTER TABLE ADD CONSTRAINT, so the dialect
        // inlines them into the CREATE TABLE and the separate actions render as nothing — the snapshot must show
        // one CREATE TABLE carrying everything, and nothing after it.
        var table = new Table
        {
            Name = "orders",
            PrimaryKey = new PrimaryKey { Name = "pk_orders", ColumnNames = ["id"] },
            Columns =
            [
                new Column { Name = "id", Type = SqlType.BigInt },
                new Column { Name = "user_id", Type = SqlType.BigInt },
                new Column { Name = "code", Type = SqlType.VarChar(20) },
                new Column { Name = "total", Type = SqlType.Decimal(10, 2) },
            ],
            ForeignKeys =
            [
                new ForeignKey
                {
                    Name = "fk_orders_user",
                    ColumnNames = ["user_id"],
                    References = new(Schema, "users"),
                    ReferencedColumnNames = ["id"],
                    OnDelete = ReferentialAction.Cascade,
                },
            ],
            UniqueConstraints = [new UniqueConstraint { Name = "uq_orders_code", ColumnNames = ["code"] }],
            CheckConstraints = [new CheckConstraint { Name = "ck_orders_total", Expression = "total >= 0" }],
        };

        return VerifyRendering(
            new CreateTable(Schema, table),
            new AddForeignKey(new(Schema, "orders"), table.ForeignKeys[0]),
            new AddUniqueConstraint(new(Schema, "orders"), table.UniqueConstraints[0]),
            new AddCheckConstraint(new(Schema, "orders"), table.CheckConstraints[0]));
    }

    [Fact]
    public Task TableLifecycle() => VerifyRendering(
        new RenameTable(new(Schema, "old_users"), "users"),
        new DropTable(new(Schema, "legacy")));

    // ── Columns ───────────────────────────────────────────────────────────────

    [Fact]
    public Task ColumnOperations() => VerifyRendering(
        new AddColumn(new(Schema, "users"), new Column { Name = "age", Type = SqlType.Int, IsNullable = true }),
        new AddColumn(new(Schema, "users"), new Column { Name = "status", Type = SqlType.VarChar(20), DefaultExpression = "'active'" }),
        new RenameColumn(new(Schema, "users", "age"), "years"),
        new DropColumn(new(Schema, "users"), new Column { Name = "years", Type = SqlType.Int, IsNullable = true }));

    [Fact]
    public Task GeneratedColumn() => VerifyRendering(
        new CreateTable(Schema, new Table
        {
            Name = "boxes",
            Columns =
            [
                new Column { Name = "w", Type = SqlType.Int },
                new Column { Name = "h", Type = SqlType.Int },
                new Column { Name = "area", Type = SqlType.Int, IsNullable = true, GeneratedExpression = "w * h" },
            ],
        }));

    // ── Indexes ───────────────────────────────────────────────────────────────

    [Fact]
    public Task IndexOperations() => VerifyRendering(
        new CreateIndex(new(Schema, "users"), new TableIndex { Name = "idx_users_email", Columns = ["email"], IsUnique = true }),
        new CreateIndex(new(Schema, "users"), new TableIndex { Name = "idx_users_active", Columns = ["created_at"], Predicate = "notes IS NOT NULL" }),
        new CreateIndex(new(Schema, "users"), new TableIndex
        {
            Name = "idx_users_recent",
            Columns = [new IndexColumn(new SqlIdentifier("created_at"), Sort: IndexSort.Descending), new IndexColumn(Expression: new SqlText("lower(email)"))],
        }),
        new DropIndex(new(Schema, "users", "idx_users_email")));

    // ── Views ─────────────────────────────────────────────────────────────────

    [Fact]
    public Task ViewOperations() => VerifyRendering(
        // CreateView serves both add and body-modify; Sqlite has no CREATE OR REPLACE, so each is DROP + CREATE.
        new CreateView(Schema, new View { Name = "active_users", Body = "SELECT id, email FROM main.users WHERE active" }),
        new DropView(new(Schema, "active_users")));

    // ── Triggers (inline body; single event) ─────────────────────────────────────

    [Fact]
    public Task TriggerOperations() => VerifyRendering(
        // AFTER … FOR EACH ROW with an inline body. (A trigger body can't use schema-qualified table names in Sqlite.)
        new CreateTrigger(new(Schema, "users"), new Trigger
        {
            Name = "users_audit",
            Timing = TriggerTiming.After,
            Events = TriggerEvent.Insert,
            Level = TriggerLevel.Row,
            Body = "BEGIN INSERT INTO audit (msg) VALUES ('inserted'); END",
        }),
        // BEFORE UPDATE OF (cols) with a WHEN guard.
        new CreateTrigger(new(Schema, "users"), new Trigger
        {
            Name = "users_guard",
            Timing = TriggerTiming.Before,
            Events = TriggerEvent.Update,
            Level = TriggerLevel.Row,
            UpdateOfColumns = ["email", "name"],
            When = "new.email IS NOT NULL",
            Body = "BEGIN INSERT INTO audit (msg) VALUES ('updated'); END",
        }),
        // No explicit level → no FOR EACH ROW emitted.
        new CreateTrigger(new(Schema, "users"), new Trigger
        {
            Name = "users_cleanup",
            Timing = TriggerTiming.After,
            Events = TriggerEvent.Delete,
            Body = "BEGIN DELETE FROM audit WHERE msg = old.email; END",
        }),
        // Sqlite has no COMMENT ON, so a trigger comment is skipped with a warning (contributes no statement).
        new SetTriggerComment(new(Schema, "users", "users_audit"), null, "audit inserts"),
        new DropTrigger(new(Schema, "users", "users_audit")));

    [Fact]
    public void MultiEventTrigger_IsAnError()
    {
        var rendered = new SqliteSqlDialect().Generate(new CreateTrigger(new(Schema, "users"), new Trigger
        {
            Name = "t",
            Timing = TriggerTiming.After,
            Events = TriggerEvent.Insert | TriggerEvent.Update,
            Level = TriggerLevel.Row,
            Body = "BEGIN END",
        }));

        rendered.IsFailure.ShouldBeTrue();
        rendered.Errors.ShouldHaveSingleItem().Message.ShouldContain("single event");
    }

    [Fact]
    public void InsteadOfTrigger_IsAnError()
    {
        var rendered = new SqliteSqlDialect().Generate(new CreateTrigger(new(Schema, "users"), new Trigger
        {
            Name = "t",
            Timing = TriggerTiming.InsteadOf,
            Events = TriggerEvent.Insert,
            Level = TriggerLevel.Row,
            Body = "BEGIN END",
        }));

        rendered.IsFailure.ShouldBeTrue();
        rendered.Errors.ShouldHaveSingleItem().Message.ShouldContain("INSTEAD OF");
    }

    // ── Type mapping (canonical NSchema type names, applied via CREATE TABLE) ────

    [Fact]
    public Task TypeMapping_CoversAllSqlTypes() => VerifyRendering(
        new CreateTable(Schema, new Table
        {
            Name = "types",
            Columns =
            [
                Col("boolean", SqlType.Boolean),
                Col("tinyint", SqlType.TinyInt),
                Col("smallint", SqlType.SmallInt),
                Col("int", SqlType.Int),
                Col("bigint", SqlType.BigInt),
                Col("float", SqlType.Float),
                Col("double", SqlType.Double),
                Col("decimal", SqlType.Decimal(18, 4)),
                Col("char", SqlType.Char(10)),
                Col("nchar", SqlType.NChar(10)),
                Col("varchar_unbounded", SqlType.VarChar(null)),
                Col("varchar", SqlType.VarChar(100)),
                Col("nvarchar", SqlType.NVarChar(100)),
                Col("text", SqlType.Text),
                Col("date", SqlType.Date),
                Col("time", SqlType.Time),
                Col("datetime", SqlType.DateTime),
                Col("datetimeoffset", SqlType.DateTimeOffset),
                Col("guid", SqlType.Guid),
                Col("binary", SqlType.Binary(16)),
                Col("varbinary", SqlType.VarBinary(null)),
            ],
        }));

    private static Column Col(string name, SqlType type) => new() { Name = name, Type = type, IsNullable = true };

    // ── Comments are skipped (Sqlite has no COMMENT ON) ─────────────────────────

    [Fact]
    public void CommentActions_ProduceNoStatements_AndWarn()
    {
        var dialect = new SqliteSqlDialect();

        var table = dialect.Generate(new SetTableComment(new(Schema, "users"), null, "Registered users"));
        var column = dialect.Generate(new SetColumnComment(new(Schema, "users", "email"), null, "Login address"));

        table.IsSuccess.ShouldBeTrue();
        table.Require().ShouldBeEmpty();
        table.Warnings.ShouldHaveSingleItem().Message.ShouldContain("skipped");
        column.IsSuccess.ShouldBeTrue();
        column.Require().ShouldBeEmpty();
        column.Warnings.ShouldHaveSingleItem().Message.ShouldContain("skipped");
    }

    // ── Scripts pass through verbatim ───────────────────────────────────────────

    [Fact]
    public void DeploymentScripts_PassThrough_PreservingRunOutsideTransaction()
    {
        var dialect = new SqliteSqlDialect();
        var ordinary = new DeploymentScript("seed", "INSERT INTO main.t VALUES (1)", null, DeploymentPhase.Pre);
        var detached = new DeploymentScript("vacuum", "VACUUM", null, DeploymentPhase.Post) { RunOutsideTransaction = true };

        var first = dialect.Generate(new ExecuteScript(ordinary)).Require().ShouldHaveSingleItem();
        var second = dialect.Generate(new ExecuteScript(detached)).Require().ShouldHaveSingleItem();

        first.Sql.Value.ShouldContain("INSERT");
        first.RunOutsideTransaction.ShouldBeFalse();
        second.Sql.Value.ShouldBe("VACUUM");
        second.RunOutsideTransaction.ShouldBeTrue();
    }

    [Fact]
    public Task ChangeScript_EmitsUserSqlVerbatimAtItsPlannedPosition() => VerifyRendering(
        // The script's SQL is user-authored for Sqlite and must appear exactly as written, between the
        // statements the linearizer placed it among — no translation, no quoting.
        new AddColumn(new(Schema, "users"), new Column { Name = "status", Type = SqlType.VarChar(20), IsNullable = true }),
        new ExecuteScript(new ChangeScript("backfill_status", "UPDATE \"users\" SET status = 'active' WHERE status IS NULL",
            Schema, ChangeTrigger.AddColumn, "users", "status")),
        new CreateIndex(new(Schema, "users"), new TableIndex { Name = "idx_users_status", Columns = ["status"] }));

    // ── Unsupported operations are error diagnostics that block the plan ────────

    public static TheoryData<string, MigrationAction> UnsupportedActions() => new()
    {
        { "alter column type", new AlterColumnType(new(Schema, "users", "id"), SqlType.Int, SqlType.BigInt) },
        { "alter column nullability", new AlterColumnNullability(new(Schema, "users", "email"), true, false) },
        { "set column default", new SetColumnDefault(new(Schema, "users", "email"), null, "'x'") },
        {
            "add foreign key to existing table",
            new AddForeignKey(new(Schema, "orders"), new ForeignKey
            {
                Name = "fk", ColumnNames = ["uid"], References = new(Schema, "users"), ReferencedColumnNames = ["id"],
            })
        },
        { "add unique to existing table", new AddUniqueConstraint(new(Schema, "orders"), new UniqueConstraint { Name = "uq", ColumnNames = ["code"] }) },
        { "add check to existing table", new AddCheckConstraint(new(Schema, "orders"), new CheckConstraint { Name = "ck", Expression = "x > 0" }) },
        { "add primary key", new AddPrimaryKey(new(Schema, "users"), new PrimaryKey { Name = "pk", ColumnNames = ["id"] }) },
        { "create schema", new CreateSchema("other") },
        { "rename view", new RenameView(new(Schema, "old_v"), "new_v") },
        { "materialized view", new CreateView(Schema, new View { Name = "mv", Body = "SELECT 1", IsMaterialized = true }) },
        {
            "function trigger",
            new CreateTrigger(new(Schema, "users"), new Trigger
            {
                Name = "t", Timing = TriggerTiming.After, Events = TriggerEvent.Insert, Function = new RoutineReference(Schema, "f"),
            })
        },
        { "sequence", new CreateSequence(Schema, new Sequence { Name = "s" }) },
        { "enum", new CreateEnum(Schema, new EnumType { Name = "e", Values = [new("a"), new("b")] }) },
        { "routine", new CreateRoutine(Schema, new Routine { Name = "f", RoutineKind = RoutineKind.Function, Arguments = "", Definition = "AS $$ SELECT 1 $$" }) },
        { "grant", new GrantTablePrivileges(new(Schema, "users"), "app", TablePrivilege.Select) },
        { "identity column", new CreateTable(Schema, new Table { Name = "c", Columns = [new Column { Name = "id", Type = SqlType.BigInt, IsIdentity = true }] }) },
    };

    [Theory]
    [MemberData(nameof(UnsupportedActions))]
    public void UnsupportedAction_IsAnErrorDiagnostic(string scenario, MigrationAction action)
    {
        _ = scenario;

        var rendered = new SqliteSqlDialect().Generate(action);

        rendered.IsFailure.ShouldBeTrue();
        rendered.Errors.ShouldNotBeEmpty();
    }
}
