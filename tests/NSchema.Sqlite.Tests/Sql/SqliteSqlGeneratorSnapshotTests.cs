using NSchema.Plan.Model;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Enums;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Routines;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Sequence;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;
using NSchema.Schema.Model.Columns;
using NSchema.Schema.Model.Constraints;
using NSchema.Schema.Model.Enums;
using NSchema.Schema.Model.Indexes;
using NSchema.Schema.Model.Routines;
using NSchema.Schema.Model.Scripts;
using NSchema.Schema.Model.Sequences;
using NSchema.Schema.Model.Tables;
using NSchema.Schema.Model.Triggers;
using NSchema.Schema.Model.Views;
using NSchema.Sql;
using NSchema.Sqlite.Sql;

namespace NSchema.Sqlite.Tests.Sql;

/// <summary>
/// Snapshot tests for <see cref="SqliteSqlGenerator"/>: they assert on the exact SQL text the generator emits
/// (no database required). Snapshots live alongside this file as <c>*.verified.txt</c>; review and commit them
/// when the generated SQL intentionally changes. Behaviour that depends on a real database (round-trips through
/// introspection) lives in <see cref="SqliteSqlGeneratorTests"/>.
/// </summary>
public sealed class SqliteSqlGeneratorSnapshotTests
{
    private static readonly ISqlGenerator Generator = new SqliteSqlGenerator();

    private const string Schema = "main";

    private static Task VerifyPlan(params MigrationAction[] actions) =>
        Verify(Generator.Generate(new MigrationPlan(actions, [], [])));

    // ── Tables ──────────────────────────────────────────────────────────────────

    [Fact]
    public Task CreateTable_WithColumnsAndPrimaryKey() => VerifyPlan(
        new CreateTable(Schema, new Table("users",
            PrimaryKey: new PrimaryKey("pk_users", ["id"]),
            Columns:
            [
                new Column("id", SqlType.BigInt, IsNullable: false),
                new Column("email", SqlType.VarChar(255), IsNullable: false),
                new Column("created_at", SqlType.DateTimeOffset, IsNullable: false, DefaultExpression: "CURRENT_TIMESTAMP"),
                new Column("notes", SqlType.Text),
            ])));

    [Fact]
    public Task CreateTable_WithAllConstraints_InlinesThemAndFoldsSeparateAddActions()
    {
        // The linearizer creates a table with only columns + PK inline, then emits the foreign keys, unique and
        // check constraints as separate Add* actions. Sqlite can't ALTER TABLE ADD CONSTRAINT, so the generator
        // inlines them into the CREATE TABLE and folds the separate actions away — the snapshot must show one
        // CREATE TABLE carrying everything, and nothing after it.
        var table = new Table("orders",
            PrimaryKey: new PrimaryKey("pk_orders", ["id"]),
            Columns:
            [
                new Column("id", SqlType.BigInt, IsNullable: false),
                new Column("user_id", SqlType.BigInt, IsNullable: false),
                new Column("code", SqlType.VarChar(20), IsNullable: false),
                new Column("total", SqlType.Decimal(10, 2), IsNullable: false),
            ],
            ForeignKeys: [new ForeignKey("fk_orders_user", ["user_id"], Schema, "users", ["id"], OnDelete: ReferentialAction.Cascade)],
            UniqueConstraints: [new UniqueConstraint("uq_orders_code", ["code"])],
            CheckConstraints: [new CheckConstraint("ck_orders_total", "total >= 0")]);

        return VerifyPlan(
            new CreateTable(Schema, table),
            new AddForeignKey(Schema, "orders", table.ForeignKeys[0]),
            new AddUniqueConstraint(Schema, "orders", table.UniqueConstraints[0]),
            new AddCheckConstraint(Schema, "orders", table.CheckConstraints[0]));
    }

    [Fact]
    public Task TableLifecycle() => VerifyPlan(
        new RenameTable(Schema, "old_users", "users"),
        new DropTable(Schema, "legacy"));

    // ── Columns ───────────────────────────────────────────────────────────────

    [Fact]
    public Task ColumnOperations() => VerifyPlan(
        new AddColumn(Schema, "users", new Column("age", SqlType.Int)),
        new AddColumn(Schema, "users", new Column("status", SqlType.VarChar(20), IsNullable: false, DefaultExpression: "'active'")),
        new RenameColumn(Schema, "users", "age", "years"),
        new DropColumn(Schema, "users", new Column("years", SqlType.Int)));

    [Fact]
    public Task GeneratedColumn() => VerifyPlan(
        new CreateTable(Schema, new Table("boxes",
            Columns:
            [
                new Column("w", SqlType.Int, IsNullable: false),
                new Column("h", SqlType.Int, IsNullable: false),
                new Column("area", SqlType.Int, GeneratedExpression: "w * h"),
            ])));

