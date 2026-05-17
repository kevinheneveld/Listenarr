# kevin/live → origin/canary Rebase Triage

Per-commit triage of the 8 kevin-only commits to rebase on top of `origin/canary`
(tip `31b6c628`, post-refactor `1d6a8e97`). The two language-filter commits
(`426f1945`, `72cf80ce`) are dropped per prior decision.

Project moves to keep in mind:
- `listenarr.api/Services/*` → `listenarr.application/{Audiobooks,Downloads,Search,Metadata,Common,Notification}/*`
- Renames: `AudioFileService` → `AudiobookFileService`, `ImportService` → `DownloadImportService`, `Adapters/NzbgetAdapter` → `listenarr.infrastructure/Adapters/NzbgetAdapter`, `Services/Scoring/*` (no longer exists as a folder; some logic is in `Search/Filters/*` and `Search/CompositeScorer.cs`).
- Interfaces moved to `listenarr.application/Interfaces/*`.

---

## 1. `bd394aea` — feat: promote local audiobook metadata from files (Pass A)

- **Files originally touched (kevin-only):** `Services/AudioFileService.cs`, `Services/FfmpegService.cs`, `Services/ImageCacheService.cs`, `Services/LibraryAddService.cs`, `Services/Metadata/IMetadata.cs`, `Services/Metadata/MetadataService.cs`, plus new `domain/Models/AudiobookExternalIdentifier.cs` helper.
- **Original intent:** When a new `AudiobookFile` row is created during scan, lift embedded file-tag values (title/subtitle/series/series-pos/publisher/language/desc/narrator/authors/ASIN/ISBN) into blank `Audiobook` fields, extract embedded cover art into library storage, and re-sync `ExternalIdentifiers`.
- **New upstream home:**
  - `AudioFileService` → `listenarr.application/Audiobooks/AudiobookFileService.cs` (EnsureAudiobookFileAsync)
  - `FfmpegService` → `listenarr.infrastructure/Ffmpeg/FfmpegService.cs` (probe + `ApplyTagMetadata`)
  - `ImageCacheService` → `listenarr.application/Common/ImageCacheService.cs`
  - `LibraryAddService` → `listenarr.application/Audiobooks/LibraryAddService.cs`
  - `MetadataService` → `listenarr.application/Metadata/MetadataService.cs` (interface in `Interfaces/IMetadataService.cs`)
- **Already solved upstream?** No. `grep -l "PromoteLocalMetadata\|AttachedPicCodec\|StoreLibraryImageBytes\|ExtractEmbeddedCover"` on `origin/canary` returns nothing.
- **Recommended action:** port-with-redesign.
- **Rationale:** Logic is intact and self-contained, but every touched file moved and got renamed; the FfmpegService split (probe → application + run → infrastructure) means tag-extraction additions land in a different file than the AttachedPic detection. `IMetadataService` and `IImageCacheService` both need their new methods added in the application-interface project, with implementations in the new homes.

---

## 2. `01477425` — feat: opt-in full metadata rescan for existing audiobooks

- **Files originally touched (kevin-only):** `Services/AudioFileService.cs`, `Services/IScanQueueService.cs`, `Services/IServices.cs`, `Services/ScanBackgroundService.cs`, `Services/ScanQueueService.cs`, `Controllers/LibraryController.cs`, plus FE.
- **Original intent:** Adds `ScanJob.ForceMetadataRefresh` + `SkipMissingBasePathCleanup` flags so library-wide and per-book rescans can re-run Pass A's promotion against already-tracked files. New endpoints `POST /library/backfill-metadata` and `POST /library/{id}/scan` with `forceMetadataRefresh: true`. UI surfaces on Library Import page and Edit Audiobook modal.
- **New upstream home:**
  - `IScanQueueService` → `listenarr.application/Interfaces/IScanQueueService.cs` (4 methods, no flags)
  - `ScanQueueService`, `ScanBackgroundService`, `ScanJob` → `listenarr.application/Audiobooks/`
  - `AudiobookFileService.EnsureAudiobookFileAsync` (no overload)
  - `LibraryController` still under `listenarr.api/Controllers/`
