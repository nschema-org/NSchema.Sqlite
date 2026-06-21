using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Views;
using NSchema.Schema.Model.Columns;
using NSchema.Schema.Model.Constraints;
using NSchema.Schema.Model.Indexes;
using NSchema.Schema.Model.Tables;
using NSchema.Schema.Model.Views;
using NSchema.Sqlite.Tests.Fixtures;

namespace NSchema.Sqlite.Tests.Sql;

/// <summary>
/// Executes the generated SQL against a real Sqlite database and asserts on the result — both that the DDL is
/// valid Sqlite and that what is applied reads back through the provider unchanged (no phantom drift on a re-plan).
/// </summary>
public sealed class SqliteSqlGeneratorTests : SqliteTestBase
{
    private const string Schema = "main";

    // ── Tables and columns ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTable_CreatesTableWithColumns()
    {
        await Apply(new CreateTable(Schema, new Table("users",
            PrimaryKey: new PrimaryKey("pk_users", ["id"]),
            Columns:
            [
                new Column("id", SqlType.BigInt, IsNullable: false),
                new Column("email", SqlType.VarChar(255), IsNullable: false),
            ])));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'users'")).ShouldBe(1);
    }

    [Fact]
    public async Task DropTable_RemovesTable()
    {
        await Exec("CREATE TABLE \"products\" (id integer)");

        await Apply(new DropTable(Schema, "products"));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'products'")).ShouldBe(0);
    }

    [Fact]
    public async Task RenameTable_RenamesTable()
    {
        await Exec("CREATE TABLE \"old_name\" (id integer)");

        await Apply(new RenameTable(Schema, "old_name", "new_name"));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'new_name'")).ShouldBe(1);
    }

