using NSchema.Operations;
using NSchema.Sqlite.Tests.Fixtures;

namespace NSchema.Sqlite.Tests;

/// <summary>
/// Drives a declared <c>SCRIPT … RUN ON</c> change script through the whole pipeline against a real SQLite
/// database: DDL on disk → refresh → plan (the script matches its structural change and is woven into the plan) →
/// apply → the backfill has really run.
/// </summary>
/// <remarks>
/// The canonical change-script shape — a NOT NULL, no-default column add — is not exercisable here: the core
/// decomposes it into add-nullable → backfill → tighten to NOT NULL, and that final <c>AlterColumnNullability</c>
/// needs a table rebuild, which NSchema.Sqlite does not support. A nullable column add with a matched backfill
/// runs the same match-and-weave path end to end without the rebuild.
/// </remarks>
[Collection("sqlite-environment")]
public sealed class SqliteDataMigrationEndToEndTests : SqliteTestBase
{
    [Fact]
    public async Task Apply_WithAddColumnChangeScript_RunsTheBackfillAtItsPlannedPosition()
    {
        // Arrange — a live baseline with data, and a desired schema adding a column plus its backfill script.
        // Everything lives under SQLite's single built-in schema, so the script's target path uses `main`.
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

                SCRIPT backfill_status RUN ON ADD COLUMN main.users.status AS $$
                UPDATE "users" SET "status" = 'active' WHERE "status" IS NULL
                $$;
                """, TestContext.Current.CancellationToken);

            var builder = NSchemaApplication.CreateBuilder();
            builder.UseSqlite(ConnectionString);
            builder.AddProjectSource(projectDirectory);
            builder.UseEphemeralState();
            using var app = builder.Build();

            // Act — refresh so the recorded state reflects the live baseline, plan, and apply what it produced.
            var refreshed = await app.Operations.Refresh(new RefreshArguments(), TestContext.Current.CancellationToken);
            refreshed.IsSuccess.ShouldBeTrue();
            var planResult = await app.Operations.Plan(new PlanArguments(), TestContext.Current.CancellationToken);
            planResult.IsSuccess.ShouldBeTrue(string.Join("; ", planResult.Diagnostics.Select(d => d.Message)));
            var applyResult = await app.Operations.Apply(new ApplyArguments { Plan = planResult.Value!.Plan! }, TestContext.Current.CancellationToken);
            applyResult.IsSuccess.ShouldBeTrue(string.Join("; ", applyResult.Diagnostics.Select(d => d.Message)));

            // Assert — the column landed and the script's SQL really ran against the existing row.
            (await Scalar<string>("SELECT \"status\" FROM \"users\" WHERE \"id\" = 1")).ShouldBe("active");
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
