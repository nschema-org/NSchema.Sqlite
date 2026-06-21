# Changelog

All notable changes to NSchema.Sqlite will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project (mostly) adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Versioning policy

This package uses **lockstep major versioning** with the `NSchema.Core` package: `NSchema.Sqlite X.*.*` requires `NSchema.Core X.*.*`, so version compatibility is always clear.

As a consequence, breaking changes that are specific to this provider (rather than the core API) are signalled by a **minor version bump** rather than a major one, and called out explicitly in this changelog.

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
