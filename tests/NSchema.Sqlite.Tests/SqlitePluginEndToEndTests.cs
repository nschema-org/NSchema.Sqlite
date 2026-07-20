using NSchema.Operations;
using NSchema.Plugins;
using NSchema.Sqlite.Tests.Fixtures;

namespace NSchema.Sqlite.Tests;

/// <summary>
/// End-to-end proof that the <see cref="SqlitePlugin"/> manifest wires a fully working provider: it runs a real
/// migration THROUGH the plugin's <c>Configure</c> (not the direct <c>UseSqlite</c> API) against a real (temp
/// file) SQLite database, then re-introspects to confirm the schema was applied. In-process — no Docker.
/// </summary>
[Collection("sqlite-environment")]
public sealed class SqlitePluginEndToEndTests : SqliteTestBase
{
    /// <summary>
    /// The plugin lets <c>NSCHEMA_SQLITE_CONNECTION_STRING</c> override the configured connection string (by design).
    /// If that variable is set in the host environment (Rider run-config, CI, a stray <c>launchctl setenv</c>), the
    /// apply would target a different database than <see cref="SqliteTestBase.Introspect"/> reads, leaving the
    /// assertion's live schema empty. Pinning it to the test's own connection string makes the test hermetic.
    /// </summary>
    private const string EnvConnectionString = "NSCHEMA_SQLITE_CONNECTION_STRING";

    [Fact]
    public async Task Apply_ThroughThePlugin_CreatesTheDesiredSchema()
    {
        // Arrange — a desired schema on disk (under SQLite's built-in `main` schema), configured ONLY via the plugin.
        var projectDirectory = Directory.CreateTempSubdirectory("nschema-sqlite-e2e-").FullName;
        var priorEnvConnectionString = Environment.GetEnvironmentVariable(EnvConnectionString);
        Environment.SetEnvironmentVariable(EnvConnectionString, ConnectionString);
        try
        {
            // The second table carries a foreign key and a unique constraint: the linearizer emits those as
            // separate Add* actions after the CREATE TABLE, and the dialect must fold them into the inline form.
            await File.WriteAllTextAsync(Path.Combine(projectDirectory, "schema.sql"), """
                CREATE TABLE main.widgets (
                  id   bigint NOT NULL,
                  name text,
                  CONSTRAINT widgets_pkey PRIMARY KEY (id)
                );

                CREATE TABLE main.orders (
                  id        bigint NOT NULL,
                  widget_id bigint NOT NULL,
                  code      varchar(20) NOT NULL,
                  CONSTRAINT orders_pkey PRIMARY KEY (id),
                  CONSTRAINT orders_code_uq UNIQUE (code),
                  CONSTRAINT orders_widget_fk FOREIGN KEY (widget_id) REFERENCES main.widgets (id)
                );
                """, TestContext.Current.CancellationToken);

            var builder = NSchemaApplication.CreateBuilder();
            var configured = new SqlitePlugin().Configure(builder, new PluginConfig(new PluginLabel("sqlite"),
                new Dictionary<AttributeKey, ConfigValue>
                {
                    [new AttributeKey("connection_string")] = ConfigValue.OfString(ConnectionString),
                }));
            configured.IsSuccess.ShouldBeTrue();

            builder.AddProjectSource(projectDirectory);
            builder.UseEphemeralState();
            using var app = builder.Build();

            // Act — the CLI-style flow through the plugin-wired provider: refresh so state reflects the live
            // database, plan, then apply.
            var refreshed = await app.Operations.Refresh(new RefreshArguments(), TestContext.Current.CancellationToken);
            refreshed.IsSuccess.ShouldBeTrue();
            var planResult = await app.Operations.Plan(new PlanArguments(), TestContext.Current.CancellationToken);
            planResult.IsSuccess.ShouldBeTrue(string.Join("; ", planResult.Diagnostics.Select(d => d.Message)));
            var applyResult = await app.Operations.Apply(new ApplyArguments { Plan = planResult.Value!.Plan! }, TestContext.Current.CancellationToken);
            applyResult.IsSuccess.ShouldBeTrue(string.Join("; ", applyResult.Diagnostics.Select(d => d.Message)));

            // Assert — both tables really exist, read back via a fresh introspection, with the folded
            // constraints carrying the author's names.
            var live = await Introspect();
            var tables = live.Schemas.ShouldHaveSingleItem().Tables;
            tables.Select(t => t.Name.Value).ShouldBe(["orders", "widgets"]);
            var orders = tables.Single(t => t.Name.Value == "orders");
            orders.ForeignKeys.ShouldHaveSingleItem().Name.Value.ShouldBe("orders_widget_fk");
            orders.UniqueConstraints.ShouldHaveSingleItem().Name.Value.ShouldBe("orders_code_uq");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvConnectionString, priorEnvConnectionString);
            Directory.Delete(projectDirectory, recursive: true);
        }
    }
}
