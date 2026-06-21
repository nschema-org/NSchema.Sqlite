using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace NSchema.SQLite;

/// <summary>
/// The single connection seam for the SQLite provider: a <see cref="DbDataSource"/> over a SQLite connection string.
/// </summary>
internal sealed class SqliteConnectionSource(string connectionString) : DbDataSource
{
    /// <inheritdoc/>
    public override string ConnectionString { get; } = connectionString;

    /// <inheritdoc/>
    protected override DbConnection CreateDbConnection() => new SqliteConnection(ConnectionString);
}
