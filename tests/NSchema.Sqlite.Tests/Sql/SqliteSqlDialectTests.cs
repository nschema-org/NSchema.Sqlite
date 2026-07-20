using NSchema.Model;
using NSchema.Model.Columns;
using NSchema.Model.Constraints;
using NSchema.Model.Indexes;
using NSchema.Model.Tables;
using NSchema.Model.Triggers;
using NSchema.Model.Views;
using NSchema.Plan.Model.Columns;
using NSchema.Plan.Model.Constraints;
using NSchema.Plan.Model.Indexes;
using NSchema.Plan.Model.Schemas;
using NSchema.Plan.Model.Tables;
using NSchema.Plan.Model.Triggers;
using NSchema.Plan.Model.Views;
using NSchema.Sqlite.Tests.Fixtures;

namespace NSchema.Sqlite.Tests.Sql;

/// <summary>
/// Executes the rendered SQL against a real Sqlite database and asserts on the result — both that the DDL is
/// valid Sqlite and that what is applied reads back through the introspector unchanged (no phantom drift on a re-plan).
/// </summary>
public sealed class SqliteSqlDialectTests : SqliteTestBase
{
    private const string Schema = "main";

    // ── Tables and columns ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTable_CreatesTableWithColumns()
    {
        await Apply(new CreateTable(Schema, new Table
        {
            Name = "users",
            PrimaryKey = new PrimaryKey { Name = "pk_users", ColumnNames = ["id"] },
            Columns =
            [
                new Column { Name = "id", Type = SqlType.BigInt },
                new Column { Name = "email", Type = SqlType.VarChar(255) },
            ],
        }));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'users'")).ShouldBe(1);
    }

    [Fact]
    public async Task DropTable_RemovesTable()
    {
        await Exec("CREATE TABLE \"products\" (id integer)");

        await Apply(new DropTable(new(Schema, "products")));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'products'")).ShouldBe(0);
    }

    [Fact]
    public async Task Teardown_DropsTablesThenNoOpsTheMainSchema()
    {
        await Exec("CREATE TABLE \"widgets\" (id integer)");

        // A teardown emits the explicit table drop plus DropSchema("main"). The implicit 'main' schema can't be
        // dropped, so that action is a no-op rather than an error — and the table is still removed.
        await Apply(new DropTable(new(Schema, "widgets")), new DropSchema(Schema));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'widgets'")).ShouldBe(0);
    }

    [Fact]
    public async Task Apply_CreateSchemaMain_IsANoOp_AndTheTableStillLands()
    {
        // Planning against a state store that does not record the implicit 'main' schema emits CreateSchema("main")
        // ahead of the table. 'main' always exists, so that action must be a no-op (not an error), and the table that
        // follows it must still be created.
        await Apply(
            new CreateSchema(Schema),
            new CreateTable(Schema, new Table { Name = "widgets", Columns = [new Column { Name = "id", Type = SqlType.Int }] }));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'widgets'")).ShouldBe(1);
    }

    [Fact]
    public async Task RenameTable_RenamesTable()
    {
        await Exec("CREATE TABLE \"old_name\" (id integer)");

        await Apply(new RenameTable(new(Schema, "old_name"), "new_name"));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'new_name'")).ShouldBe(1);
    }

