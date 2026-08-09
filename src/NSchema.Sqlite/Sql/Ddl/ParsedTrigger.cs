using NSchema.Model.Triggers;

namespace NSchema.Sqlite.Sql.Ddl;

internal sealed record ParsedTrigger(
    TriggerTiming Timing,
    TriggerEvent Events,
    IReadOnlyList<string> UpdateOfColumns,
    bool ForEachRow,
    string? When,
    string Body);
