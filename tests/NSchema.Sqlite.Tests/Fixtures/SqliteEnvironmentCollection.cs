namespace NSchema.Sqlite.Tests.Fixtures;

/// <summary>
/// Serializes the test classes that mutate the process-global <c>NSCHEMA_DATABASE_CONNECTION_STRING</c>
/// environment variable. xUnit runs distinct collections in parallel, so without grouping these classes the
/// end-to-end test's set/restore of the variable races the plugin unit tests' read of it — intermittently
/// failing <c>SqlitePluginEndToEndTests.Apply_ThroughThePlugin_CreatesTheDesiredSchema</c>. No
/// shared fixture is needed; the collection exists purely to opt these classes out of parallelism.
/// </summary>
[CollectionDefinition("sqlite-environment")]
public sealed class SqliteEnvironmentCollection;
