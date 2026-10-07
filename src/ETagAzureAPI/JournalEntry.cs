namespace ETAG;


/// <summary>
/// Tipo di operazione tracciata da una voce di journal.
/// </summary>
public enum JournalOperationType
{
    Create,
    Modify,
    Delete
}

/// <summary>
/// Una voce di journal: cosa è cambiato, da parte di chi, su quale versione si basava e quale
/// versione ha prodotto.
/// </summary>
public sealed record JournalEntry(
    Guid DeviceId,
    JournalOperationType OperationType,
    string BlobName,
    string? BasedOnETag,
    string? ResultingETag,
    string? ContentHash,
    string PreviousEntryHash,
    DateTimeOffset Timestamp
);
