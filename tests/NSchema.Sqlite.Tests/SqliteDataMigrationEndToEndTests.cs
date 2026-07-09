using NSchema.Operations.Apply;
using NSchema.Operations.Plan;
using NSchema.Sql.Model;
using NSchema.Sqlite.Tests.Fixtures;

namespace NSchema.Sqlite.Tests;

/// <summary>
/// Drives a declared <c>MIGRATION … FOR</c> data migration through the whole pipeline against a real SQLite
/// database: DDL on disk → plan (the migration matches its structural change and is spliced into the plan) →
/// apply → the backfill has really run.
/// </summary>
/// <remarks>
/// The canonical data-migration shape — a NOT NULL, no-default column add — is not exercisable here: the core
/// decomposes it into add-nullable → backfill → tighten to NOT NULL, and that final <c>AlterColumnNullability</c>
/// needs a table rebuild, which NSchema.Sqlite does not support. A nullable column add with a matched backfill
/// runs the same match-and-splice path end to end without the rebuild.
/// </remarks>
[Collection("sqlite-environment")]
public sealed class SqliteDataMigrationEndToEndTests : SqliteTestBase
{
    [Fact]
    public async Task Apply_WithAddColumnDataMigration_RunsTheBackfillAtItsPlannedPosition()
    {
        // Arrange — a live baseline with data, and a desired schema adding a column plus its backfill migration.
        // Everything lives under SQLite's single built-in schema, so the migration's target path uses `main`.
        await Exec("""
            CREATE TABLE "main"."users" (
                "id" bigint NOT NULL,
                CONSTRAINT "pk_users" PRIMARY KEY ("id")
            )
            """);
        await Exec("INSERT INTO \"users\" (\"id\") VALUES (1)");

        var projectDirectory = Directory.CreateTempSubdirectory("nschema-sqlite-migration-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "schema.sql"), """
                CREATE TABLE main.users (
                  id     bigint NOT NULL,
                  status varchar(20),
                  CONSTRAINT pk_users PRIMARY KEY (id)
                );

                MIGRATION 'backfill status' FOR ADD COLUMN main.users.status AS $$
                UPDATE "users" SET "status" = 'active' WHERE "status" IS NULL
                $$;
                """, TestContext.Current.CancellationToken);

            var builder = NSchemaApplication.CreateBuilder();
            builder.UseSqliteSchema(ConnectionString);
            builder.AddDdlSchemas(projectDirectory);
            using var app = builder.Build();

            // Act — plan against the live database and apply what it produced.
            var planResult = await app.Operations.Plan(new PlanArguments { Schemas = ["main"], Target = PlanTarget.Live }, TestContext.Current.CancellationToken);
            planResult.IsSuccess.ShouldBeTrue();
            var applyResult = await app.Operations.Apply(new ApplyArguments { Sql = planResult.Value!.Sql ?? new SqlPlan([]) }, TestContext.Current.CancellationToken);
            applyResult.IsSuccess.ShouldBeTrue();

            // Assert — the column landed and the migration's SQL really ran against the existing row.
            (await Scalar<string>("SELECT \"status\" FROM \"users\" WHERE \"id\" = 1")).ShouldBe("active");
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
