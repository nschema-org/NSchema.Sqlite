# Changelog

All notable changes to NSchema.Sqlite will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project (mostly) adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Versioning policy

This package uses **lockstep major versioning** with the `NSchema.Core` package: `NSchema.Sqlite X.*.*` requires `NSchema.Core X.*.*`, so version compatibility is always clear.

As a consequence, breaking changes that are specific to this provider (rather than the core API) are signalled by a **minor version bump** rather than a major one, and called out explicitly in this changelog.

## [Unreleased]

### Added

- **`SupportsRestrict`** is declared, so `RESTRICT` is rendered rather than dropped.

### Changed

- **The build now writes the full dependency closure beside the assembly** (`CopyLocalLockFileAssemblies`), so a `dotnet build` of this project can be loaded directly by `PLUGIN ( path = '...' )` without being packed first. Package contents are unchanged.

### Fixed

- **`RESTRICT` is no longer parsed as `NO ACTION`.** The DDL parser recognised `CASCADE`, `SET NULL` and `SET DEFAULT` and let everything else fall through, so a foreign key declaring `RESTRICT` came back declaring nothing.
- **Generated columns are no longer all `STORED`.** Sqlite defaults to `VIRTUAL`, and the dialect emitted `STORED` unconditionally, so a virtual column round-tripped as a stored one.

## [5.4.0] - 2026-08-09

### Added

- **XML indexes are reported.** SQLite has no XML type, so nothing to shred and no node table to index.
- **Schema binding and view indexes are reported.** SQLite re-parses a view at each use and stores nothing to index, so a declaration that binds a view to its schema or puts an index on one is now an error diagnostic on the plan rather than something the plan quietly drops.

## [5.3.1] - 2026-08-03

### Fixed

- **`int` columns render as `INTEGER`.** Only that exact spelling makes a SQLite primary key the rowid alias, and an introspected `INTEGER` parses to the model's `int` — so the previous `int` rendering quietly changed a table's shape on a round trip.

## [5.3.0] - 2026-08-03

### Changed

- **A create is a create.** `CREATE VIEW` no longer drops an existing view first: the unconditional drop-and-create renders only for the new replace action, where the plan knows the view exists. A create colliding with a view the plan didn't know about now fails loudly instead of silently overwriting it.

## [5.2.0] - 2026-08-03

### Added

- **Type references are never checked for existence.** SQLite accepts any type name — a column's type is advisory — so the provider now declares this (`SqliteSqlEquivalence.ValidatesTypeNames`), and a plan resolves every type reference rather than hedging about names it cannot verify.

## [5.1.0] - 2026-08-02

### Changed

- **`main` is reported as a schema SQLite provides.** It is a container rather than something a migration creates, and declaring it is an error.

## [5.0.0] - 2026-08-01

Tracks the `NSchema.Core` 5.0.0 rearchitecture (lockstep major versioning). See the core changelog for the engine-wide changes; the provider-facing ones are below.

### Added

- **`new` asks which file to use.** The plugin declares the database file as a scaffolding question and composes the answer into the `connection_string` it writes.

### Changed

- **`UseSqlite(...)` replaces `UseSqliteSchema(...)`,** and `UseSqliteDialect()` replaces `UseSqliteGenerator()`.
- **Introspection implements `IDatabaseIntrospector`** (the core seam replacing `ISchemaProvider`) and returns the new `NSchema.Model` schema model.
- **SQL generation is a `SqlDialect`** (the core seam replacing `ISqlGenerator`), registered via `UseSqliteDialect()`.
- **Unsupported operations are error diagnostics now.** Actions SQLite cannot express (in-place column changes, constraint changes on an existing table, sequences, enums, routines, grants, materialized views, …) surface as errors on the plan result instead of throwing `NotSupportedException`, so you always get the complete plan.
- **Comment changes are skipped with a warning.** SQLite has no `COMMENT ON`; a declared comment previously emitted no SQL silently, and now carries a warning diagnostic explaining why it never converges.
- **The plugin is configured by a `DATABASE` statement** (replacing the `PROVIDER` block), and its scaffold template no longer pins a version — the host owns the `PLUGIN` declaration.

## [4.3.0] - 2026-07-09

### Added

- **Data migrations.** Supports the `MIGRATION … FOR` data migrations introduced in `NSchema.Core` 4.3.

## [4.0.0] - 2026-07-01

### Added

- Added plugin manifest to allow for automatic registration of the provider coming in `NSchema 4.0.0.

## [3.2.1] - 2026-06-24

### Fixed

- The provider will now no-longer attempt to `CREATE`/`DROP` the `main` schema.

## [3.2.0] - 2026-06-21

### Added

- **Trigger Support.** SQLite triggers run an inline `BEGIN … END` body, which the provider now supports, using the inline trigger `Body` added in `NSchema.Core` 3.2.0. A trigger is generated as `CREATE TRIGGER … {BEFORE|AFTER} {event} ON t [FOR EACH ROW] [WHEN (…)] BEGIN … END` and recovered from `sqlite_master`.

### Changed

- Bumped the `NSchema.Core` dependency to `3.2.0`.

## [3.1.0] - 2026-06-21

## Changed

- Updated `Microsoft.Data.Sqlite` from `10.0.3` to `10.0.9`.
- **Breaking:** The casing has been changed from `SQLite` to `Sqlite` to line up with the official Microsoft packages. This applies across all namespaces, but the NuGet package will probably be stuck with the old casing.

## [3.0.1] - 2026-06-21

### Fixed

- `apply` now works against Sqlite. `UseSqliteSchema(...)` registers a `DbDataSource` (alongside the existing introspection source) so the core's SQL executor can open connections to run the migration.

### Security

- Pin the bundled native Sqlite library (`SqlitePCLRaw.lib.e_sqlite3`) forward to the patched 3.50.3, resolving advisory GHSA-2m69-gcr7-jv3q. This replaces the prior `NuGetAudit` suppression with an actual fix.

## [3.0.0] - 2026-06-21

First release of the Sqlite provider for NSchema, tracking NSchema 3.0.0.

### Added

- `NSchemaApplicationBuilder.UseSqliteSchema(...)` extensions for registering the provider — overloads for a connection string, a `SqliteConnectionStringBuilder` configuration delegate, and a no-arg form for a connection registered elsewhere — plus `UseSqliteGenerator()` for registering only the SQL generator.
- `SqliteSchemaProvider` — `ISchemaProvider` implementation that reads the live database from `sqlite_master` and `PRAGMA`s, recovering named constraints (primary keys, foreign keys, unique and check constraints) by parsing the stored `CREATE` SQL. Everything is reported under the `main` schema.
- `SqliteSqlGenerator` — `ISqlGenerator` implementation that translates an NSchema `MigrationPlan` into Sqlite DDL, supporting the features Sqlite has and raising a clear `NotSupportedException` for those it does not.
- SourceLink and symbol packages (`.snupkg`) published alongside the main package for source-level debugging.

[3.1.0]: https://github.com/nschema-org/NSchema.Sqlite/compare/v3.0.1...v3.1.0
[3.0.1]: https://github.com/nschema-org/NSchema.Sqlite/compare/v3.0.0...v3.0.1
[3.0.0]: https://github.com/nschema-org/NSchema.Sqlite/releases/tag/v3.0.0