    [Fact]
    public async Task AddColumn_AddsColumn()
    {
        await Exec("CREATE TABLE \"items\" (id integer)");

        await Apply(new AddColumn(Schema, "items", new Column("name", SqlType.VarChar(100), IsNullable: false, DefaultExpression: "''")));

        (await Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = 'name'")).ShouldBe(1);
    }

    [Fact]
    public async Task DropColumn_RemovesColumn()
    {
        await Exec("CREATE TABLE \"items\" (id integer, name text)");

        await Apply(new DropColumn(Schema, "items", new Column("name", SqlType.Text)));

        (await Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = 'name'")).ShouldBe(0);
    }

    [Fact]
    public async Task RenameColumn_RenamesColumn()
    {
        await Exec("CREATE TABLE \"items\" (id integer, old_col text)");

        await Apply(new RenameColumn(Schema, "items", "old_col", "new_col"));

        (await Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = 'new_col'")).ShouldBe(1);
    }

    // ── Indexes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateIndex_CreatesIndex()
    {
        await Exec("CREATE TABLE \"items\" (id integer, name text)");

        await Apply(new CreateIndex(Schema, "items", new TableIndex("idx_items_name", ["name"], IsUnique: true)));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_items_name'")).ShouldBe(1);
    }

    [Fact]
    public async Task DropIndex_RemovesIndex()
    {
        await Exec("CREATE TABLE \"items\" (id integer, name text)");
        await Exec("CREATE INDEX \"idx_items_name\" ON \"items\" (name)");

        await Apply(new DropIndex(Schema, "items", "idx_items_name"));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'idx_items_name'")).ShouldBe(0);
    }

    // ── Views ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateView_CreatesView()
    {
        await Exec("CREATE TABLE \"users\" (id integer, active integer)");

        await Apply(new CreateView(Schema, new View("active_users", "SELECT id FROM \"users\" WHERE active")));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'view' AND name = 'active_users'")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateView_OnExistingView_ReplacesDefinition()
    {
        // CreateView serves both add and body-modify; Sqlite has no CREATE OR REPLACE, so the generator drops first.
        await Exec("CREATE TABLE \"users\" (id integer, email text)");
        await Exec("CREATE VIEW \"u\" AS SELECT id FROM \"users\"");

        await Apply(new CreateView(Schema, new View("u", "SELECT id, email FROM \"users\"")));

        (await Scalar<string>("SELECT sql FROM sqlite_master WHERE name = 'u'")).ShouldContain("email");
    }

    [Fact]
    public async Task DropView_RemovesView()
    {
        await Exec("CREATE TABLE \"users\" (id integer)");
        await Exec("CREATE VIEW \"u\" AS SELECT id FROM \"users\"");

        await Apply(new DropView(Schema, "u"));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'u'")).ShouldBe(0);
    }

    // ── Round-trips (generate → execute → introspect) ───────────────────────────

    [Fact]
    public async Task RoundTrip_RichTable_IntrospectsToTheSameStructure()
    {
        // A table with every kind of constraint NSchema can carry on Sqlite. The linearizer would emit the FK,
        // unique and check as separate Add* actions; the generator inlines them. What is applied must read back
        // with the author's constraint names intact, or a re-plan would churn.
        var users = new Table("users",
            PrimaryKey: new PrimaryKey("pk_users", ["id"]),
            Columns: [new Column("id", SqlType.BigInt, IsNullable: false)]);

        var orders = new Table("orders",
            PrimaryKey: new PrimaryKey("pk_orders", ["id"]),
            Columns:
            [
                new Column("id", SqlType.BigInt, IsNullable: false),
                new Column("user_id", SqlType.BigInt, IsNullable: false),
                new Column("code", SqlType.VarChar(20), IsNullable: false),
                new Column("total", SqlType.Decimal(10, 2), IsNullable: false, DefaultExpression: "0"),
            ],
            ForeignKeys: [new ForeignKey("fk_orders_user", ["user_id"], Schema, "users", ["id"], OnDelete: ReferentialAction.Cascade)],
            UniqueConstraints: [new UniqueConstraint("uq_orders_code", ["code"])],
            CheckConstraints: [new CheckConstraint("ck_orders_total", "total >= 0")]);

        await Apply(
            new CreateTable(Schema, users),
            new CreateTable(Schema, orders),
            new AddForeignKey(Schema, "orders", orders.ForeignKeys[0]),
            new AddUniqueConstraint(Schema, "orders", orders.UniqueConstraints[0]),
            new AddCheckConstraint(Schema, "orders", orders.CheckConstraints[0]),
            new CreateIndex(Schema, "orders", new TableIndex("idx_orders_user", ["user_id"])));

        var introspected = (await Introspect()).Schemas[0].Tables.Single(t => t.Name == "orders");

        introspected.PrimaryKey.ShouldBe(new PrimaryKey("pk_orders", ["id"]));
        introspected.ForeignKeys.ShouldHaveSingleItem()
            .ShouldBe(new ForeignKey("fk_orders_user", ["user_id"], Schema, "users", ["id"], OnDelete: ReferentialAction.Cascade));
        introspected.UniqueConstraints.ShouldHaveSingleItem().ShouldBe(new UniqueConstraint("uq_orders_code", ["code"]));
        var check = introspected.CheckConstraints.ShouldHaveSingleItem();
        check.Name.ShouldBe("ck_orders_total");
        check.Expression.ShouldBe("total >= 0");
        introspected.Indexes.ShouldHaveSingleItem().Name.ShouldBe("idx_orders_user");

        // Columns read back with the same types/nullability/defaults.
        introspected.Columns.Select(c => (c.Name, c.Type, c.IsNullable)).ShouldBe(
        [
            ("id", SqlType.BigInt, false),
            ("user_id", SqlType.BigInt, false),
            ("code", SqlType.VarChar(20), false),
            ("total", SqlType.Decimal(10, 2), false),
        ]);
        introspected.Columns.Single(c => c.Name == "total").DefaultExpression.ShouldBe("0");
    }

    [Fact]
    public async Task RoundTrip_GeneratedColumn_IntrospectsAsGenerated()
    {
        await Apply(new CreateTable(Schema, new Table("boxes", Columns:
        [
            new Column("w", SqlType.Int, IsNullable: false),
            new Column("h", SqlType.Int, IsNullable: false),
            new Column("area", SqlType.Int, GeneratedExpression: "w * h"),
        ])));

        var area = (await Introspect()).Schemas[0].Tables.Single().Columns.Single(c => c.Name == "area");
        area.GeneratedExpression.ShouldNotBeNull();
        area.GeneratedExpression!.ShouldBe("w * h");
        area.DefaultExpression.ShouldBeNull();
    }

    [Fact]
    public async Task RoundTrip_Index_PreservesUniquenessOrderingAndExpression()
    {
        await Exec("CREATE TABLE \"items\" (id integer, name text, qty integer)");

        await Apply(new CreateIndex(Schema, "items", new TableIndex("idx_items_rich",
            [new IndexColumn("name", Sort: IndexSort.Descending), new IndexColumn("lower(name)", IsExpression: true)],
            IsUnique: true, Predicate: "qty > 0")));

        var index = (await Introspect()).Schemas[0].Tables.Single(t => t.Name == "items").Indexes.ShouldHaveSingleItem();
        index.IsUnique.ShouldBeTrue();
        index.Predicate.ShouldNotBeNull();
        index.Predicate!.ShouldContain("qty > 0");
        index.Columns[0].ShouldBe(new IndexColumn("name", IsExpression: false, IndexSort.Descending));
        index.Columns[1].IsExpression.ShouldBeTrue();
        index.Columns[1].Expression.ShouldContain("lower");
    }

    [Fact]
    public async Task RoundTrip_View_IntrospectsWithBody()
    {
        await Exec("CREATE TABLE \"users\" (id integer, active integer)");

        await Apply(new CreateView(Schema, new View("active_users", "SELECT id FROM \"users\" WHERE active")));

        var view = (await Introspect()).Schemas[0].Views.ShouldHaveSingleItem();
        view.Name.ShouldBe("active_users");
        view.Body.ShouldContain("SELECT id");
        view.IsMaterialized.ShouldBeFalse();
    }
}
