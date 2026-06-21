using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSchema.SQLite.Sql;

namespace NSchema.SQLite;

/// <summary>
/// Provides extension methods for configuring NSchema to use SQLite as the underlying database provider.
/// </summary>
public static class NSchemaApplicationBuilderExtensions
{
    extension(NSchemaApplicationBuilder builder)
    {
        /// <summary>
        /// Configures NSchema to use SQLite as the database provider with the specified connection string.
        /// </summary>
        /// <param name="connectionString">The connection string to the SQLite database, e.g. <c>Data Source=app.db</c>.</param>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqliteSchema(string connectionString)
        {
            builder.Services.AddSingleton(_ => new SqliteConnectionSource(connectionString));
            builder.Services.AddSingleton<DbDataSource>(p => p.GetRequiredService<SqliteConnectionSource>());
            return builder.UseSqliteSchema();
        }

        /// <summary>
        /// Configures NSchema to use SQLite as the database provider, building the connection string with a
        /// configuration action for a <see cref="SqliteConnectionStringBuilder"/>.
        /// </summary>
        /// <param name="configure">A delegate that configures the <see cref="SqliteConnectionStringBuilder"/>.</param>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqliteSchema(Action<SqliteConnectionStringBuilder> configure)
        {
            var connectionStringBuilder = new SqliteConnectionStringBuilder();
            configure(connectionStringBuilder);
            return builder.UseSqliteSchema(connectionStringBuilder.ConnectionString);
        }

        /// <summary>
        /// Configures NSchema to use SQLite as the database provider by registering the schema provider and SQL
        /// generator. A <see cref="SqliteConnectionSource"/> (and the <see cref="DbDataSource"/> the executor needs)
        /// must already be registered (use one of the overloads that accept a connection string to register them).
        /// </summary>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqliteSchema() => builder
            .UseCurrentSchema<SqliteSchemaProvider>()
            .UseSqliteGenerator();

        /// <summary>
        /// Configures the NSchema application to generate SQL for SQLite.
        /// </summary>
        /// <returns>The <see cref="NSchemaApplicationBuilder"/> instance, allowing for method chaining.</returns>
        public NSchemaApplicationBuilder UseSqliteGenerator() => builder.UseSqlGenerator<SqliteSqlGenerator>();
    }
}
