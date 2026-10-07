using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ETAG;

/// <summary>
/// Persistenza locale del journal: la cronologia di tutte le operazioni (Create/Modify/Delete)
/// che questo dispositivo ha registrato
/// Su disco è un unico file di testo append-only in formato NDJSON
/// nessuna riga già scritta viene mai modificata o rimossa.
/// la normale operatività legge solo l'ultima voce
/// </summary>
public sealed class LocalJournalStore
{
    private const string GenesisHash = "genesis";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string filePath;
    private readonly Dictionary<string, JournalEntry> lastEntryByBlobName = new();
    private string lastEntryHash = GenesisHash;

    public string FilePath => filePath;

    public LocalJournalStore(string journalFilePath)
    {
        filePath = journalFilePath;

        if (File.Exists(filePath))
        {
            foreach (string line in File.ReadAllLines(filePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JournalEntry entry = Deserialize(line);
                lastEntryByBlobName[entry.BlobName] = entry;
                lastEntryHash = ComputeHash(entry);
            }
        }
    }

    /// <summary>
    /// L'ultima voce nota per un nome di blob, o null se non è mai stato tracciato.
    /// </summary>
    public JournalEntry? GetLastEntryForBlob(string blobName)
    {
        lastEntryByBlobName.TryGetValue(blobName, out JournalEntry? entry);
        return entry;
    }

    /// <summary>
    /// Tutti i nomi di blob per cui esiste almeno una voce, inclusi quelli la cui ultima voce è
    /// una <c>Delete</c>.
    /// </summary>
    public IReadOnlyCollection<string> GetAllTrackedBlobNames() => lastEntryByBlobName.Keys.ToArray();

    /// <summary>
    /// Accoda una nuova voce, incatenandola alla precedente tramite hash (tamper-evidence) e
    /// aggiornando l'indice in memoria.
    /// </summary>
    public JournalEntry Append(Guid deviceId, JournalOperationType operationType, string blobName,
        string? basedOnETag, string? resultingETag, string? contentHash)
    {
        var entry = new JournalEntry(deviceId, operationType, blobName, basedOnETag, resultingETag,
            contentHash, lastEntryHash, DateTimeOffset.UtcNow);

        File.AppendAllText(filePath, Serialize(entry) + Environment.NewLine);

        lastEntryByBlobName[blobName] = entry;
        lastEntryHash = ComputeHash(entry);
        return entry;
    }

    /// <summary>
    /// Verifica la catena di hash: ricalcola l'hash di ogni voce e lo confronta con il
    /// <c>PreviousEntryHash</c> della voce successiva. Ritorna l'indice (0-based) della prima
    /// voce la cui posizione non torna, o -1 se la catena è integra.
    /// </summary>
    public int VerifyChain()
    {
        if (!File.Exists(filePath))
        {
            return -1;
        }

        string expectedPreviousHash = GenesisHash;
        string[] lines = File.ReadAllLines(filePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        for (int i = 0; i < lines.Length; i++)
        {
            JournalEntry entry = Deserialize(lines[i]);
            if (entry.PreviousEntryHash != expectedPreviousHash)
            {
                return i;
            }

            expectedPreviousHash = ComputeHash(entry);
        }

        return -1;
    }

    private static string Serialize(JournalEntry entry) => JsonSerializer.Serialize(entry, JsonOptions);

    private static JournalEntry Deserialize(string line) =>
        JsonSerializer.Deserialize<JournalEntry>(line, JsonOptions)
        ?? throw new InvalidDataException($"Voce di journal non deserializzabile: {line}");

    private static string ComputeHash(JournalEntry entry) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(entry))));
}
