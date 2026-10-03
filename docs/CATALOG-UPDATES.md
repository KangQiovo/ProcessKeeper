# Signed suggestion catalog updates

[English](CATALOG-UPDATES.md) | [简体中文](CATALOG-UPDATES.zh-CN.md) | [繁體中文](CATALOG-UPDATES.zh-TW.md)

The catalog updates **registered-name suggestion labels**, not antivirus signatures or file detection. Watchlist preferences and historical published reports remain separate. A matched name is a prompt to review the installation; it does not establish that current files are malicious. No application inventory is uploaded and no update runs code or uninstalls software.

## In the application

Open **Uninstall → Rule catalog** to see the version, evidence date and official repository. **Check catalog updates** downloads and verifies an offer without changing labels. **Update catalog** requires confirmation, commits the verified catalog and refreshes labels. There are no automatic network checks or automatic applications in this version. A batch uninstall already being confirmed or executed cannot be changed by a catalog update.

The built-in catalog contains 68 product rules and 117 aliases. It works offline. A valid newer cache is loaded on opening the uninstall page; invalid caches report an error while retaining trusted rules. A later valid higher revision can repair a damaged cache. Network, HTTP, timeout, signature, schema and write errors are reported rather than presented as successful updates.

## Source, signature and limits

Only these fixed endpoints are requested:

- `https://raw.githubusercontent.com/KangQiovo/ProcessKeeper/main/catalog/v1/catalog.json`
- `https://raw.githubusercontent.com/KangQiovo/ProcessKeeper/main/catalog/v1/catalog.sig`

Redirects are not followed. System HTTPS certificate validation stays enabled. Downloads are bounded while streaming. JSON is limited to 512 KiB, 500 rules, 2,000 aliases total, 20 aliases per rule, 160 characters per alias and 1,500 characters per localized reason. Every object rejects missing, unknown and duplicate fields. UTF-8 decoding, depth, IDs, dates, names and HTTPS evidence links are validated. Only `exact-name-v1` is accepted; downloads cannot add regular expressions, commands, scripts or match engines.

The 384-byte detached signature is RSA-3072 / SHA-256 / PKCS#1 v1.5 over the **exact JSON bytes**. The public key is embedded in the client; `public-key.xml` is the public reference. Settings import cannot alter the endpoint or trust key. The .NET Framework verifier uses an ephemeral CSP key context, without persisting a user/machine key container. The mechanism uses [.NET RSA verification](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rsa.verifydata).

The cache is one `verified.catalog` envelope containing magic `PKCAT001`, a little-endian 32-bit JSON byte length, raw JSON bytes and the 384 signature bytes. A same-directory temporary file is flushed and replaced atomically. A `FileShare.None` writer lock covers re-reading the latest cache, revision comparison and replacement. A competing writer fails promptly. Failures retain the previous file; a cancelled transfer does not change rules.

Only higher revisions are applied. The same revision with different content and lower revisions are rejected. Corrections and withdrawals must be published as a **new higher revision**, even if that means restoring older rule content. A client rejects revisions below its built-in floor and the highest trusted revision in its current state. This is not absolute rollback protection against an administrator deleting or restoring all local state, replacing the app or compromising the publisher's private key. Blocking network access also cannot prove that no newer catalog exists.

## Schema and maintenance

The top-level fields are exactly `schema` (1), `repository` (`KangQiovo/ProcessKeeper`), increasing integer `revision`, dotted numeric `version`, UTC `publishedUtc`, ISO date `checkedOn`, `matching` (`exact-name-v1`) and `rules`.

Each rule has `id`, `basis` (`watchlist` or `published-report`), `names`, `reason` with all three keys `en`, `zh-Hans`, `zh-Hant`, `sourceUrl` and `sourcePublishedOn`. Watchlists have empty source fields; reported risks require a permitted first-party HTTPS source. Unknown report dates use an empty date, never an invented one. Evidence URL hosts are explicitly allowed by the client; adding a host requires a reviewed app release. Matching uses normalized exact names with the existing limited numeric-version/architecture suffix handling.

1. Review first-party evidence and distinguish current facts from historical installer behavior. Update all three reason translations.
2. Increment `revision`; update version and dates. To withdraw a rule, publish a higher revision without it. Keep a copy of the previous public catalog for comparison.
3. Build Core, keep the offline private key **outside the repository**, and sign using PowerShell 7:

```powershell
./scripts/Sign-Catalog.ps1 -PrivateKeyPath 'E:\offline-keys\catalog-signing.private.xml' -PreviousCatalogPath 'E:\review\previous-catalog.json'
```

4. Run `ProcessKeeper.CatalogUpdate.Tests` for both frameworks. Review the signed JSON diff, version, sources and signature. Publish `catalog.json` and `catalog.sig` together in one commit. A transient mismatched download is rejected; users can retry.
5. Do not format signed JSON after signing. UTF-8 without BOM and LF are used; `.gitattributes` disables text conversion for signed JSON and marks the signature binary. Validate the actual Git blobs before release. Never commit or upload the private key. Back it up offline; key loss or rotation requires a new reviewed app release with a new embedded key.

This repository ships a signed initial catalog, but the endpoints are available only after the files are published to the fixed public repository. Before publication, HTTP 404 is expected and reported accurately.