- **Already solved upstream?** No. Upstream `ScanJob` has no `ForceMetadataRefresh` / `SkipMissingBasePathCleanup`; `grep -lE "ForceMetadataRefresh|backfill-metadata"` is empty.
- **Recommended action:** port-with-redesign (depends on commit 1).
- **Rationale:** Strictly builds on commit 1's `PromoteLocalMetadataAsync`. Once #1 has been ported to `AudiobookFileService`, threading the flag through `ScanJob` → `ScanQueueService` → `ScanBackgroundService` is mechanical. Interface change (`IScanQueueService`) lives in a new project. Worth re-asking Kevin if he wants the UI surfaces upstreamed or kept private given the pacing note.

---

## 3. `737592e7` — fix: add fallback and bulk assignment for wanted quality profiles

- **Files originally touched (kevin-only):** `Services/AutomaticSearchService.cs`, `Services/DownloadService.cs`, plus FE.
- **Original intent:** When an audiobook has no `QualityProfile`, fall back to `qualityProfileService.GetDefaultAsync()` so an unconfigured book still gets searched. UI adds a bulk "Assign default profile to N wanted audiobooks" button.
- **New upstream home:**
  - `AutomaticSearchService` → `listenarr.application/Search/AutomaticSearchService.cs` (still has the `audiobook.QualityProfile == null → return 0` early-out at line ~167)
  - `DownloadService` → `listenarr.application/Downloads/DownloadService.cs` (also calls `searchService.SearchAsync(..., isAutomaticSearch: true)` at line 198)
- **Already solved upstream?** No.
- **Recommended action:** port-mechanically.
- **Rationale:** Small, well-contained behavior change. Apply the same fallback pattern in the new file locations; the only friction is the project rename. UI patch in `WantedView.vue` is unchanged.

---

## 4. `ee9cd01c` — fix(search): reject results whose title is not relevant + restrict automatic search to audiobook category

- **Files originally touched (kevin-only):** `Services/AutomaticSearchService.cs`, `Services/DownloadService.cs`, `Services/QualityProfileService.cs`, `Services/Scoring/SearchResultScorer.cs`, new `Services/Scoring/RelevanceFilter.cs`, `Controllers/LibraryController.cs`.
- **Original intent:** Add a token-overlap relevance check against the audiobook being searched (rejects e.g. Patterson Hood concert vs. James Patterson book). Thread `Audiobook?` through `Score`/`ScoreSearchResults`. Also restrict automatic Newznab queries to category `3030`.
- **New upstream home:**
  - `SearchResultScorer` → `listenarr.application/Search/SearchResultScorer.cs` (signature still `Score(SearchResult, QualityProfile)` — no Audiobook)
  - `QualityProfileService` → `listenarr.application/Audiobooks/QualityProfileService.cs`
  - `AutomaticSearchService` → `listenarr.application/Search/AutomaticSearchService.cs` (still calls `SearchAsync(query, isAutomaticSearch: true)` with no category)
  - There's now a sibling pipeline in `listenarr.application/Search/Filters/*` (Kindle/ProductLike/Promotional/AudiobookOnly/MissingInformation), and an `ISearchResultFilter` interface — `RelevanceFilter` would be a natural new filter, but those filters don't currently receive per-audiobook context.
- **Already solved upstream?** Partial. Upstream now has standalone filters that reject obvious non-audiobook noise, but nothing compares result titles against the specific audiobook being searched, and there's no category restriction on automatic Newznab queries.
- **Recommended action:** surface-to-Kevin.
- **Rationale:** The defensive intent is still valid (upstream filters address adjacent symptoms but not the Patterson Hood case), and the cleanest port may be a new `ISearchResultFilter`-style abstraction that takes per-request context — different from the original scorer-signature change. Kevin should confirm whether he wants to (a) port the scorer-signature change as written, (b) restructure as a new context-aware filter, or (c) defer pending a discussion with upstream maintainer given the open-PR pacing constraint.

---

## 5. `a2e3a4f3` — feat(import): add pre-ingest verification of completed downloads

