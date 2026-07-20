using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSchema.Sqlite.Sql;

namespace NSchema.Sqlite;

/// <summary>
/// Provides extension methods for configuring NSchema to use Sqlite as the underlying database provider.
/// </summary>
public static class NSchemaApplicationBuilderExtensions
{
    extension(NSchemaApplicationBuilder builder)
    {
        /// <summary>
        /// Configures NSchema to use Sqlite as the database provider with the specified connection string.
        /// </summary>
        /// <param name="connectionString">The connection string to the Sqlite database, e.g. <c>Data Source=app.db</c>.</param>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqlite(string connectionString)
        {
            builder.Services.AddSingleton(_ => new SqliteConnectionSource(connectionString));
            builder.Services.AddSingleton<DbDataSource>(p => p.GetRequiredService<SqliteConnectionSource>());
            return builder.UseSqlite();
        }

        /// <summary>
        /// Configures NSchema to use Sqlite as the database provider, building the connection string with a
        /// configuration action for a <see cref="SqliteConnectionStringBuilder"/>.
        /// </summary>
        /// <param name="configure">A delegate that configures the <see cref="SqliteConnectionStringBuilder"/>.</param>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqlite(Action<SqliteConnectionStringBuilder> configure)
        {
            var connectionStringBuilder = new SqliteConnectionStringBuilder();
            configure(connectionStringBuilder);
            return builder.UseSqlite(connectionStringBuilder.ConnectionString);
        }

        /// <summary>
        /// Configures NSchema to use Sqlite as the database provider by registering the database introspector and
        /// SQL dialect. A <see cref="SqliteConnectionSource"/> (and the <see cref="DbDataSource"/> the executor needs)
        /// must already be registered (use one of the overloads that accept a connection string to register them).
        /// </summary>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqlite() => builder
            .UseDatabaseIntrospector<SqliteDatabaseIntrospector>()
            .UseSqliteDialect();

        /// <summary>
        /// Configures the NSchema application to render SQL for Sqlite.
        /// </summary>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqliteDialect() => builder.UseSqlDialect<SqliteSqlDialect>();
    }
}
