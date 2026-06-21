using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace NSchema.Sqlite;

/// <summary>
/// The single connection seam for the Sqlite provider: a <see cref="DbDataSource"/> over a Sqlite connection string.
/// </summary>
internal sealed class SqliteConnectionSource(string connectionString) : DbDataSource
{
    /// <inheritdoc/>
    public override string ConnectionString { get; } = connectionString;

    /// <inheritdoc/>
    protected override DbConnection CreateDbConnection() => new SqliteConnection(ConnectionString);
}