- **Files originally touched (kevin-only):** `Services/FfmpegService.cs`, `Services/ImportService.cs`, new `Services/PreIngestVerification.cs`, `Services/Scoring/LanguageFilter.cs`.
- **Original intent:** Last-line check after a download lands but before it's committed: reject (a) batches that look like a music album (≥10 audio files, median track length <5min) or (b) audio whose explicit `language` tag isn't in profile preferences.
- **New upstream home:**
  - `ImportService` → `listenarr.application/Downloads/DownloadImportService.cs` (`ImportDownloadFilesAsync`)
  - `FfmpegService` probe → `listenarr.infrastructure/Ffmpeg/FfmpegService.cs`
  - `Services/Scoring/LanguageFilter.cs` — does NOT exist upstream; the whole `Scoring` folder is gone.
- **Already solved upstream?** No.
- **Recommended action:** surface-to-Kevin (split into two ports).
- **Rationale:** The music-album shape check (median duration + file count) is independent and ports cleanly to `DownloadImportService`. The language check depends on `LanguageFilter.MapLanguageTag` — but `LanguageFilter.cs` itself was introduced by the dropped language-filter commits. Either the language half is dropped (music shape only), or `MapLanguageTag` plus the canonical-language tables it needs are extracted as a small standalone helper. Kevin should decide which.

---

## 6. `37f41600` — fix(nzbget): embed credentials in XML-RPC URL so auth survives redirects

- **Files originally touched (kevin-only):** `Services/Adapters/NzbgetAdapter.cs`.
- **Original intent:** Pass `includeCredentials: true` to `DownloadClientUriBuilder.BuildUri` when calling NZBGet's `/xmlrpc` endpoint so `user:pass@host` is in the URL and survives an `AllowAutoRedirect`-stripped `Authorization` header.
- **New upstream home:** `listenarr.infrastructure/Adapters/NzbgetAdapter.cs`, `CallXmlRpcAsync` at line 864 still builds URL via `DownloadClientUriBuilder.BuildUri(client, "/xmlrpc")` without `includeCredentials`. `DownloadClientUriBuilder` is in `listenarr.application/Downloads/` and still has the `includeCredentials = false` default parameter.
- **Already solved upstream?** No — bug still present at canary tip.
- **Recommended action:** port-mechanically.
- **Rationale:** One-line behavior change; only the file path moved. Tests move from `tests/Features/Api/Services/Adapters/` to wherever the canary infrastructure tests live.

---

## 7. `a79bd2cb` — fix(search): tolerate punctuation in backfill candidate title filter (TitleMatcher)

- **Files originally touched (kevin-only):** new `Services/Scoring/TitleMatcher.cs`, `Services/SearchService.cs` (one call site in AUTHOR_TITLE branch).
- **Original intent:** Replace the naive `IndexOf` title narrowing in `IntelligentSearchAsync`'s AUTHOR_TITLE branch with `TitleMatcher.Matches` (normalize punctuation; multi-token overlap fallback) so "1634 - The Baltic War" finds "1634: The Baltic War".
- **New upstream home:**
  - `SearchService.IntelligentSearchAsync` → `listenarr.application/Search/SearchService.cs` line ~711; the naive `b.Title.IndexOf(titleVal, StringComparison.OrdinalIgnoreCase) >= 0` check at line ~712 still present verbatim.
  - `TitleMatcher` has nowhere obvious to live (no `Scoring/` folder); `listenarr.application/Search/` is the natural home.
- **Already solved upstream?** No.
- **Recommended action:** port-with-redesign.
- **Rationale:** Logic is intact. Caveat: `TitleMatcher.Matches` calls `RelevanceFilter.SignificantTokens`, and `RelevanceFilter` comes from commit 4. If commit 4 is deferred/redesigned, `SignificantTokens` will need to be either copied in, lifted into a shared helper, or replaced with a local stop-word splitter. Decide on commit 4 first, then port this.

---

## 8. `abe06be2` — feat(series): auto-cache Audible catalogs for in-library series

