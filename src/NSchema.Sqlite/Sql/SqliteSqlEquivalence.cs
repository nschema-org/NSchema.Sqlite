using NSchema.Diff.Plugins;

namespace NSchema.Sqlite.Sql;

/// <summary>
/// SQLite equivalence rules.
/// </summary>
public sealed class SqliteSqlEquivalence : SqlEquivalence
{
    /// <inheritdoc/>
    /// <remarks>
    /// SQLite accepts any type name — a column's type is advisory, resolved to an affinity by substring
    /// matching — so there is no vocabulary to miss and every reference resolves.
    /// </remarks>
    public override bool ValidatesTypeNames => false;
}
