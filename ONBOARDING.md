# Listenarr session handoff — 2026-05-17 (mid-rebase)

Kevin's fork of [Listenarrs/Listenarr](https://github.com/Listenarrs/Listenarr). See `CLAUDE.md` for the project guide. This doc captures the state of an in-progress rebase from old canary (`6246e960` / v0.3.1) onto new canary (`31b6c628` / v0.4.1) that was paused after PR #535/#492 turned out to be larger architectural surgery than the previous handoff anticipated.

**Next session's focus: finish chunk 3 — the four remaining redesign ports (#2, #4, #7, #5). See "What's left" below and `REBASE_TRIAGE.md` for per-commit notes.**

## Where things stand

### Live deploy (UNCHANGED this session)

- **Live image:** `listenarr:local-20260516-1940` (head commit `1d6e84c1` on the *pre-rebase* `kevin/live`).
- **Live URL:** https://your-host.example.
- The deployed branch `fork/kevin/live` still points at `c5fabefb` (the previous-session handoff commit). The rebase work happened on a parallel `kevin/live-rebased` branch and **has not been promoted**. Rolling back is just "stay on the current image."

### Rebase work this session — on `kevin/live-rebased`

| | |
|---|---|
| **Base** | `canary` (`31b6c628`), synced from upstream via `scripts/sync-upstream.sh` in phase 1. |
| **Tip** | `14cad72c` `feat: promote local audiobook metadata from files`. |
| **Commits above canary** | **40** (35 leaf cherry-picks + 4 chunk-2 mechanical ports + 1 chunk-3 redesign port). |
| **Tests** | dotnet 701/701, vitest 363/363, `npm run build` green. |
| **Pushed to** | `fork/kevin/live-rebased`. Not pushed to `fork/kevin/live`. |

### What was decided this session (and what changed from the previous handoff plan)

The previous handoff predicted "conflicts cluster in LibraryController.cs / IServices.cs / ImportService.cs / NzbgetAdapterTests / package.json." Wrong — PR #535/#492 was an **architectural refactor** that **deleted 21 backend files** kevin/live modifies. Most kevin/live commits needed re-implementation against renamed/relocated targets, not conflict resolution. See `REBASE_TRIAGE.md` (root) for the per-commit analysis.

Triage produced three buckets:

1. **35 leaf commits** — cherry-pick mechanically, some CHANGELOG and FE conflicts. Done in chunk 1.
2. **4 mechanical ports** — content survives but the file moved/renamed. Done in chunk 2.
3. **5 redesign ports** — same intent, different shape. 1 done (Pass A), 4 remaining.