    // ── Indexes ───────────────────────────────────────────────────────────────

    [Fact]
    public Task IndexOperations() => VerifyPlan(
        new CreateIndex(Schema, "users", new TableIndex("idx_users_email", ["email"], IsUnique: true)),
        new CreateIndex(Schema, "users", new TableIndex("idx_users_active", ["created_at"], Predicate: "notes IS NOT NULL")),
        new CreateIndex(Schema, "users", new TableIndex("idx_users_recent",
            [new IndexColumn("created_at", Sort: IndexSort.Descending), new IndexColumn("lower(email)", IsExpression: true)])),
        new DropIndex(Schema, "users", "idx_users_email"));

    // ── Views ─────────────────────────────────────────────────────────────────

    [Fact]
    public Task ViewOperations() => VerifyPlan(
        // CreateView serves both add and body-modify; Sqlite has no CREATE OR REPLACE, so each is DROP + CREATE.
        new CreateView(Schema, new View("active_users", "SELECT id, email FROM main.users WHERE active")),
        new DropView(Schema, "active_users"));

    // ── Type mapping (canonical NSchema type names, applied via CREATE TABLE) ────

    [Fact]
    public Task TypeMapping_CoversAllSqlTypes() => VerifyPlan(
        new CreateTable(Schema, new Table("types", Columns:
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
        ])));

    private static Column Col(string name, SqlType type) => new(name, type);

    // ── Comments are no-ops (Sqlite has no COMMENT ON) ──────────────────────────

    [Fact]
    public void CommentActions_ProduceNoStatements()
    {
        var plan = Generator.Generate(new MigrationPlan(
            [
                new SetTableComment(Schema, "users", null, "Registered users"),
                new SetColumnComment(Schema, "users", "email", null, "Login address"),
            ], [], []));

        plan.IsEmpty.ShouldBeTrue();
    }

    // ── Deployment scripts pass through ─────────────────────────────────────────

    [Fact]
    public void DeploymentScripts_PassThrough_PreservingRunOutsideTransaction()
    {
        var ordinary = new Script("seed", "INSERT INTO main.t VALUES (1)", ScriptType.PreDeployment);
        var detached = new Script("vacuum", "VACUUM", ScriptType.PostDeployment) { RunOutsideTransaction = true };

        var plan = Generator.Generate(new MigrationPlan([], [ordinary], [detached]));

        plan.Statements.Single(s => s.Sql.Contains("INSERT")).RunOutsideTransaction.ShouldBeFalse();
        plan.Statements.Single(s => s.Sql.Contains("VACUUM")).RunOutsideTransaction.ShouldBeTrue();
    }

    // ── Unsupported operations throw a clear NotSupportedException ───────────────

    public static TheoryData<string, MigrationAction> UnsupportedActions() => new()
    {
        { "alter column type", new AlterColumnType(Schema, "users", "id", SqlType.Int, SqlType.BigInt) },
        { "alter column nullability", new AlterColumnNullability(Schema, "users", "email", true, false) },
        { "set column default", new SetColumnDefault(Schema, "users", "email", null, "'x'") },
        { "add foreign key to existing table", new AddForeignKey(Schema, "orders", new ForeignKey("fk", ["uid"], Schema, "users", ["id"])) },
        { "add unique to existing table", new AddUniqueConstraint(Schema, "orders", new UniqueConstraint("uq", ["code"])) },
        { "add check to existing table", new AddCheckConstraint(Schema, "orders", new CheckConstraint("ck", "x > 0")) },
        { "add primary key", new AddPrimaryKey(Schema, "users", new PrimaryKey("pk", ["id"])) },
        { "create schema", new CreateSchema("other") },
        { "rename view", new RenameView(Schema, "old_v", "new_v") },
        { "materialized view", new CreateView(Schema, new View("mv", "SELECT 1", IsMaterialized: true)) },
        { "trigger", new CreateTrigger(Schema, "users", new Trigger("t", TriggerTiming.After, TriggerEvent.Insert, "main.f")) },
        { "sequence", new CreateSequence(Schema, new Sequence("s")) },
        { "enum", new CreateEnum(Schema, new EnumType("e", ["a", "b"])) },
        { "routine", new CreateRoutine(Schema, new Routine("f", RoutineKind.Function, "", "AS $$ SELECT 1 $$")) },
        { "grant", new GrantTablePrivileges(Schema, "users", "app", TablePrivilege.Select) },
        { "identity column", new CreateTable(Schema, new Table("c", Columns: [new Column("id", SqlType.BigInt, IsIdentity: true)])) },
    };

    [Theory]
    [MemberData(nameof(UnsupportedActions))]
    public void UnsupportedAction_Throws(string scenario, MigrationAction action)
    {
        _ = scenario;
        Should.Throw<NotSupportedException>(() => Generator.Generate(new MigrationPlan([action], [], [])));
    }
}
