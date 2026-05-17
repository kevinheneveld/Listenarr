# Listenarr session handoff — 2026-05-17 (post-rebase ship)

Kevin's fork of [Listenarrs/Listenarr](https://github.com/Listenarrs/Listenarr). See `CLAUDE.md` for the project guide. The multi-session rebase of `kevin/live` onto the refactored canary (v0.4.1) is done, deployed, and PR branches are realigned.

## Where things stand

### Live deploy

- **Live image:** `listenarr:local-20260517-0958` (head commit `07afc34c` on `kevin/live`).
- **Live URL:** https://your-host.example.
- **Rollback target:** `listenarr:local-20260517-0939` (the previous deploy from earlier this session; rollback command is in the deploy script's final output).
- The pre-rebase image was `listenarr:local-20260516-1940` (kevin/live tip `1d6e84c1`); the post-rebase deploy chain so far has been:
  1. `local-20260517-0939` — chunk-3 ports landed (45 commits over canary).
  2. `local-20260517-0958` — adds the auto-pick call-site fix for `RelevanceFilter` in `DownloadService` and `LibraryController` (was missing from the initial #4 port).

### Branch state

| Branch | Tip | Notes |
|---|---|---|
| `canary` | `31b6c628` v0.4.1 | Clean mirror of upstream. |
| `kevin/live` | `07afc34c` | Deployed. 46 commits above canary. Pushed to `fork/kevin/live`. |
| `kevin/live-rebased` | `07afc34c` | Identical to `kevin/live`. Pushed to `fork/kevin/live-rebased`. Kept around as a safety harbor; can be deleted once you trust the live deploy. |

### Chunk-3 ports done this session

| # | Commit on kevin/live | Subject | Notes |
|---|---|---|---|
| 2 | `184cf7d2` | feat: opt-in full metadata rescan for existing audiobooks | `ForceMetadataRefresh` + `SkipMissingBasePathCleanup` flags threaded through `ScanJob` → `ScanQueueService` → `ScanBackgroundService` → `AudiobookFileService`. `BackfillMetadata` endpoint re-added to `LibraryController`. FE: "Full metadata rescan" checkbox + "Fill missing from file" button. |
| 4 | `a40d9c00` + `07afc34c` | fix(search): RelevanceFilter as `ISearchResultFilter` + category 3030 | `ISearchResultFilter` extended with a default-interface-method overload accepting `Audiobook?`. `SearchResultFilterPipeline.ApplyFilters` accepts optional `audiobook` context. New `RelevanceFilter` + `SignificantTokens` helper in `Search/Filters/`. Wired into `AutomaticSearchService`, `DownloadService`, and `LibraryController.ProcessAudiobookForSearchAsync` auto-pick flows. Manual search still fails open (no context). |
| 7 | `5f9244ca` | fix(search): tolerate punctuation in backfill candidate title filter | New `listenarr.application/Search/TitleMatcher.cs`, shares `SignificantTokens` with `#4`. Used at the `SearchService.IntelligentSearchAsync` AUTHOR_TITLE narrow-down call site. |
| 5 | `c65dd0f9` | feat(import): pre-ingest verification (music-shape only) | `listenarr.application/Downloads/PreIngestVerification.cs` runs after files land in `DownloadImportService.ImportDownloadFilesAsync`. Rejects batches with ≥10 audio files whose median duration is <5 min. Language-half dropped per the prior decision (upstream's `SearchResultScorer.LanguageMismatchPenalty` covers it). |

Test counts: dotnet 740/740, vitest 363/363, FE `npm run build` green.

### Upstream PR branches — all rebased onto new canary

Cherry-picked from `kevin/live-rebased` onto fresh-cut-from-canary branches and force-pushed. (Don't `git rebase canary` directly — the new canary deleted 21 files the old branches touch.)

| PR | Branch | Commits | Status |
|---|---|---|---|
| [#580](https://github.com/Listenarrs/Listenarr/pull/580) | `fix/nzbget-xmlrpc-auth` | `48f89047` | Open, ready for merge. nzbget XML-RPC URL credentials. |
| [#585](https://github.com/Listenarrs/Listenarr/pull/585) | `feat/audiobooks-grouped-list-view` | `e965c309` | Open, drafted. List view for grouped author/series. |
| [#589](https://github.com/Listenarrs/Listenarr/pull/589) | `feat/audiobooks-collection-ready-count` | `f86e084b` + `0e4423c5` | Open, drafted. Stacked on `#585`. Got auto-closed by GitHub during the rebase when I accidentally pushed an empty branch tip — reopened with the correct content. |
| [#590](https://github.com/Listenarrs/Listenarr/pull/590) | `feat/audiobooks-view-mode-per-grouping` | `2918134d` + `9ca92997` | Open, drafted. Stacked on `#585`. |
| [#591](https://github.com/Listenarrs/Listenarr/pull/591) | `fix/automatic-search-relevance` | `fdb44cb8` + `c4792f89` | Open, drafted. Relevance filter + cat 3030. The original PR's separate "drop hardcoded category 3030" follow-up commit was *not* re-applied — the rebased form keeps the category restriction. If you decide to drop it again, amend the first commit before un-drafting. The PR title still mentions "language" — language was dropped per session decisions; consider editing the title to `fix(search): reject automatic-search results whose title is irrelevant to the audiobook`. |

#### Pacing comment posted

Per the maintainer's 2026-05-14 ask, I posted [a brief update on PR #590](https://github.com/Listenarrs/Listenarr/pull/590#issuecomment-4472747468) noting all five PRs are rebased and asking when the foundation phase is open enough to un-draft. No reply yet; wait before opening new PRs or un-drafting.

### Local-only branches (no open upstream PR)

These are based on old canary `6246e960` and have not been touched this session. They were never PR'd upstream — they exist as candidates for future PRs once foundation-phase pacing clears.

| Branch | Notes |
|---|---|
| `feat/add-series` | The "Add series" bulk-add flow. Already in `kevin/live` as `c3188a9c` etc. |
| `feat/library-dashboard` | 8 commits. Already in `kevin/live` as `cebf9291` through `2938a171`. |
| `feat/metadata-backfill` | The online metadata-backfill modal. Already in `kevin/live` as `b8746f02`. |
| `feat/pre-ingest-verification` | Original PreIngest commit (with language half). Now superseded by `c65dd0f9` (music-shape only) on `kevin/live`. |
| `feat/pass-a-local-metadata-enrichment` | [PR #583](https://github.com/Listenarrs/Listenarr/pull/583) was Kevin-closed 2026-05-14 (maintainer asked why a flag was needed; Kevin agreed and closed). The Pass A logic itself is live on `kevin/live` as `14cad72c`. Don't reopen as-is. |
| `feat/language-filter` | **Permanently dropped.** Upstream now covers this via `SearchResultScorer.LanguageMismatchPenalty` / `LanguageMissingPenalty`. Safe to delete the branch. |

If you want to rebase any of these to new canary for future PR submission, the cherry-pick-from-`kevin/live` approach worked well for the open PRs — same playbook.

## Known issues / follow-ups

1. **"2010 — Odyssey Two" by Arthur C. Clarke** returns only foreign editions in the backfill modal. (Carried over from previous sessions.) Audible US doesn't carry the English audiobook. Remediation paths: expose `?region=uk`, or extend candidate discovery to OpenLibrary print editions.
2. **Two integration tests dropped during chunk 1** in `tests/Features/Api/Services/AudioFileServiceTests.cs` still have TODOs. Unit promotion coverage is in `AudioFileService_PromotionTests.cs`. Worth re-porting the integration tests against the new `AudiobookFileService` primary-ctor shape — see also the new `AudioFileService_ForceMetadataRefreshTests.cs` for a working pattern.
3. **Two nullable-dereference warnings** in `listenarr.application/Audiobooks/AudiobookFileService.cs` lines 250 and 262 (pre-existing, not introduced by chunk 3). Worth cleaning up.
4. **CHANGELOG: the `[0.2.72]` heading on `kevin/live`** has no release date yet — that's normal for in-flight unreleased changes. Tag and date it when you next cut a release.
5. The original PR #591 had a follow-up commit that *removed* the category 3030 restriction (the rebased form keeps it). Decide which behaviour you want upstream and either amend or leave as-is.

## Operational notes

- **Deploy:** `./scripts/deploy-local.sh --skip-tests` (project root only — script enforces cwd). Verified twice this session; see updated `reference_listenarr_deploy.md` memory.
- **Verify before declaring FE clean:** run `npm run build`, not just `npx vue-tsc --noEmit`. Memory `feedback_browser_smoke_for_fe_deploys.md`.
- **Push targets:** `fork` only. Today's work pushed to `fork/kevin/live`, `fork/kevin/live-rebased`, and all five `fork/{PR branch}`.
- **Browser verification:** Kevin's job after every FE-bundle deploy. The two deploys this session passed HTTP smoke but await your incognito browser sanity-check.

## When in doubt

CLAUDE.md is the source of truth for branch strategy, deploy, and PR workflow. The previous `REBASE_TRIAGE.md` has been deleted — its job is done. Ask Kevin before opening upstream PRs while the pacing comment on #590 remains unanswered.
