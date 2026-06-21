using NSchema.Schema.Model.Columns;
using NSchema.Schema.Model.Tables;
using NSchema.Sqlite.Tests.Fixtures;

namespace NSchema.Sqlite.Tests.Sql;

/// <summary>
/// Introspection-focused tests for <see cref="NSchema.Sqlite.Sql.SqliteSchemaProvider"/>, including reading
/// databases created by hand (not by the generator) to prove the <c>sqlite_master</c> SQL parsing recovers the
/// constraint names and expressions that Sqlite's PRAGMAs hide.
/// </summary>
public sealed class SqliteSchemaProviderTests : SqliteTestBase
{
    [Fact]
    public async Task EmptyDatabase_ReportsTheMainSchemaWithNothingInIt()
    {
        // 'main' is always returned so the schema itself is never planned as a create.
        var schema = (await Introspect()).Schemas.ShouldHaveSingleItem();
        schema.Name.ShouldBe("main");
        schema.Tables.ShouldBeEmpty();
        schema.Views.ShouldBeEmpty();
    }

    [Fact]
    public async Task NamedConstraints_AreRecoveredFromTheStoredSql()
    {
        await Exec("""
            CREATE TABLE "parents" (
                "id" bigint NOT NULL,
                CONSTRAINT "pk_parents" PRIMARY KEY ("id")
            )
            """);
        await Exec("""
            CREATE TABLE "children" (
                "id" bigint NOT NULL,
                "parent_id" bigint NOT NULL,
                "code" varchar(20) NOT NULL,
                CONSTRAINT "pk_children" PRIMARY KEY ("id"),
                CONSTRAINT "uq_children_code" UNIQUE ("code"),
                CONSTRAINT "ck_children_code" CHECK (length(code) > 0),
                CONSTRAINT "fk_children_parent" FOREIGN KEY ("parent_id") REFERENCES "parents" ("id") ON DELETE CASCADE
            )
            """);

        var children = (await Introspect()).Schemas[0].Tables.Single(t => t.Name == "children");

        children.PrimaryKey!.Name.ShouldBe("pk_children");
        children.UniqueConstraints.ShouldHaveSingleItem().Name.ShouldBe("uq_children_code");
        children.CheckConstraints.ShouldHaveSingleItem().Name.ShouldBe("ck_children_code");
        var fk = children.ForeignKeys.ShouldHaveSingleItem();
        fk.Name.ShouldBe("fk_children_parent");
        fk.ReferencedTable.ShouldBe("parents");
        fk.OnDelete.ShouldBe(ReferentialAction.Cascade);
    }

    [Fact]
    public async Task InlineIntegerPrimaryKey_GetsASynthesizedName()
    {
        // A hand-written, unnamed inline primary key (the most common real-world form) has no name to recover, so
        // the provider synthesizes a deterministic one rather than dropping the key.
        await Exec("CREATE TABLE \"widgets\" (\"id\" INTEGER PRIMARY KEY, \"name\" TEXT)");

        var widgets = (await Introspect()).Schemas[0].Tables.Single(t => t.Name == "widgets");
        widgets.PrimaryKey.ShouldNotBeNull();
        widgets.PrimaryKey!.Name.ShouldBe("pk_widgets");
        widgets.PrimaryKey.ColumnNames.ShouldBe(["id"]);
    }

    [Fact]
    public async Task Columns_MapDeclaredTypesBackToSqlType()
    {
        await Exec("""
            CREATE TABLE "t" (
                "a" bigint NOT NULL,
                "b" varchar(50),
                "c" decimal(12,3) NOT NULL,
                "d" text,
                "e" boolean NOT NULL
            )
            """);

        var columns = (await Introspect()).Schemas[0].Tables.Single().Columns;
        columns.Single(c => c.Name == "a").Type.ShouldBe(SqlType.BigInt);
        columns.Single(c => c.Name == "b").Type.ShouldBe(SqlType.VarChar(50));
        columns.Single(c => c.Name == "c").Type.ShouldBe(SqlType.Decimal(12, 3));
        columns.Single(c => c.Name == "d").Type.ShouldBe(SqlType.Text);
        columns.Single(c => c.Name == "e").Type.ShouldBe(SqlType.Boolean);
        columns.Single(c => c.Name == "a").IsNullable.ShouldBeFalse();
        columns.Single(c => c.Name == "b").IsNullable.ShouldBeTrue();
    }

    [Fact]
    public async Task AutoIndexesAreNotReportedAsIndexes_OnlyExplicitCreateIndex()
    {
        // A UNIQUE constraint's backing auto-index has a NULL sql in sqlite_master and must surface as a unique
        // constraint, not a TableIndex; only an explicit CREATE INDEX is an index.
        await Exec("""
            CREATE TABLE "t" (
                "id" bigint NOT NULL,
                "code" text NOT NULL,
                CONSTRAINT "uq_t_code" UNIQUE ("code")
            )
            """);
        await Exec("CREATE INDEX \"idx_t_id\" ON \"t\" (\"id\")");

        var table = (await Introspect()).Schemas[0].Tables.Single();
        table.UniqueConstraints.ShouldHaveSingleItem().Name.ShouldBe("uq_t_code");
        table.Indexes.ShouldHaveSingleItem().Name.ShouldBe("idx_t_id");
    }
}