- **Files originally touched (kevin-only):** new `Services/SeriesCatalogBackfillService.cs`, `Services/SeriesCatalogService.cs` (+`HasCachedCatalogAsync`), `Services/ISeriesCatalogService.cs`, `Extensions/HostedServiceRegistrationExtensions.cs`.
- **Original intent:** Background service that, every 6h, walks the library for series with ≥2 books, skips already-cached series, and writes Audible-catalog cache rows for the rest. Opt-out via `LISTENARR_AUTO_CACHE_SERIES_CATALOGS=false`. Adds `HasCachedCatalogAsync` to skip without triggering a fetch.
- **New upstream home:**
  - `SeriesCatalogService` → `listenarr.application/Audiobooks/SeriesCatalogService.cs`
  - `ISeriesCatalogService` → `listenarr.application/Interfaces/ISeriesCatalogService.cs`
  - `HostedServiceRegistrationExtensions` still under `listenarr.api/Extensions/`
  - `SeriesCatalogBackfillService` is brand new; would live alongside `SeriesMonitoringBackgroundService` in `listenarr.application/Audiobooks/`.
- **Already solved upstream?** No.
- **Recommended action:** port-mechanically.
- **Rationale:** Self-contained background service plus a one-method interface addition. Move both files into `listenarr.application/Audiobooks/`, add `HasCachedCatalogAsync` to `ISeriesCatalogService` in the interfaces project. Confirm registration call still works in the API-layer extensions file.

---

## 9. `a38c82eb` — feat(library): admin sweep to cache external cover art into local storage

- **Files originally touched (kevin-only):** `Controllers/LibraryController.cs`, `Extensions/AppServiceRegistrationExtensions.cs`, new `Services/Images/ExternalCoverArtSweepService.cs`, new `Services/Images/LibraryImageStorageHelper.cs`, plus FE.
- **Original intent:** New admin endpoint `POST /api/v1/library/cache-external-covers` walks every audiobook, downloads any external `http(s)` cover into local library storage, and rewrites stored URL. Idempotent skip for already-local URLs, throttled for Amazon CDN. UI is a "Run sweep" button in a new General-settings Library Maintenance section.
- **New upstream home:** No upstream equivalent. Natural home is `listenarr.application/Common/` next to `ImageCacheService.cs` (or a new `listenarr.application/Images/` folder). `LibraryController` is still in `listenarr.api/Controllers/`; `AppServiceRegistrationExtensions` still in `listenarr.api/Extensions/`.
- **Already solved upstream?** No.
- **Recommended action:** port-mechanically.
- **Rationale:** Net-new code with one new endpoint and one helper. The on-save caching hook this depends on landed in earlier kevin/live commits (`705f4f88`, `86959f21`) that already merged cleanly. Move the two new service files into the application project, keep the controller change in `listenarr.api`, register the service in the API extensions file.

---

## Summary

| # | Commit     | Subject (short)                                          | Recommended action     |
|---|------------|----------------------------------------------------------|------------------------|
| 1 | `bd394aea` | Pass A: promote local audiobook metadata from files      | port-with-redesign     |
| 2 | `01477425` | Opt-in full metadata rescan for existing audiobooks      | port-with-redesign     |
| 3 | `737592e7` | QP fallback + bulk-assign default profile                | port-mechanically      |
| 4 | `ee9cd01c` | Relevance filter + audiobook-category restriction        | surface-to-Kevin       |
| 5 | `a2e3a4f3` | Pre-ingest verification (music-shape + language)         | surface-to-Kevin       |
| 6 | `37f41600` | NZBGet XML-RPC URL credentials                           | port-mechanically      |
| 7 | `a79bd2cb` | TitleMatcher for backfill candidate filter               | port-with-redesign     |
| 8 | `abe06be2` | Auto-cache Audible catalogs for in-library series        | port-mechanically      |
| 9 | `a38c82eb` | Admin sweep to cache external cover art                  | port-mechanically      |

### Ordering / dependency notes
- Commit 2 depends on commit 1's `PromoteLocalMetadataAsync`.
- Commit 7's `TitleMatcher.Matches` calls `RelevanceFilter.SignificantTokens` from commit 4 — settle commit 4 first, or extract `SignificantTokens` into a shared helper.
- Commit 5's language check depends on `LanguageFilter.MapLanguageTag` from the *dropped* language-filter branch; either drop that half of the verification or lift `MapLanguageTag` as a tiny standalone helper.
- Commits 3, 6, 8, 9 have no kevin-only dependencies and can be ported in isolation.
