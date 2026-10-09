extern alias identity;

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure;
using System.Runtime.InteropServices;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using Azure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


namespace ETAG
{
    public class ETagAzureAPI
    {
        /// <summary>
        /// Initializes a new instance of <see cref="ETagAzureAPI"/> configured for Azure Blob Storage.
        /// </summary>
        /// <param name="_accountName">The Azure Storage account name.</param>
        /// <param name="_blobStorageConnectionString">The connection string or storage account key.</param>
        /// <param name="_blobStorageContainerName">The name of the target blob container.</param>
        /// <param name="_blobSyncFolder">The local directory path to synchronize.</param>
        /// <param name="journalFilePath">Path to the local NDJSON journal store file.</param>
        /// <param name="deviceIdFilePath">Path to the file storing this device's persistent unique identifier (GUID).</param>
        /// <param name="logger">Optional logger for operational diagnostics; defaults to <see cref="NullLogger"/> if null.</param>
        /// <exception cref="ArgumentException">Thrown when the connection string cannot be resolved or is invalid.</exception>
        public ETagAzureAPI(string _accountName, string _blobStorageConnectionString, string _blobStorageContainerName,
            string _blobSyncFolder, string journalFilePath, string deviceIdFilePath, ILogger? logger = null)
        {
            this.logger = logger ?? NullLogger.Instance;

            string connectionString = getConnectionString(_accountName, _blobStorageConnectionString);

            // if connection string valid
            if (!IsConnectionStringValid(connectionString))
            {
                Console.WriteLine("Error, no valid connection string");
                throw new ArgumentException("Error, no valid connection string to azure storage account.");
            }
            blobStorageConnectionString = connectionString;

            blobStorageContainerName = _blobStorageContainerName;
            blobContainerclient = new BlobContainerClient(blobStorageConnectionString, blobStorageContainerName);
            //TODO: this can throw exceptions that must be handled at the higher level


            blobSyncFolder = _blobSyncFolder;
            Directory.CreateDirectory(blobSyncFolder);

            journal = new LocalJournalStore(journalFilePath);
            deviceId = LoadOrCreateDeviceId(deviceIdFilePath);
        }

        /// <summary>
        /// Initializes a new instance of <see cref="ETagAzureAPI"/> configured for the local Azurite storage emulator.
        /// </summary>
        /// <param name="containerName">The name of the target container in the local emulator.</param>
        /// <param name="_blobSyncFolder">The local directory path to synchronize.</param>
        /// <param name="journalFilePath">Path to the local NDJSON journal store file.</param>
        /// <param name="deviceIdFilePath">Path to the persistent device ID file.</param>
        /// <param name="logger">Optional logger instance.</param>
        public ETagAzureAPI(string containerName, string _blobSyncFolder, string journalFilePath,
            string deviceIdFilePath, ILogger? logger = null)
        {
            this.logger = logger ?? NullLogger.Instance;

            blobStorageConnectionString = "UseDevelopmentStorage=true";
            blobStorageContainerName = containerName;
            blobContainerclient = new BlobContainerClient(blobStorageConnectionString, blobStorageContainerName);

            blobSyncFolder = _blobSyncFolder;
            Directory.CreateDirectory(blobSyncFolder);

            journal = new LocalJournalStore(journalFilePath);
            deviceId = LoadOrCreateDeviceId(deviceIdFilePath);
        }


        #region public_methods
        public async Task<bool> ValidityCheck()
        {
            try
            {
                
                // if blob exists
                if (blobContainerclient == null)
                {
                    logger.LogError("Blob container client is null");
                    return false;
                }

                bool success = await blobContainerclient.ExistsAsync();
                if (!success)
                {
                    logger.LogError("Blob container does not exists");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // print error ---- better to handle the flow control instead of relying on exceptions
                logger.LogError("Azure blob exception: " + ex.Message);
                return false;
            }
            return true;
        }

        /*
         var provisioner = new AzureStorageProvisioner();

         await provisioner.CreateStorageAccountAndContainerAsync(
            "<your-subscription-id>",
            "myResourceGroup",
            "westeurope",
            "mystorageacct123",
            "mycontainer"
        );
        */

        public async Task<bool> CreateStorageAccountAndContainerAsync(
             string subscriptionId,
             string resourceGroupName,
             string location,
             string storageAccountName,
             string containerName,
             string tenantId,
             string clientId,
             string clientSecret)
        {
            try
            {

                // Create the storage account

                string blobName = containerName;// "testfile.txt";
                string blobContent = "Hello from Azure SDK for .NET!";

                // Authenticate
                //var credential = new DefaultAzureCredential();
                var credential = new identity::Azure.Identity.ClientSecretCredential(tenantId, clientId, clientSecret);
                var armClient = new ArmClient(credential);

                // Get subscription
                var subscription = armClient.GetSubscriptionResource(
                    SubscriptionResource.CreateResourceIdentifier(subscriptionId));

                // for error on subscription run
                // az role assignment create --assignee 76e8f74d-6ab9-4e93-af3b-df7e71db2ac0  --role "Contributor"  --scope /subscriptions/subscriptionId

                // Create or get resource group
                var rgCollection = subscription.GetResourceGroups();
                var rgResponse = await rgCollection.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    resourceGroupName,
                    new ResourceGroupData(location));
                var resourceGroup = rgResponse.Value;

                // Create storage account
                var storageCollection = resourceGroup.GetStorageAccounts();
                var storageData = new StorageAccountCreateOrUpdateContent(
                    new StorageSku(StorageSkuName.StandardLrs),
                    StorageKind.StorageV2,
                    location);
                var storageAccount = (await storageCollection.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    storageAccountName,
                    storageData)).Value;

                // Get storage account keys
                //var keys = await storageAccount.GetKeysAsync();
                string accountKey = "";
                await foreach (var key in storageAccount.GetKeysAsync())
                {
                    accountKey = key.Value;
                    // Use it here or break after the first
                    break;
                }
                //string accountKey = keys.Value[0].Value;

                // Create blob container
                //var containerCollection = storageAccount.GetBlobService().Value.GetBlobContainers();
                var blobService = await storageAccount.GetBlobService().GetAsync(); // if you need to call GetAsync()
                var containerCollection = blobService.Value.GetBlobContainers();

                await containerCollection.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    containerName,
                    new BlobContainerData());

