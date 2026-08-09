using NSchema.Model.Tables;

namespace NSchema.Sqlite.Sql.Ddl;

internal sealed record ParsedForeignKey(
    string? Name,
    IReadOnlyList<string> Columns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    ReferentialAction OnDelete,
    ReferentialAction OnUpdate);
