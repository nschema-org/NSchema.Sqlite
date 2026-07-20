# ![NSchema](https://raw.githubusercontent.com/nschema-org/NSchema.Docs/main/assets/nschema-logo-horizontal.png)

[![NSchema.Sqlite](https://github.com/nschema-org/NSchema.Sqlite/actions/workflows/cicd.yml/badge.svg)](https://github.com/nschema-org/NSchema.Sqlite/actions/workflows/cicd.yml)

# NSchema.Sqlite

Sqlite provider for [NSchema](https://github.com/nschema-org/NSchema), the declarative database schema migration tool for .NET. It plugs Sqlite introspection and DDL generation into NSchema via [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/).

Most users should use the [NSchema CLI](https://github.com/nschema-org/NSchema), which already includes this provider. Add this package directly only when [embedding the engine](https://nschema.dev/library/embedding/) in your own application.

## Installation

```sh
dotnet add package NSchema.Core
dotnet add package NSchema.Sqlite
```

## Scope

Sqlite has a deliberately small surface, so this provider models what Sqlite actually supports:

- **Supported:** tables, columns (with `DEFAULT` and stored generated columns), primary keys, foreign keys, unique constraints, check constraints, indexes, views, and triggers.
- **The schema is always `main`.** Sqlite's primary database is `main`; declare objects as `main.<name>` in your DDL. (`temp` and `ATTACH`ed databases are out of scope.)
- **Native `ALTER TABLE` only.** Create/drop/rename tables and columns, and create/drop indexes/views, are applied directly. Operations Sqlite cannot do in place — changing a column's type, nullability or default, or adding/dropping a constraint on an existing table — require a full table rebuild and are currently reported as clear plan errors rather than silently rebuilding.
- **Triggers** carry an inline body, written as `CREATE TRIGGER … ON main.t AS $$ BEGIN … END $$`, and fire `BEFORE` or `AFTER` a single statement event.
- **Not supported (Sqlite has no equivalent):** schemas other than `main`, sequences, enums, domains, composite types, stored functions/procedures, `GRANT`s, and materialized views. These are reported as plan errors.
- **Comments are not persisted.** Sqlite has no `COMMENT ON`, so documentation comments are skipped with a warning when rendering SQL.

Column types are emitted using NSchema's canonical type names (e.g. `bigint`, `varchar(255)`, `decimal(18,2)`); Sqlite applies its normal type affinity and preserves the declared name, so a schema round-trips without phantom drift.

## Documentation

Full documentation lives at **[nschema.dev](https://nschema.dev)**:

- [Embedding the engine](https://nschema.dev/library/embedding/)

## License

See [LICENSE](LICENSE).