                // Upload blob using Azure.Storage.Blobs
                var blobServiceClient = new BlobServiceClient(
                    new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                    new StorageSharedKeyCredential(storageAccountName, accountKey));

                var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
                var blobClient = containerClient.GetBlobClient(blobName);

                // Create a sample stream to upload
                using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(blobContent));
                await blobClient.UploadAsync(stream, overwrite: true);

                Console.WriteLine(" Blob uploaded successfully.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
                //return false;
            }
        }



        /// <summary>
        /// Crea il container se non esiste già.
        /// </summary>
        public async Task EnsureContainerExistsAsync()
        {
            if (blobContainerclient == null)
            {
                throw new InvalidOperationException("Blob container client is null.");
            }

            await blobContainerclient.CreateIfNotExistsAsync();
        }

        public bool isSyncRunning()
        {
            return syncRunning;
        }

        public void breakSync()
        {
            syncTerminationHandler = true;
        }

        /// <summary>
        /// Downloads a blob from the Azure Blob Storage to a local file asynchronously.
        /// </summary>
        /// <param name="blobFilePath">The path of the blob file to be downloaded.</param>
        /// <returns>L'ETag del blob scaricato serve al chiamante per condizionare un upload successivo sullo stesso percorso</returns>
        /// <exception cref="RequestFailedException">Thrown when the request to Azure Blob Storage fails.</exception>
        /// <remarks>        
        /// This method downloads the specified blob from the Azure Blob Storage to a local file
        /// located at the destination path determined by combining the <paramref name="blobSyncFolder"/>
        /// and the <paramref name="blobFilePath"/>. The method creates the necessary directories in
        /// the local file system if they do not already exist. If the download operation encounters
        /// an error, a <see cref="RequestFailedException"/> is thrown.
        /// </remarks>
        /// <example>
        /// <code>
        /// // Example usage:
        /// string blobFilePath = "path/to/your/blob/file.txt";
        /// await DownloadBlobToFileAsync(blobFilePath);
        /// </code>
        /// </example>        
        public async Task<string?> DownloadBlobToFileAsync(string blobFilePath)
        {
            Console.WriteLine("Downloading ");
            try
            {

                // validity check
                if (blobFilePath == null)
                {
                    logger.LogError("Null string in blob file path");
                    return null;
                }

                BlobClient? blob = blobContainerclient?.GetBlobClient(blobFilePath);
                string destinationPath = Path.Combine(blobSyncFolder, blobFilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? string.Empty);

                if (blob != null)
                {
                    await blob.DownloadToAsync(destinationPath);
                    logger.LogInformation("Downloaded" + destinationPath);
                    BlobProperties props = await blob.GetPropertiesAsync();
                    return props.ETag.ToString();
                }
                else
                {
                    // return some error;
                    logger.LogError("Null reference in blob path");
                    return null;
                }

            }
            catch (RequestFailedException e)
            {
                logger.LogError(e.Message);
                throw;
            }
        }

        /// <summary>
        /// Asynchronously retrieves a list of blobs from the Azure Blob Storage container associated with the current instance of the class.
        /// </summary>
        /// <returns>
        /// A task representing the asynchronous operation. Upon completion, the <see cref="blobStorageContainerList"/> class member is populated with
        /// the retrieved blobs from the Azure Blob Storage container.
        /// </returns>
        /// <exception cref="RequestFailedException">Thrown when the request to Azure Blob Storage fails.</exception>
        /// <remarks>
        /// This method asynchronously calls the listing operation on the Azure Blob Storage container specified by the <see cref="blobContainerclient"/>
        /// class member. It retrieves a collection of blobs from the container and stores them in the <see cref="blobStorageContainerList"/> class member.
        /// If the listing operation encounters an error, a <see cref="RequestFailedException"/> is thrown, and the error message is displayed in the console.
        /// </remarks>
        /// <example>
        /// <code>
        /// // Example usage:
        /// await listMyBlob();
        /// foreach (var blob in blobStorageContainerList)
        /// {
        ///     Console.WriteLine($"Blob Name: {blob.Name}, Size: {blob.Properties.ContentLength}");
        /// }
        /// </code>
        /// </example>
        public async Task listMyBlob()
        {
            try
            {
                // Call the listing operation and return pages of the specified size.
                AsyncPageable<BlobItem>? blob = blobContainerclient?.GetBlobsAsync();
                if (blob != null)
                {
                    blobStorageContainerList = blob.AsPages(default);
                }
                else
                {
                    //TODO: log some error
                }
            }
            catch (RequestFailedException e)
            {
                logger.LogError(e.Message);
                throw;
            }
        }

        public async Task downloadAllFiles()
        {
            // Check if the IAsyncEnumerable is empty
            if (blobStorageContainerList == null)
            {
                logger.LogInformation("The blobStorageContainerList is null.");
                return;
            }

            await foreach (Page<BlobItem> blobPage in blobStorageContainerList)
            {
                foreach (BlobItem blobItem in blobPage.Values)
                {
                    using (var cts = new CancellationTokenSource())
                    {
                        Task downloadTask = DownloadBlobToFileAsync(blobItem.Name);
                        Task timeoutTask = Task.Delay(1000);

                        if (await Task.WhenAny(downloadTask, timeoutTask) == downloadTask)
                        {
                            // Download completed within the timeout
                            Console.WriteLine("Downloaded");
                        }
                        else
                        {
                            // Timeout occurred
                            cts.Cancel(); // Cancel the download task if it's still running
                            Console.WriteLine("Cancelled ");
                        }
                    }
                }

                Console.WriteLine();
            }
        }

        public async Task printBlobContent()
        {

            // Check if the IAsyncEnumerable is empty
            if (blobStorageContainerList == null)
            {
                Console.WriteLine("The blobStorageContainerList is null.");
                return;
            }

            // Enumerate the blobs returned for each page.
            await foreach (Page<BlobItem> blobPage in blobStorageContainerList)
            {
                foreach (BlobItem blobItem in blobPage.Values)
                {
                    Console.WriteLine("Blob name: {0}", blobItem.Name);
                }

                Console.WriteLine();
            }
        }


        /// <summary>
        /// Synchronizes the local directory to the remote Azure Blob container using content hashes and ETags.
        /// </summary>
        /// <param name="myFileList">Optional explicit list of files to process; if null or empty, all files in <see cref="blobSyncFolder"/> are processed.</param>
        /// <param name="deleteUploadedFile">If true, deletes local files after successful upload or verification.</param>
        /// <param name="onUploadStarted">Callback invoked when an upload begins for a blob name.</param>
        /// <param name="onFileUploaded">Callback invoked when a file is successfully uploaded.</param>
        /// <param name="onFileDeleted">Callback invoked when a remote orphan blob is deleted.</param>
        public async Task SyncLocalDirectoryToBlob(
            string[]? myFileList = null,
            bool deleteUploadedFile = false,
            Action<string>? onUploadStarted = null,
            Action<string>? onFileUploaded = null,
            Action<string>? onFileDeleted = null)
        {
            try
            {
                syncRunning = true;
                syncTerminationHandler = false;

                AsyncPageable<BlobItem>? blobPropertyList = blobContainerclient?.GetBlobsAsync();
                List<BlobItem> blobItems = await fromPageableToList(blobPropertyList);

                // If myFileList argument is null or empty, get all files in the blobSyncFolder, otherwise use the provided list of files
                if (myFileList == null || myFileList.Length == 0)
                {
                    myFileList = System.IO.Directory
                        .GetFiles(blobSyncFolder, "*.*", SearchOption.AllDirectories)
                        .ToArray();
                }

                // con la cartella vuota si prosegue: la fase delle cancellazioni deve girare comunque

                foreach (string localFilePath in myFileList)
                {
                    if (syncTerminationHandler)
                    {
                        syncRunning = false;
                        return;
                    }

                    string blobFilename = getFullBlobName(localFilePath, blobSyncFolder);
                    BlobItem? blobItem = FindBlobByName(blobItems, blobFilename);
                    BlobClient? blobItemClient = blobContainerclient?.GetBlobClient(blobFilename);

                    if (blobItemClient == null)
                        continue;

                    bool fileExists = System.IO.File.Exists(localFilePath);

                    byte[] localContent = fileExists ? System.IO.File.ReadAllBytes(localFilePath) : Array.Empty<byte>();
                    string localHash = ComputeSha256Hex(localContent);

                    JournalEntry? lastEntry = journal.GetLastEntryForBlob(blobFilename);
                    bool unchanged = lastEntry != null
                        && lastEntry.OperationType != JournalOperationType.Delete
                        && lastEntry.ContentHash == localHash;

                    if (blobItem != null)
                    {
                        if (lastEntry == null)
                        {
                            // Il blob esiste già sul server ma questo dispositivo non ha alcuna
                            // voce di journal per questo percorso
                            Console.WriteLine($"'{blobFilename}': SKIP, blob existing server side without local journal for this path; do sync-remote and reconcile manually before reloading.");
                        }
                        else if (!unchanged)
                        {
                            // Se l'ultima voce è una Delete non c'è un ETag atteso: la scrittura è una creazione
                            // (If-None-Match: *) e il server la rifiuta se il blob è stato ricreato altrove.
                            string? basedOnETag = lastEntry.ResultingETag;
                            try
                            {
                                onUploadStarted?.Invoke(blobFilename);

                                var conditions = basedOnETag == null
                                    ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                                    : new BlobRequestConditions { IfMatch = new ETag(basedOnETag) };
                                BlobContentInfo blobContentInfo;
                                using (var uploadStream = System.IO.File.OpenRead(localFilePath))
                                {
                                    blobContentInfo = await blobItemClient.UploadAsync(
                                        uploadStream, new BlobUploadOptions { Conditions = conditions });
                                }

                                journal.Append(deviceId,
                                    basedOnETag == null ? JournalOperationType.Create : JournalOperationType.Modify,
                                    blobFilename, basedOnETag, blobContentInfo.ETag.ToString(), localHash);

                                onFileUploaded?.Invoke(blobFilename);
                                if (deleteUploadedFile) System.IO.File.Delete(localFilePath);
                            }
                            catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409)
                            {
                                Console.WriteLine($"'{blobFilename}': REFUSED, blob last version not matching.");
                            }
                        }
                        else
                        {
                            // do not load any unmodified file
                            // cancella il file se visto e sincronizzato
                            if (deleteUploadedFile) System.IO.File.Delete(localFilePath);
                        }
                    }
                    else if (unchanged)
                    {
                        // Il blob non esiste più sul server e il filo locale è rimasto invariato
                        // dell'ultima sincronizzazione riuscita dunque non ho nessuna
                        // modifica pending. L'assenza sul server implica  una cancellazione vera, fatta da
                        // un altro device , quindi cancello il file. 
                        // Senza questo check il file viene ricaricato come se fosse un nuovo file
                        Console.WriteLine($"'{blobFilename}': il blob non esiste più sul server e il contenuto locale è invariato dall'ultima sincronizzazione, cancello la copia locale invece di ricaricarla.");
                        System.IO.File.Delete(localFilePath);
                        journal.Append(deviceId, JournalOperationType.Delete, blobFilename, lastEntry!.ResultingETag, null, localHash);
                    }
                    else if (lastEntry != null && lastEntry.OperationType == JournalOperationType.Delete
                        && lastEntry.ContentHash == localHash)
                    {
                        // Questo device ha già cancellato questo blob precedentemente, se rileva lo stesso identico file
                        // non lo ricarica ma può ricrearlo esplicitamente con upload-tracked --new 

                        Console.WriteLine($"'{blobFilename}': SKIP, questo device l'ha già cancellato in precedenza con lo stesso contenuto; usa upload-tracked --new per ricrearlo di proposito.");
                    }
                    else
                    {
                        try
                        {
                            if (lastEntry != null && lastEntry.OperationType == JournalOperationType.Delete)
                            {
                                //Se l'hash non combacia è un nuovo file quindi viene caricato
                                
                                Console.WriteLine($"'{blobFilename}': contenuto diverso da quello cancellato in precedenza (hash non combacia); carico come nuovo.");
                            }
                            else if (lastEntry != null)
                            {
                                // lastEntry.OperationType è Create o Modify (non Delete) ma il blob non esiste più (cancellato altrove, non da
                                // questo device) e il contenuto locale contiente modifiche rispetto al precedente sync
                                // carico il file
                                Console.WriteLine($"'{blobFilename}': pubblico una versione più recente di (ETag {lastEntry.ResultingETag}), nel frattempo cancellata altrove.");
                            }
                            onUploadStarted?.Invoke(blobFilename);
                            // HTTP "IfNoneMatch = ETag.All": il server accetta la scrittura solo se non esiste ancora nulla a questo nome
                            // Nel raro caso in cui un altro device scrive un file con lo stesso file name appena prima del device attuale
                            var conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
                            BlobContentInfo blobContentInfo;
                            using (var uploadStream = System.IO.File.OpenRead(localFilePath))
                            {
                                blobContentInfo = await blobItemClient.UploadAsync(
                                    uploadStream, new BlobUploadOptions { Conditions = conditions });
                            }

                            journal.Append(deviceId, JournalOperationType.Create, blobFilename, null,
                                blobContentInfo.ETag.ToString(), localHash);

                            onFileUploaded?.Invoke(blobFilename);

                            if (deleteUploadedFile) System.IO.File.Delete(localFilePath);
                        }
                        catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 409)
                        {
                            Console.WriteLine($"'{blobFilename}': REFUSED,blob already existing.");
                        }
                    }
                }

                // Cancellazioni
                foreach (BlobItem orphanBlob in blobItems)
                {
                    JournalEntry? lastEntry = journal.GetLastEntryForBlob(orphanBlob.Name);
                    if (lastEntry == null || lastEntry.OperationType == JournalOperationType.Delete)
                    {
                        continue; // Non cancello il blob se mai tracciato da questo dispositivo, o già segnato come cancellato
                    }

                    BlobClient? orphanClient = blobContainerclient?.GetBlobClient(orphanBlob.Name);
                    if (orphanClient == null || lastEntry.ResultingETag == null) //scarta eventuali situazioni anomale
                    {
                        continue;
                    }

                    try
                    {
                        //cancella solo se l'ETag attuale sul server è ancora quello che conoscevo il device
                        await orphanClient.DeleteAsync(conditions: new BlobRequestConditions { IfMatch = new ETag(lastEntry.ResultingETag) });
                        //resultingETag a null nel journal (non esiste più), contentHash registra cosa è stato cancellato
                        journal.Append(deviceId, JournalOperationType.Delete, orphanBlob.Name, lastEntry.ResultingETag, null, lastEntry.ContentHash);
                        onFileDeleted?.Invoke(orphanBlob.Name);
                    }
                    catch (RequestFailedException ex) when (ex.Status == 412 || ex.Status == 404)
                    {
                        Console.WriteLine($"'{orphanBlob.Name}': delate FAILED, blob last version not matching.");
                    }
                }
            }
            catch (RequestFailedException e)
            {
                Console.WriteLine(e.Message);
                syncRunning = false;
                throw;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                syncRunning = false;
                throw;
            }

            syncRunning = false;
        }

        public async Task<List<string>> ListItemsToSync()
        {
            Console.WriteLine("Listing the items to sync.");
            List<string> itemsToSync = new List<string>();

            try
            {
                if (blobContainerclient != null)
                    await foreach (BlobItem blobItem in blobContainerclient.GetBlobsAsync())
                    {
                        // I segmenti di journal vengono skippati 
                        if (blobItem.Name.StartsWith(JournalPrefix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        Console.WriteLine($"Checking blob: {blobItem.Name}");

                        // Confronto sull'ETag noto dal journal, non sui timestamp 
                        JournalEntry? lastEntry = journal.GetLastEntryForBlob(blobItem.Name);
                        string? knownETag = NormalizeETag(lastEntry?.ResultingETag);
                        string? currentETag = NormalizeETag(blobItem.Properties.ETag?.ToString());

                        if (knownETag != currentETag)
                        {
                            // if we are here something is out of sync
                            itemsToSync.Add(blobItem.Name);
                        }
                    }
            }
            catch (RequestFailedException e)
            {
                logger.LogError("Error " + e.Message);
                throw;
            }
            return itemsToSync;
        }

        public async Task<bool> CheckForItemsToSync()
        {
            Console.WriteLine("Check for items to sync.");
            return (await ListItemsToSync()).Count > 0;
        }

        public async Task SyncBlobsToLocalDirectory(
            Action<List<string>>? filesToDownload = null,
            Action<string>? statusMessage = null,
            Action<string>? onDownloadStarted = null,
            Action<string>? onFileDownloaded = null,
            Action<string>? onFileDeleted = null)
        {
            syncTerminationHandler = false;

            try
            {
                List<string> itemsToSync = await ListItemsToSync();
                filesToDownload?.Invoke(itemsToSync);
                int totFileToDownloadCount = itemsToSync.Count;
                int counterRemainingFiles = totFileToDownloadCount;
                string statusStr = counterRemainingFiles.ToString() + "/" + totFileToDownloadCount.ToString();
                statusMessage?.Invoke(statusStr);

                var itemsToSyncSet = new HashSet<string>(itemsToSync, StringComparer.Ordinal);

                // Nomi di tutti i blob attualmente sul server
                var currentBlobNames = new HashSet<string>(StringComparer.Ordinal);

                if (blobContainerclient != null)
                    await foreach (BlobItem blobItem in blobContainerclient.GetBlobsAsync())
                    {
                        currentBlobNames.Add(blobItem.Name);

                        if (syncTerminationHandler)
                        {
                            syncRunning = false;
                            logger.LogWarning("Sync interrupted by the user");
                            return;
                        }

                        if (!itemsToSyncSet.Contains(blobItem.Name))
                        {
                            continue;
                        }

                        BlobClient blobClient = blobContainerclient.GetBlobClient(blobItem.Name);

                        // Create local file path
                        string localFilePath = Path.Combine(blobSyncFolder, blobItem.Name);

                        // se il file locale ha una modifica non ancora sincronizzata con successo, non sovrascriverla
                        JournalEntry? ownLastEntry = journal.GetLastEntryForBlob(blobItem.Name);
                        if (ownLastEntry != null && System.IO.File.Exists(localFilePath))
                        {
                            string localHash = ComputeSha256Hex(System.IO.File.ReadAllBytes(localFilePath));
                            if (localHash != ownLastEntry.ContentHash)
                            {
                                Console.WriteLine($"Skipped blob: {blobItem.Name} (modifica locale non ancora sincronizzata)");
                                continue;
                            }
                        }


                        Directory.CreateDirectory(Path.GetDirectoryName(localFilePath) ?? string.Empty);

                        BlobDownloadInfo blobDownloadInfo = await blobClient.DownloadAsync();

                        try
                        {
                            onDownloadStarted?.Invoke(blobItem.Name);
                            // FileMode.Create sovrascrive il file esistente o lo crea se non esiste
                            using (FileStream fs = new FileStream(
                                localFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                            {
                                await blobDownloadInfo.Content.CopyToAsync(fs);
                                await fs.FlushAsync();
                            }

                            string downloadedETag = blobDownloadInfo.Details.ETag.ToString();
                            string downloadedHash = ComputeSha256Hex(System.IO.File.ReadAllBytes(localFilePath));
                            //registro la nuova modifica o una nuova voce
                            journal.Append(deviceId,
                                ownLastEntry == null ? JournalOperationType.Create : JournalOperationType.Modify,
                                blobItem.Name, ownLastEntry?.ResultingETag, downloadedETag, downloadedHash);

                            counterRemainingFiles--;
                            statusStr = counterRemainingFiles.ToString() + "/" + totFileToDownloadCount.ToString();
                            statusMessage?.Invoke(statusStr);

                            onFileDownloaded?.Invoke(localFilePath);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError("Error in syncing file, skipping and going to the next file" + ex.Message);
                        }

                        
                    }

                ReconcileLocalDeletions(currentBlobNames, onFileDeleted);
            }
            catch (RequestFailedException e)
            {
                logger.LogError("Error " + e.Message);
                throw;
            }
        }

        private static bool IsFileLocked(IOException exception)
        {
            int errorCode = Marshal.GetHRForException(exception) & ((1 << 16) - 1);
            return errorCode == 32 || errorCode == 33; // ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION
        }

        /// <summary>
        /// Upload a single file
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        /// 
        // public async Task<bool> upload(string filePath)
        // {
        //     try
        //     {
        //         string filename = Path.GetFileName(filePath);
        //         BlobClient? blob = blobContainerclient?.GetBlobClient(filename);
        //         var stream = System.IO.File.OpenRead(filePath);
        //         if (blob != null)
        //         {
        //             await blob.UploadAsync(stream);
        //         }
        //         else
        //         {
        //             //TODO: log some error or throw new Exception();
        //         }
        //     }
        //     catch (Exception ex)
        //     {
        //         Console.WriteLine("Upload failed - " + ex.Message);
        //         return false;
        //     }
        //     return false;

        /// <summary>
        /// Uploads a file using optimistic concurrency (ETag) and appends a corresponding entry to the local journal.
        /// </summary>
        /// <param name="filePath">Local path of the file to upload.</param>
        /// <param name="basedOnETag">
        /// Expected remote ETag for concurrency control.
        /// If <c>null</c>, enforces atomic creation (<c>If-None-Match: *</c>), failing if the blob already exists.
        /// If non-null, enforces conditional update (<c>If-Match: basedOnETag</c>), failing if modified concurrently.
        /// </param>
        /// <returns>The new ETag generated by Azure upon successful upload.</returns>
        /// <exception cref="RequestFailedException">Thrown when precondition (HTTP 412) or conflict (HTTP 409) check fails.</exception>
        public async Task<string> uploadTracked(string filePath, string? basedOnETag)
        {
            if (blobContainerclient == null)
            {
                throw new InvalidOperationException("Blob container client is null.");
            }

            ValidateBasedOnETag(basedOnETag, allowNull: true);

            string filename = Path.GetFileName(filePath);
            byte[] content = System.IO.File.ReadAllBytes(filePath);
            string contentHash = ComputeSha256Hex(content);

            BlobClient blob = blobContainerclient.GetBlobClient(filename);
            // Se l'etag è nullo carica solo per blob non attulmente esistenti
            // altrimenti se etag ha un valore confrontalo con quello del blob sul server
            var conditions = basedOnETag == null
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }
                : new BlobRequestConditions { IfMatch = new ETag(basedOnETag) };

            using var stream = new MemoryStream(content);
            BlobContentInfo info = await blob.UploadAsync(stream, new BlobUploadOptions { Conditions = conditions });

            string newETag = info.ETag.ToString();
            JournalOperationType opType = basedOnETag == null ? JournalOperationType.Create : JournalOperationType.Modify;
            journal.Append(deviceId, opType, filename, basedOnETag, newETag, contentHash);
            return newETag;
        }

        /// <summary>
        /// Deletes a remote blob conditionally based on its expected ETag, removes the local file, and records a Delete tombstone in the journal.
        /// </summary>
        /// <param name="blobName">Remote blob name to delete.</param>
        /// <param name="basedOnETag">The expected ETag; cannot be null or empty.</param>
        /// <exception cref="RequestFailedException">Thrown if the blob has changed remotely since <paramref name="basedOnETag"/> (HTTP 412).</exception>
        public async Task deleteTracked(string blobName, string basedOnETag)
        {
            if (blobContainerclient == null)
            {
                throw new InvalidOperationException("Blob container client is null.");
            }

            ValidateBasedOnETag(basedOnETag, allowNull: false);

            BlobClient blob = blobContainerclient.GetBlobClient(blobName);
            await blob.DeleteAsync(conditions: new BlobRequestConditions { IfMatch = new ETag(basedOnETag) });


            string? deletedContentHash = journal.GetLastEntryForBlob(blobName)?.ContentHash;
            journal.Append(deviceId, JournalOperationType.Delete, blobName, basedOnETag, null, deletedContentHash);

            string localFilePath = Path.Combine(blobSyncFolder, blobName);
            if (System.IO.File.Exists(localFilePath))
            {
                System.IO.File.Delete(localFilePath);
            }
        }

        /// <summary>
        /// Validates that an expected ETag string is well-formed, preventing unintentional unconditional operations.
        /// </summary>
        private static void ValidateBasedOnETag(string? basedOnETag, bool allowNull)
        {
            if (basedOnETag == null)
            {
                if (!allowNull)
                {
                    throw new ArgumentNullException(nameof(basedOnETag), "Etag can't be null for this operation.");
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(basedOnETag))
            {
                throw new ArgumentException(
                     "ETag can't be empty — an empty string is silently ignored by the SDK, making the write unconditional. Use null instead.");
            }
        }

        /// <summary>
        /// Retrieves the current ETag of a remote blob, returning <c>null</c> if the blob does not exist (404).
        /// </summary>
        public async Task<string?> GetRemoteETagAsync(string blobName)
        {
            if (blobContainerclient == null)
            {
                throw new InvalidOperationException("Blob container client is null.");
            }

            BlobClient blob = blobContainerclient.GetBlobClient(blobName);
            try
            {
                BlobProperties props = await blob.GetPropertiesAsync();
                return props.ETag.ToString();
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        /// <summary>
        /// Verify hash chain tamper-evidence)
        /// </summary>
        public int VerifyJournal() => journal.VerifyChain();

        /// <summary>
        /// Publishes the local device journal to the container at '_journal/{deviceId}.ndjson'.
        /// </summary>
        public async Task PushJournalAsync()
        {
            if (blobContainerclient == null)
            {
                throw new InvalidOperationException("Blob container client is null.");
            }

            BlobClient segmentBlob = blobContainerclient.GetBlobClient(JournalSegmentBlobName);
            byte[] content = System.IO.File.Exists(journal.FilePath)
                ? System.IO.File.ReadAllBytes(journal.FilePath)
                : Array.Empty<byte>();

            using var stream = new MemoryStream(content);
            await segmentBlob.UploadAsync(stream, overwrite: true);
        }

        /// <summary>
        /// Pulls other devices' journals from the container to a local directory.
        /// </summary>
        public async Task<List<Guid>> PullJournalsAsync()
        {
            if (blobContainerclient == null)
            {
                throw new InvalidOperationException("Blob container client is null.");
            }

            Directory.CreateDirectory(remoteJournalsFolder);
            var pulled = new List<Guid>();

            await foreach (BlobItem item in blobContainerclient.GetBlobsAsync(
                traits: BlobTraits.None, states: BlobStates.None, prefix: JournalPrefix, cancellationToken: default))
            {
                string guidPart = item.Name.Substring(JournalPrefix.Length).Replace(".ndjson", "");
                if (!Guid.TryParse(guidPart, out Guid remoteDeviceId) || remoteDeviceId == deviceId)
                {
                    continue;
                }

                BlobClient segmentBlob = blobContainerclient.GetBlobClient(item.Name);
                string localPath = Path.Combine(remoteJournalsFolder, guidPart + ".ndjson");
                await segmentBlob.DownloadToAsync(localPath);
                pulled.Add(remoteDeviceId);
            }

            return pulled;
        }

        /// <summary>
        /// Deletes local files that are missing from the remote container, provided they have no pending local modifications.
        /// </summary>
        /// <param name="currentBlobNames">Set of all blob names currently present in the remote container.</param>
        /// <param name="onFileDeleted">Optional callback invoked when a local file is deleted.</param>
        /// <remarks>
        /// A local file is only deleted if its current content hash matches the last recorded journal hash. 
        /// If the file was modified locally, it is preserved to avoid data loss.
        /// </remarks>
        private void ReconcileLocalDeletions(HashSet<string> currentBlobNames, Action<string>? onFileDeleted = null)
        {
            foreach (string blobName in journal.GetAllTrackedBlobNames())
            {
                JournalEntry? ownLastEntry = journal.GetLastEntryForBlob(blobName);
                if (ownLastEntry == null || ownLastEntry.OperationType == JournalOperationType.Delete)
                {
                    continue; // mai sincronizzato con successo, o già segnato come cancellato
                }

                string localFilePath = Path.Combine(blobSyncFolder, blobName);
                if (!System.IO.File.Exists(localFilePath))
                {
                    continue; // niente da cancellare in locale
                }

                string localHash = ComputeSha256Hex(System.IO.File.ReadAllBytes(localFilePath));
                if (localHash != ownLastEntry.ContentHash)
                {
                    continue; // modifica locale non ancora sincronizzata, non cancellare
                }

                if (currentBlobNames.Contains(blobName))
                {
                    continue; // esiste ancora sul server, non è stato cancellato
                }

                System.IO.File.Delete(localFilePath);
                journal.Append(deviceId, JournalOperationType.Delete, blobName, ownLastEntry.ResultingETag, null, ownLastEntry.ContentHash);
                onFileDeleted?.Invoke(blobName);
            }
        }

        public void createFolder()
        {
            try
            {
                Directory.CreateDirectory(blobSyncFolder);
            }
            catch (Exception ex)
            {
                // UnauthorizedAccessException
                // IOException
                // NotSupportedException
                // PathTooLongException
                // SecurityException
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region private_methods

        /// <summary>
        /// Return the connection string to the azure storage account given the accountName and account key
        /// </summary>
        /// <param name="accountName"> the blob storage created for the user </param>
        /// <param name="accountKey"> the key obtained from microsoft </param>
        /// <returns></returns>
        private string getConnectionString(string accountName, string accountKey)
        {
            string connectionString = "DefaultEndpointsProtocol=https;AccountName=" + accountName;
            connectionString += ";AccountKey=" + accountKey + ";EndpointSuffix=core.windows.net";
            if (!IsConnectionStringValid(connectionString))
                return "error";

            return connectionString;
        }

        private static string getFullBlobName(string fullPath, string knownPart)
        {
            if (!fullPath.StartsWith(knownPart, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"'{fullPath}' non è sotto la cartella attesa '{knownPart}'.", nameof(fullPath));
            }

            string returnPath = fullPath.Substring(knownPart.Length).Replace(@"\", "/").TrimStart('/');
            return returnPath;
        }

        private BlobItem? FindBlobByName(List<BlobItem> blobItems, string blobFilename)
        {
            for (int i = 0; i < blobItems.Count; i++)
            {
                if (blobItems[i].Name == blobFilename)
                {
                    // Blob found, remove it from the list and return the BlobItem
                    BlobItem foundBlob = blobItems[i];
                    blobItems.RemoveAt(i); // Remove the found item
                    //Console.WriteLine($"Found and removed file: {foundBlob.Name}");
                    return foundBlob;
                }
            }

            // If no blob was found with the specified file name, return null
            return null;
        }

        private async Task<List<BlobItem>> fromPageableToList(AsyncPageable<BlobItem>? blobPropertyList)
        {
            // Create a new list to store BlobItems
            List<BlobItem> blobItems = new List<BlobItem>();
            if (blobPropertyList == null)
            {
                return blobItems;
            }

            // Convert the AsyncPageable<BlobItem> to a List<BlobItem> by asynchronously enumerating it
            await foreach (BlobItem blobItem in blobPropertyList)
            {
                blobItems.Add(blobItem);
            }

            return blobItems;
        }

        private static bool IsConnectionStringValid(string connectionString)
        {
            try
            {
                BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);
                // Un account senza ancora nessun container e' comunque una stringa di connessione valida
                _ = blobServiceClient.GetBlobContainers().Take(1).ToList();
                return true;
            }
            catch (Exception ex)
            {
                // An exception indicates that the connection string is invalid
                Console.WriteLine("Invalid connection string  " + ex.Message);
                return false;
            }
        }

        private static string ComputeSha256Hex(byte[] content) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content));

        /// <summary>
        /// Strips enclosing double quotes from an ETag string.
        /// The Azure SDK returns quoted ETags from point operations (upload, GetProperties), 
        /// but unquoted ones from blob listings (GetBlobsAsync).
        /// </summary>

        private static string? NormalizeETag(string? etag) =>
            etag != null && etag.Length >= 2 && etag[0] == '"' && etag[^1] == '"'
                ? etag[1..^1]
                : etag;

        private static Guid LoadOrCreateDeviceId(string filePath)
        {
            if (File.Exists(filePath) && Guid.TryParse(File.ReadAllText(filePath).Trim(), out Guid existing))
            {
                return existing;
            }

            var id = Guid.NewGuid();
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, id.ToString());
            return id;
        }

        #endregion

        #region private_members

        private string blobStorageConnectionString = "";// { get; set; }
        private string blobStorageContainerName = "";// { get; set; }
        private BlobContainerClient? blobContainerclient = null;
        private IAsyncEnumerable<Page<BlobItem>>? blobStorageContainerList = null;
        private bool syncRunning = false; // true when sync is in progress, false otherwise
        private bool syncTerminationHandler = false; // true to break the sync

        private readonly ILogger logger;
        private readonly LocalJournalStore journal;
        private readonly Guid deviceId;


        private const string JournalPrefix = "_journal/";
        private string JournalSegmentBlobName => JournalPrefix + deviceId + ".ndjson";
        private string remoteJournalsFolder => Path.Combine(blobSyncFolder, "..", "journals-remote");

        #endregion

        #region public_members

        public string blobSyncFolder; //C:/users/user_name/Documents/MY_Data/&blobStorageContainerName

        #endregion

    }
}
