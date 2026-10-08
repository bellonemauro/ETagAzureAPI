# azure-blob-etag-sync

Sample demonstrating an ETag-backed journaling sync mechanism for Azure Blob Storage.

## Overview

This repository implements:
- Per-device NDJSON journal tracking local operations (create/modify/delete).
- ETag-aware conditional writes using `If-Match` / `If-None-Match`.
- Push/pull of per-device journal segments to `_journal/{deviceId}.ndjson`.
- Tools to verify the local journal chain integrity (tamper-evidence).

Core implementation: `src/ETagAzureAPI/ETagAzureAPI.cs` 

## Quickstart

Prerequisites:
- .NET 8 SDK
- (Optional) Azurite for local Blob Storage emulation

Build:
- dotnet build

Run the console test project:
- dotnet run --project src\ETagAzureAPI.ConsoleTest

Example configuration (environment variables recommended):
- AZURE_STORAGE_CONNECTION_STRING — storage account connection string (do NOT commit)
- BLOB_CONTAINER — target container name
- BLOB_SYNC_FOLDER — local folder to sync
- JOURNAL_FILE — local NDJSON journal file path
- DEVICE_ID_FILE — local file to persist this device identifier

Local Azurite emulator:
- Set `AZURE_STORAGE_CONNECTION_STRING=UseDevelopmentStorage=true` (or use the ETagAzureAPI constructor that targets the emulator).

Example usage (powershell):
- $env:AZURE_STORAGE_CONNECTION_STRING="UseDevelopmentStorage=true"
- dotnet run --project src\MyAzureAPI.ConsoleTest

## Security & secrets

- Never commit connection strings, keys, or `local.settings.json`.
- Use environment variables or `dotnet user-secrets` for local development.
- For production, prefer Managed Identity or Azure Key Vault.
- If CI/CD needs Azure access, use OIDC or a Service Principal with least privilege and store secrets securely.

## ETag / Journal notes

- Uploads use conditional requests:
  - Create: `If-None-Match: *` (fail if blob already exists).
  - Modify/Delete: `If-Match: "<expectedETag>"` (fail if remote version changed).
- Local journal stores the expected ETag and a content hash (SHA-256 hex). The hash is used to avoid unnecessary uploads and to detect unchanged local content.
- `VerifyJournal()` validates the journal chain for tamper-evidence.
- `PushJournalAsync()` writes this device's `_journal/{deviceId}.ndjson` segment to the container.
- `PullJournalsAsync()` downloads other devices' journal segments for inspection/audit.

## Recommended gitignore / files not to commit

- `bin/`, `obj/`, `.vs/`, `*.user`, `*.suo`
- `local.settings.json`, `.env`, `*.key`, `*.pfx`
- Any file containing `UseDevelopmentStorage=true` connection strings or real keys

(Add an appropriate `.gitignore` at repo root. A sample was prepared separately.)

## Tests & CI

- Add unit tests to cover:
  - Journal chain verification
  - ETag conflict handling (simulate 412/409)
  - Push/Pull journal behavior
- CI: create a GitHub Actions workflow that runs `dotnet build` and `dotnet test`. Do not store secrets in the workflow; use repository secrets or OIDC.

## Contributing & License

- This is a student sample. Keep changes focused and documented.
- Suggested license: MIT (add `LICENSE` file if you accept).

## Next steps (optional)

- Rename repository to a clearer name (e.g. `azure-blob-etag-sync`) and update namespaces / project names.
- Add examples showing how to run against azurite vs real Azure.
- Add automated CI and a small integration test that runs against an Azurite container.
