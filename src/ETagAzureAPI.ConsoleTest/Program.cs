using Microsoft.Extensions.Logging;
using ETAG;

// Console di test manuale. Di default usa Azurite (emulatore locale); se sono impostate
// AZURE_STORAGE_ACCOUNT_NAME e AZURE_STORAGE_ACCOUNT_KEY usa invece un vero Storage Account
// su Azure (vedi azure.env.example per come impostarle senza committarle).
//
// Comandi:
//   init                                          crea il container di test se non esiste
//   validity                                      esegue ValidityCheck()
//   state <blobName>                               ETag corrente di un blob remoto, o "non esiste"
//   upload-tracked <localFile> --new|--based-on <etag>   scrittura condizionata + voce di journal
//   delete-tracked <blobName> <etag>               cancellazione condizionata + voce di journal
//   sync-local                                     SyncLocalDirectoryToBlob() (locale -> cloud)
//   sync-remote                                    SyncBlobsToLocalDirectory() (cloud -> locale)
//   journal-push                                   pubblica il proprio journal 
//   journal-pull                                   scarica i journal degli altri dispositivi
//   journal-verify                                 verifica la catena di hash del journal locale
//   sync                                           sync-local + journal-push + sync-remote
//
// Variabile d'ambiente PROTOTIPO_PROFILE (default "default"): separa i dati locali per simulare
// piu' dispositivi sulla stessa macchina fisica.
//
// Variabili d'ambiente per un vero Storage Account (entrambe richieste, altrimenti si usa Azurite):
//   AZURE_STORAGE_ACCOUNT_NAME   nome dello Storage Account
//   AZURE_STORAGE_ACCOUNT_KEY    una delle due access key dello Storage Account
//   AZURE_STORAGE_CONTAINER      (opzionale) nome del container, default "sync-test"
// Si possono esportare come variabili d'ambiente di sistema oppure scrivere in .local/azure.env
// (righe "CHIAVE=valore"): quel file e' dentro .local/, gia' ignorato da git (.gitignore),
// quindi le credenziali non vengono mai committate.

LoadLocalEnvFile(Path.Combine(".local", "azure.env"));

string profile = Environment.GetEnvironmentVariable("PROTOTIPO_PROFILE") is { Length: > 0 } p ? p : "default";
string? azureAccountName = Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT_NAME");
string? azureAccountKey = Environment.GetEnvironmentVariable("AZURE_STORAGE_ACCOUNT_KEY");
bool useRealAzure = azureAccountName is { Length: > 0 } && azureAccountKey is { Length: > 0 };

string ContainerName = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONTAINER") is { Length: > 0 } c ? c : "sync-test";
string SyncFolder = $".local/{profile}/sync";
string JournalFilePath = $".local/{profile}/journal.ndjson";
string DeviceIdFilePath = $".local/{profile}/device-id.txt";

using var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
ILogger logger = loggerFactory.CreateLogger($"Prototipo[{profile}]");

string backendDescription = useRealAzure ? $"Azure reale (account '{azureAccountName}')" : "Azurite (emulatore locale)";

if (args.Length == 0)
{
    Console.WriteLine("Comandi: init | validity | state <blob> | " +
                       "upload-tracked <file> (--new|--based-on <etag>) | delete-tracked <blob> <etag> | " +
                       "sync-local | sync-remote | journal-push | journal-pull | journal-verify | sync " +
                       $"(profilo attivo: '{profile}', variabile PROTOTIPO_PROFILE; " +
                       $"backend: {backendDescription}, container '{ContainerName}')");
    return 1;
}

logger.LogInformation($"Backend: {backendDescription}, container '{ContainerName}'.");

ETagAzureAPI api = azureAccountName is { Length: > 0 } accountName && azureAccountKey is { Length: > 0 } accountKey
    ? new ETagAzureAPI(accountName, accountKey, ContainerName, SyncFolder, JournalFilePath, DeviceIdFilePath, logger)
    : new ETagAzureAPI(ContainerName, SyncFolder, JournalFilePath, DeviceIdFilePath, logger);


void LogUploadStarted(string name) => Console.WriteLine($"  carico: {name}");
void LogFileUploaded(string name) => Console.WriteLine($"  caricato: {name}");
void LogRemoteDeleted(string name) => Console.WriteLine($"  cancellato lato server: {name}");
void LogDownloadStarted(string name) => Console.WriteLine($"  scarico: {name}");
void LogFileDownloaded(string path) => Console.WriteLine($"  scaricato: {path}");
void LogLocalDeleted(string name) => Console.WriteLine($"  cancellato in locale: {name}");