**Two commits dropped permanently** (Kevin's call, 2026-05-17):
- `426f1945` `feat(search): generalize language filter to any preferred language`
- `72cf80ce` `feat(search): reject foreign-language editions when the profile wants English`

Reason: upstream now has its own language-mismatch scoring in `listenarr.application/Search/SearchResultScorer.cs` (`LanguageMismatchPenalty=-15`, `LanguageMissingPenalty=-10`, `profile.PreferredLanguages`). Kevin's hard-filter approach is replaced by upstream's soft-penalty approach. Loss accepted: foreign-language results can still win if nothing else is available (penalty, not reject). The pre-ingest verification commit (#5, see below) also lost its language-rejection half for this reason — only the music-album-shape check is being ported.

### Notable conflict-resolution decisions in chunk 1

- `EditAudiobookModal.vue` — the `57651939 fix(edit-modal): regroup backfill buttons` cherry-pick wanted to put "Fill missing from file" + "Fill missing from online…" side-by-side in a `fill-missing-actions` div. "Fill missing from file" depends on `fillMissingMetadata` which is added by commit `#2` (`01477425`, not yet ported). **Resolution: dropped the file button for now; kept the regrouping + online button.** When `#2` lands, add the file button back into the existing `fill-missing-actions` div.
- `LibraryController.cs` — when porting `#9 a38c82eb` (cover-art sweep), the conflict context included a `BackfillMetadata` endpoint that belongs to `#2` (`01477425`). **Dropped that endpoint from chunk 2's port; it ships with `#2`.**
- `AudioFileServiceTests.cs` (existing-file additions) — two integration tests dropped with TODO comments because they used the old `AudioFileService(scopeFactory, logger, memoryCache, limiter)` ctor signature, incompatible with the new 10-param primary ctor on `AudiobookFileService`. **Unit-level promotion coverage is preserved** in `AudioFileService_PromotionTests.cs`. Re-port the integration tests against the new DI shape in a follow-up.

### Pass A port (#1, `14cad72c`) — what changed from the original

Original kevin/live commit `bd394aea` assumed:
- `AudioFileService` lived in `listenarr.api/Services/` with 4-arg ctor `(scopeFactory, logger, memoryCache, limiter)`
- Method signature `EnsureAudiobookFileAsync(int audiobookId, string filePath, string? source)`
- Used `scope.ServiceProvider.GetRequiredService<IMetadataService>()` and `scope.ServiceProvider.GetService<IImageCacheService>()`

Upstream after the refactor:
- `AudiobookFileService` in `listenarr.application/Audiobooks/`, primary constructor (C# 12)
- Method signature `EnsureAudiobookFileAsync(Audiobook audiobook, string filePath, string? source = "scan")`
- Already injects `IMetadataService` and `IFfmpegService` via primary ctor

Port: added `IAudiobookRepository audiobookRepository` and `IImageCacheService imageCache` to the primary constructor. Rewrote `PromoteLocalMetadataAsync(Audiobook, AudioMetadata, string filePath)` (dropped the `IServiceScope` arg, uses injected services). Update is via `audiobookRepository.UpdateAsync(audiobook)` against the mutated parameter, not a fresh `GetByIdAsync`. All `_logger` references rewritten to `logger` (primary-ctor parameter, not a field). All other touched files (FfmpegService, MetadataService, ImageCacheService, LibraryAddService) auto-merged correctly into their new locations.

## What's left

### Chunk 3 — 4 redesign ports remaining

Per `REBASE_TRIAGE.md`:

| # | Commit | Subject | Action | Dependencies |
|---|---|---|---|---|
| 2 | `01477425` | Opt-in full metadata rescan | port-with-redesign | Builds on `#1` Pass A's `PromoteLocalMetadataAsync` (which is now done) |
| 4 | `ee9cd01c` | Relevance filter + cat 3030 | port-with-redesign | Restructure as new context-aware `ISearchResultFilter` in `listenarr.application/Search/Filters/` (Kevin's choice). |
| 7 | `a79bd2cb` | TitleMatcher for backfill | port-with-redesign | Needs `SignificantTokens` from `#4`'s `RelevanceFilter`. |
| 5 | `a2e3a4f3` | Pre-ingest verification | port-with-redesign | **Drop language half; port music-shape only** (Kevin's choice). Re-target to `listenarr.application/Downloads/DownloadImportService.cs`. |

**Recommended ordering: 2 → 4 → 7 → 5.** `#2` is independent; `#7` is unblocked once `#4` lands; `#5` is fully independent of the others.

#### #2 (`01477425`) — what the cherry-pick wanted vs what to do

Original patch shape (won't merge cleanly):
- Changed `EnsureAudiobookFileAsync` signature from `(Audiobook, …)` to `(int audiobookId, …, bool forceMetadataRefresh)`
- Added `ScanJob.ForceMetadataRefresh` + `SkipMissingBasePathCleanup` flags
- Plumbed through `ScanQueueService` + `ScanBackgroundService`
- Added `POST /library/backfill-metadata` and `forceMetadataRefresh: true` query on `POST /library/{id}/scan`
- FE: "Full metadata rescan" checkbox on Library Import page; "Fill missing from file" button in Edit modal

Recommended approach for the port:
1. Add `bool forceMetadataRefresh = false` param to `EnsureAudiobookFileAsync(Audiobook, string, string?, bool)`.
2. Update `IAudiobookFileService` interface to match.
3. When `forceMetadataRefresh` is true and the file already exists: re-extract metadata via injected `metadataService.ExtractFileMetadataAsync`, call the existing `PromoteLocalMetadataAsync`, then `audiobookRepository.UpdateAsync(audiobook)`. Don't return early.
4. Add `ForceMetadataRefresh` + `SkipMissingBasePathCleanup` to `ScanJob` in `listenarr.application/Audiobooks/`.
5. Add overload to `IScanQueueService.EnqueueScanAsync(Audiobook, string? path = null, bool forceMetadataRefresh = false, bool skipMissingBasePathCleanup = false)`.
6. Wire `ScanBackgroundService` to read and propagate both flags through to `EnsureAudiobookFileAsync`.
7. Add the two controller endpoints to `LibraryController` (the `BackfillMetadata` endpoint chunk 2 dropped, plus `forceMetadataRefresh` query support on `ScanAudiobookFiles`).
8. FE: re-add the dropped "Fill missing from file" button to the `fill-missing-actions` div in `EditAudiobookModal.vue`; add the import-page checkbox + wiring.

#### #4 (`ee9cd01c`) — Kevin chose "restructure as context-aware `ISearchResultFilter`"

Upstream has `listenarr.application/Search/Filters/SearchResultFilterPipeline.cs` orchestrating sibling filters (Kindle / ProductLike / Promotional / AudiobookOnly / MissingInformation). None take per-audiobook context.

Plan:
1. Extend `ISearchResultFilter` (or add a sibling interface) to accept optional `Audiobook?` context.
2. Add `RelevanceFilter : ISearchResultFilter` that does token-overlap against `audiobook.Title` + `audiobook.Authors[0]`, rejects results with <30% overlap. Lift `SignificantTokens` (the stop-word-filtered tokenizer) into a sibling helper class so `#7` can use it.
3. Register the new filter in `Search/Filters/` registration site.
4. The "restrict automatic Newznab queries to category 3030" half: add a category param to `searchService.SearchAsync(query, isAutomaticSearch: true, category: 3030)`. Touch `AutomaticSearchService` and the indexer query builder.

#### #7 (`a79bd2cb`) — TitleMatcher

Original commit added `Services/Scoring/TitleMatcher.cs` (kevin-only file, no upstream conflict) and one call site change in `SearchService.IntelligentSearchAsync`'s AUTHOR_TITLE branch.

Plan:
1. Add `listenarr.application/Search/TitleMatcher.cs` (mirrors the original; uses `SignificantTokens` from `#4`).
2. In `listenarr.application/Search/SearchService.cs` (line ~711 area), replace the naive `b.Title.IndexOf(titleVal, StringComparison.OrdinalIgnoreCase) >= 0` with `TitleMatcher.Matches(b.Title, titleVal)`.
3. Port the 18 unit tests from kevin/live (they were standalone).

#### #5 (`a2e3a4f3`) — pre-ingest music-shape only (drop language half)

Original commit changes:
- New `Services/PreIngestVerification.cs` (music-album shape + language tag check)
- `Services/ImportService.cs` calls it after files land
- `Services/FfmpegService.cs` reads `language` tag
- `Services/Scoring/LanguageFilter.cs` adds `MapLanguageTag` helper

Plan (language half dropped):
1. Add `listenarr.application/Downloads/PreIngestVerification.cs` with **only the music-album shape check** (≥10 audio files, median duration <5min via existing ffprobe metadata).
2. In `listenarr.application/Downloads/DownloadImportService.cs` (`ImportDownloadFilesAsync`), call `PreIngestVerification.Inspect(...)` after files are on disk but before they're committed; reject with logged reason if shape fails.
3. Skip the FfmpegService `language` tag changes (no consumer left).
4. Port the music-shape half of the 142-line test file; drop the language tests.

### Chunk 4 (after chunk 3) — verify + deploy

- `cd tests && dotnet test` — expected ~750+ (was 701 after Pass A, plus ~50 new tests from #2/#4/#7/#5).
- `cd fe && npx vitest run` — expect new tests for #2 FE changes.
- `cd fe && npm run build` — must pass (don't trust `vue-tsc --noEmit` alone — see memory `feedback_browser_smoke_for_fe_deploys.md`).
- Promote `kevin/live-rebased` to `kevin/live`: `git checkout kevin/live && git reset --hard kevin/live-rebased && git push fork kevin/live --force-with-lease`.
- `./scripts/deploy-local.sh --skip-tests` — Kevin will browser-verify in incognito.

### Existing PR branches (NOT rebased this session)

Branches `fix/automatic-search-relevance`, `feat/audiobooks-view-mode-per-grouping`, `feat/audiobooks-collection-ready-count`, `feat/audiobooks-grouped-list-view`, `fix/nzbget-xmlrpc-auth`, `feat/library-dashboard`, `feat/add-series`, `feat/metadata-backfill`, `feat/pre-ingest-verification`, `feat/language-filter`, `feat/pass-a-local-metadata-enrichment` — all still pointing at old canary base. After chunk 3+4 lands, rebase each onto the new canary.

Special notes from the previous handoff:
- PR #591 (search-result filtering) — reviews resolved-in-agreement. After rebase, ask Kevin whether to un-draft.
- PR #590, #589, #585 — parked behind maintainer pacing. Rebase + leave drafted.
- PR #580 (nzbget) — open. nzbget adapter conflicts already resolved in chunk-2 port; PR branch needs same treatment.

### Pacing decision (not actioned this session)

Per `feedback_listenarr_upstream_pr_pacing.md`, the maintainer asked for slower pacing on 2026-05-14. With PR #576 merged 2026-05-10 and v0.4.0 cut 2026-05-14 (same day as the pacing reply), the foundation phase is ambiguous. **Previous-session plan was to post a short comment on PR #590's thread** asking whether the foundation phase is open for new feature PRs. That comment was not posted this session — chunk 3+4 came first. Either post the comment now or carry it into the next session.

## Known bugs to fix next session

1. **"2010 — Odyssey Two" by Arthur C. Clarke returns only foreign editions in the backfill modal.** (Carried over from previous session.) Audible US genuinely doesn't carry the English audiobook. Remediation paths: (a) expose `?region=uk` in the modal, (b) extend candidate discovery to OpenLibrary print editions.

## Operational notes

- **Deploy:** `./scripts/deploy-local.sh --skip-tests` (project root only — script enforces cwd). Browser-verify in incognito after every FE-bundle change.
- **Verify before declaring FE clean:** run `npm run build`, not just `npx vue-tsc --noEmit`. Memory `feedback_browser_smoke_for_fe_deploys.md`.
- **Push targets:** `fork` only. Today's work is on `fork/kevin/live-rebased`; old `fork/kevin/live` is untouched.
- **Pacing:** still default to no new upstream PRs until you've pinged or confirmed.

## When in doubt

CLAUDE.md is the source of truth for branch strategy, deploy, and PR workflow. `REBASE_TRIAGE.md` (root) is the per-commit triage doc for chunk 3. Ask Kevin before opening upstream PRs.
