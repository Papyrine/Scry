namespace Scry;

/// <summary>What of the disclosure record SQL Server keeps in ledger tables.</summary>
public enum ScryDisclosureLedgerScope
{
    /// <summary>
    /// The chain of hashes, and the log of what became of each segment. The record itself is in plain
    /// tables, which lets the oldest of it be retired; a change to it shows as a link of the chain
    /// that no longer holds.
    /// </summary>
    Hashes,

    /// <summary>
    /// Every table the record is only ever added to, as well. The database itself refuses a change to
    /// any of them, and nothing of them is ever retired.
    /// </summary>
    Record
}