    [Fact]
    public async Task AddColumn_AddsColumn()
    {
        await Exec("CREATE TABLE \"items\" (id integer)");

        await Apply(new AddColumn(new(Schema, "items"), new Column { Name = "name", Type = SqlType.VarChar(100), DefaultExpression = "''" }));

        (await Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = 'name'")).ShouldBe(1);
    }

    [Fact]
    public async Task DropColumn_RemovesColumn()
    {
        await Exec("CREATE TABLE \"items\" (id integer, name text)");

        await Apply(new DropColumn(new(Schema, "items"), new Column { Name = "name", Type = SqlType.Text }));

        (await Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = 'name'")).ShouldBe(0);
    }

    [Fact]
    public async Task RenameColumn_RenamesColumn()
    {
        await Exec("CREATE TABLE \"items\" (id integer, old_col text)");

        await Apply(new RenameColumn(new(Schema, "items", "old_col"), "new_col"));

        (await Scalar<long>("SELECT COUNT(*) FROM pragma_table_info('items') WHERE name = 'new_col'")).ShouldBe(1);
    }

    // ── Indexes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateIndex_CreatesIndex()
    {
        await Exec("CREATE TABLE \"items\" (id integer, name text)");

        await Apply(new CreateIndex(new(Schema, "items"), new TableIndex { Name = "idx_items_name", Columns = ["name"], IsUnique = true }));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_items_name'")).ShouldBe(1);
    }

    [Fact]
    public async Task DropIndex_RemovesIndex()
    {
        await Exec("CREATE TABLE \"items\" (id integer, name text)");
        await Exec("CREATE INDEX \"idx_items_name\" ON \"items\" (name)");

        await Apply(new DropIndex(new(Schema, "items", "idx_items_name")));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'idx_items_name'")).ShouldBe(0);
    }

    // ── Views ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateView_CreatesView()
    {
        await Exec("CREATE TABLE \"users\" (id integer, active integer)");

        await Apply(new CreateView(Schema, new View { Name = "active_users", Body = "SELECT id FROM \"users\" WHERE active" }));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'view' AND name = 'active_users'")).ShouldBe(1);
    }

    [Fact]
    public async Task CreateView_OnExistingView_ReplacesDefinition()
    {
        // CreateView serves both add and body-modify; Sqlite has no CREATE OR REPLACE, so the dialect drops first.
        await Exec("CREATE TABLE \"users\" (id integer, email text)");
        await Exec("CREATE VIEW \"u\" AS SELECT id FROM \"users\"");

        await Apply(new CreateView(Schema, new View { Name = "u", Body = "SELECT id, email FROM \"users\"" }));

        (await Scalar<string>("SELECT sql FROM sqlite_master WHERE name = 'u'")).ShouldContain("email");
    }

    [Fact]
    public async Task DropView_RemovesView()
    {
        await Exec("CREATE TABLE \"users\" (id integer)");
        await Exec("CREATE VIEW \"u\" AS SELECT id FROM \"users\"");

        await Apply(new DropView(new(Schema, "u")));

        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE name = 'u'")).ShouldBe(0);
    }

    // ── Triggers ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_Trigger_IntrospectsToTheSameStructureAndFires()
    {
        await Exec("CREATE TABLE \"users\" (id integer, email text, name text)");
        await Exec("CREATE TABLE \"audit\" (msg text)");
        // A trigger body can't use schema-qualified table names (a Sqlite rule), so it writes plain `audit`.
        var trigger = new Trigger
        {
            Name = "users_audit",
            Timing = TriggerTiming.After,
            Events = TriggerEvent.Update,
            Level = TriggerLevel.Row,
            UpdateOfColumns = ["email"],
            When = "new.email IS NOT NULL",
            Body = "BEGIN INSERT INTO audit (msg) VALUES (new.email); END",
        };

        await Apply(new CreateTrigger(new(Schema, "users"), trigger));

        // What was applied reads back identically — timing, single event, UPDATE OF, row-level, WHEN and the body
        // (structural equality excludes the comment).
        var introspected = (await Introspect()).Schemas[0].Tables.Single(t => t.Name.Value == "users").Triggers.ShouldHaveSingleItem();
        introspected.ShouldBe(trigger);
        introspected.Function.ShouldBeNull();

        // The trigger actually fires: updating the email lands an audit row.
        await Exec("INSERT INTO \"users\" (id, email) VALUES (1, 'a@b.com')");
        await Exec("UPDATE \"users\" SET email = 'c@d.com' WHERE id = 1");
        (await Scalar<long>("SELECT COUNT(*) FROM audit WHERE msg = 'c@d.com'")).ShouldBe(1);

        await Apply(new DropTrigger(new(Schema, "users", "users_audit")));
        (await Scalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'users_audit'")).ShouldBe(0);
    }

    [Fact]
    public async Task RoundTrip_BeforeDeleteTrigger_WithoutForEachRow_IntrospectsAsStatementLevel()
    {
        // No FOR EACH ROW emitted (Level defaults to Statement), so introspection reads it back as Statement —
        // a faithful round-trip even though Sqlite physically fires per row.
        await Exec("CREATE TABLE \"users\" (id integer)");
        await Exec("CREATE TABLE \"audit\" (msg text)");
        var trigger = new Trigger
        {
            Name = "users_block",
            Timing = TriggerTiming.Before,
            Events = TriggerEvent.Delete,
            Body = "BEGIN INSERT INTO audit (msg) VALUES ('deleting'); END",
        };

        await Apply(new CreateTrigger(new(Schema, "users"), trigger));

        (await Introspect()).Schemas[0].Tables.Single(t => t.Name.Value == "users").Triggers.ShouldHaveSingleItem().ShouldBe(trigger);
    }

    // ── Round-trips (render → execute → introspect) ─────────────────────────────

    [Fact]
    public async Task RoundTrip_RichTable_IntrospectsToTheSameStructure()
    {
        // A table with every kind of constraint NSchema can carry on Sqlite. The linearizer folds them into the
        // CREATE TABLE (Sqlite has no ALTER TABLE ADD CONSTRAINT). What is applied must read back with the
        // author's constraint names intact, or a re-plan would churn.
        var users = new Table
        {
            Name = "users",
            PrimaryKey = new PrimaryKey { Name = "pk_users", ColumnNames = ["id"] },
            Columns = [new Column { Name = "id", Type = SqlType.BigInt }],
        };

        var orders = new Table
        {
            Name = "orders",
            PrimaryKey = new PrimaryKey { Name = "pk_orders", ColumnNames = ["id"] },
            Columns =
            [
                new Column { Name = "id", Type = SqlType.BigInt },
                new Column { Name = "user_id", Type = SqlType.BigInt },
                new Column { Name = "code", Type = SqlType.VarChar(20) },
                new Column { Name = "total", Type = SqlType.Decimal(10, 2), DefaultExpression = "0" },
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

        await Apply(
            new CreateTable(Schema, users),
            new CreateTable(Schema, orders),
            new CreateIndex(new(Schema, "orders"), new TableIndex { Name = "idx_orders_user", Columns = ["user_id"] }));

        var introspected = (await Introspect()).Schemas[0].Tables.Single(t => t.Name.Value == "orders");

        introspected.PrimaryKey.ShouldBe(new PrimaryKey { Name = "pk_orders", ColumnNames = ["id"] });
        introspected.ForeignKeys.ShouldHaveSingleItem().ShouldBe(new ForeignKey
        {
            Name = "fk_orders_user",
            ColumnNames = ["user_id"],
            References = new(Schema, "users"),
            ReferencedColumnNames = ["id"],
            OnDelete = ReferentialAction.Cascade,
        });
        introspected.UniqueConstraints.ShouldHaveSingleItem().ShouldBe(new UniqueConstraint { Name = "uq_orders_code", ColumnNames = ["code"] });
        var check = introspected.CheckConstraints.ShouldHaveSingleItem();
        check.Name.Value.ShouldBe("ck_orders_total");
        check.Expression.Value.ShouldBe("total >= 0");
        introspected.Indexes.ShouldHaveSingleItem().Name.Value.ShouldBe("idx_orders_user");

        // Columns read back with the same types/nullability/defaults.
        introspected.Columns.Select(c => (c.Name.Value, c.Type, c.IsNullable)).ShouldBe(
        [
            ("id", SqlType.BigInt, false),
            ("user_id", SqlType.BigInt, false),
            ("code", SqlType.VarChar(20), false),
            ("total", SqlType.Decimal(10, 2), false),
        ]);
        introspected.Columns.Single(c => c.Name.Value == "total").DefaultExpression.ShouldBe(new SqlText("0"));
    }

    [Fact]
    public async Task RoundTrip_GeneratedColumn_IntrospectsAsGenerated()
    {
        await Apply(new CreateTable(Schema, new Table
        {
            Name = "boxes",
            Columns =
            [
                new Column { Name = "w", Type = SqlType.Int },
                new Column { Name = "h", Type = SqlType.Int },
                new Column { Name = "area", Type = SqlType.Int, IsNullable = true, GeneratedExpression = "w * h" },
            ],
        }));

        var area = (await Introspect()).Schemas[0].Tables.Single().Columns.Single(c => c.Name.Value == "area");
        area.GeneratedExpression.ShouldBe(new SqlText("w * h"));
        area.DefaultExpression.ShouldBeNull();
    }

    [Fact]
    public async Task RoundTrip_Index_PreservesUniquenessOrderingAndExpression()
    {
        await Exec("CREATE TABLE \"items\" (id integer, name text, qty integer)");

        await Apply(new CreateIndex(new(Schema, "items"), new TableIndex
        {
            Name = "idx_items_rich",
            Columns = [new IndexColumn(new SqlIdentifier("name"), Sort: IndexSort.Descending), new IndexColumn(Expression: new SqlText("lower(name)"))],
            IsUnique = true,
            Predicate = "qty > 0",
        }));

        var index = (await Introspect()).Schemas[0].Tables.Single(t => t.Name.Value == "items").Indexes.ShouldHaveSingleItem();
        index.IsUnique.ShouldBeTrue();
        index.Predicate.ShouldNotBeNull().Value.ShouldContain("qty > 0");
        index.Columns[0].ShouldBe(new IndexColumn(new SqlIdentifier("name"), Sort: IndexSort.Descending));
        index.Columns[1].Expression.ShouldNotBeNull().Value.ShouldContain("lower");
    }

    [Fact]
    public async Task RoundTrip_View_IntrospectsWithBody()
    {
        await Exec("CREATE TABLE \"users\" (id integer, active integer)");

        await Apply(new CreateView(Schema, new View { Name = "active_users", Body = "SELECT id FROM \"users\" WHERE active" }));

        var view = (await Introspect()).Schemas[0].Views.ShouldHaveSingleItem();
        view.Name.Value.ShouldBe("active_users");
        view.Body.Value.ShouldContain("SELECT id");
        view.IsMaterialized.ShouldBeFalse();
    }
}