try
{
    switch (args[0])
    {
        case "init":
            await api.EnsureContainerExistsAsync();
            Console.WriteLine($"Container '{ContainerName}' pronto.");
            return 0;

        case "validity":
        {
            bool valid = await api.ValidityCheck();
            Console.WriteLine(valid ? "Valido." : "Non valido.");
            return valid ? 0 : 2;
        }

        case "state":
        {
            string? etag = await api.GetRemoteETagAsync(args[1]);
            Console.WriteLine(etag == null ? $"'{args[1]}' non esiste." : $"'{args[1]}': ETag={etag}");
            return 0;
        }

        case "upload-tracked":
        {
            string localFile = args[1];
            string mode = args[2];
            string? basedOn = mode switch
            {
                "--new" => null,
                "--based-on" => args[3],
                _ => throw new ArgumentException($"Modalita' non riconosciuta: {mode} (usa --new oppure --based-on <etag>)")
            };

            try
            {
                string newETag = await api.uploadTracked(localFile, basedOn);
                Console.WriteLine($"OK. Nuovo ETag: {newETag}");
                return 0;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409)
            {
                string reason = basedOn == null
                    ? "Il blob esiste gia' (creazione 'solo se nuovo' rifiutata)."
                    : "La versione attesa non e' piu' quella corrente.";
                Console.WriteLine($"RIFIUTATO ({ex.Status}). {reason}");
                return 2;
            }
        }

        case "delete-tracked":
        {
            try
            {
                await api.deleteTracked(args[1], args[2]);
                Console.WriteLine("OK. Cancellato.");
                return 0;
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 412 || ex.Status == 404)
            {
                string reason = ex.Status == 404
                    ? "Il blob non esiste (gia' cancellato, o mai esistito)."
                    : "La versione attesa non e' piu' quella corrente.";
                Console.WriteLine($"RIFIUTATO ({ex.Status}). {reason}");
                return 2;
            }
        }

        case "sync-local":
            await api.SyncLocalDirectoryToBlob(
                onUploadStarted: LogUploadStarted,
                onFileUploaded: LogFileUploaded,
                onFileDeleted: LogRemoteDeleted);
            Console.WriteLine("sync-local completato.");
            return 0;

        case "sync-remote":
            await api.SyncBlobsToLocalDirectory(
                onDownloadStarted: LogDownloadStarted,
                onFileDownloaded: LogFileDownloaded,
                onFileDeleted: LogLocalDeleted);
            Console.WriteLine("sync-remote completato.");
            return 0;

        case "journal-push":
            await api.PushJournalAsync();
            Console.WriteLine("Journal pubblicato.");
            return 0;

        case "journal-pull":
        {
            List<Guid> pulled = await api.PullJournalsAsync();
            Console.WriteLine(pulled.Count == 0
                ? "Nessun segmento di altri dispositivi trovato."
                : $"Scaricati {pulled.Count} segmenti: {string.Join(", ", pulled)}");
            return 0;
        }

        case "journal-verify":
        {
            int brokenAt = api.VerifyJournal();
            Console.WriteLine(brokenAt == -1 ? "Catena integra." : $"MANOMISSIONE RILEVATA alla voce {brokenAt}.");
            return brokenAt == -1 ? 0 : 2;
        }
        /// catena completa di utilizzo reale
        case "sync":
            Console.WriteLine("--- sync-local ---");
            await api.SyncLocalDirectoryToBlob(
                onUploadStarted: LogUploadStarted,
                onFileUploaded: LogFileUploaded,
                onFileDeleted: LogRemoteDeleted);
            Console.WriteLine("--- journal-push ---");
            await api.PushJournalAsync();
            Console.WriteLine("--- sync-remote ---");
            await api.SyncBlobsToLocalDirectory(
                onDownloadStarted: LogDownloadStarted,
                onFileDownloaded: LogFileDownloaded,
                onFileDeleted: LogLocalDeleted);
            Console.WriteLine("sync completato.");
            return 0;

        default:
            Console.WriteLine($"Comando sconosciuto: {args[0]}");
            return 1;
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Errore: {ex.Message}");
    return 1;
}

// Carica coppie CHIAVE=valore da un file locale (se esiste) nelle variabili d'ambiente del
// processo, senza sovrascrivere quelle gia' impostate a livello di sistema/shell: queste ultime
// hanno sempre la precedenza. Righe vuote o che iniziano con '#' sono ignorate.
static void LoadLocalEnvFile(string path)
{
    if (!File.Exists(path)) return;

    foreach (string line in File.ReadAllLines(path))
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

        int separatorIndex = trimmed.IndexOf('=');
        if (separatorIndex <= 0) continue;

        string key = trimmed[..separatorIndex].Trim();
        string value = trimmed[(separatorIndex + 1)..].Trim();

        if (Environment.GetEnvironmentVariable(key) == null)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
