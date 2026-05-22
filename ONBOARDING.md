# Listenarr session handoff — 2026-05-19 (per-file rename — first half of move/rename)

Kevin's fork of [Listenarrs/Listenarr](https://github.com/Listenarrs/Listenarr). See `CLAUDE.md` for the project guide.

The big multi-session rebase is done, deployed, and verified. This session cut the first of two move/rename feature PRs from canary — **per-file rename** on the audiobook detail page's Files tab. Branch `feat/per-file-rename` was merged into `kevin/live` and deployed as `listenarr:local-20260519-1108`. HTTP smoke passes; **browser verification still owed** before declaring the deploy done. The companion PR (one-step audiobook rename via Edit Audiobook modal) is the next chunk of move/rename and has not been started.

## Where things stand

### Live deploy

- **Live image:** `listenarr:local-20260519-1108` (head `c201ddd2` on `kevin/live`, post-rebase onto v0.4.2 canary + per-file-rename merge).
- **Live URL:** https://your-host.example.
- **One-step rollback:** `listenarr:local-20260519-0817` (pre-rename tip — post-rebase but without the pencil action on the Files tab).
- **Two-step rollback:** `listenarr:local-20260519-0720` (pre-rebase tree on v0.4.1 canary base; same FE/BE behaviour as `0817` but on the older canary).
- Deploy chain since the rebase shipped:

  | Tag | Head | What it added |
  |---|---|---|
  | `local-20260517-0939` | `c65dd0f9` | Chunk-3 ports landed (45 commits over canary). |
  | `local-20260517-0958` | `07afc34c` | Auto-pick call-site fix for `RelevanceFilter` in `DownloadService` + `LibraryController`. |
  | `local-20260517-1510` | `8ce8fbfa` | "Preview my file" button in metadata-backfill modal. |
  | `local-20260517-1519` | `98e846ae` | Publish-date diff normalization. |
  | `local-20260517-1617` | `fc755d07` | Pass hydrated audiobook to backfill modal so `files[0]` reaches the preview button. |
  | `local-20260517-1634` | `50d8b4b6` | `ImagesController` swallows `RuntimeBinderException`. |
  | `local-20260517-1651` | `a712b49a` | `Modal.overlayZIndex` prop so `FilePreviewModal` stacks above the backfill overlay. |
  | `local-20260518-0750` | `40a436bc` | NZBGet safe-redirect handler (supersedes URL-embedded-creds approach from `0fad52b9`). PR #580 rewritten to match. |
  | `local-20260518-1239` | `07d59881` | **Incomplete** natural-sort fix — landed in `AudiobookDtoFactory` but `LibraryController.GetAudiobook` constructs its own anonymous response and was missed. FE detail view still showed scan-time order. |
  | `local-20260518-1306` | `fef94958` | Complete natural-sort fix — new `AudiobookFileOrdering.InNaturalOrder` helper applied to **both** `LibraryController.GetAudiobook` and `AudiobookDtoFactory`. Controller-level test added. |
  | `local-20260518-1430` | `301a645f` | **v1** search-fallback for the AUTHOR_TITLE branch — fired only when narrow returned zero. Fixed Gwendy's Button Box (zero narrow → fallback) but missed Robots and Empire (one narrow Spanish match → no fallback, English edition still hidden). |
  | `local-20260518-1653` | `2522760c` | **v2** search-fallback — supplements narrow with title-only Audible search when narrow < 5 candidates, merging deduped by ASIN. Covers both Gwendy and Robots cases without diluting healthy-result queries like "Foundation" by Asimov. PR branch force-pushed. |
  | `local-20260518-1730` | `84d63d48` | Paste-an-Audible-URL escape hatch in backfill modal — when search can't reach a book (e.g. B0CSV7NJMB exists at `/metadata/B0CSV7NJMB` but is invisible to keyword / title / author-page search), the user can paste the URL or bare ASIN. Regex extracts the 10-char ASIN and routes through the existing `pickCandidate(asin)` flow. Kevin/live-only since the modal isn't on canary yet — will bundle with feature C upstream PR. |
  | `local-20260518-1756` | `60f87596` | Exact-match collapse for the backfill candidate search — when any merged candidate's normalized title equals the user's title AND the author overlaps, return only the exact matches (preserving multiple narrators / abridgements). Above the threshold or when no candidate exactly matches, return the full merged list unchanged. Second commit on `fix/backfill-search-fallback-to-title`; both upstream-bound. |
  | `local-20260519-0720` | `219703e1` (merge of `23430c7c`) | Per-file delete on the audiobook detail page — trash button per Files-tab row + confirm modal with "Also delete the file from disk" checkbox. Backend `DELETE /library/{id}/files/{fileId}?deleteFromDisk={bool}`; disk-delete failures non-fatal (DB row removed, warning surfaced); writes a "File Removed" history entry. 6 service + 4 controller tests. Cut from canary on `feat/per-file-delete`; merged into kevin/live. |
  | `local-20260519-0817` | `b5818ba9` (post-rebase) | **Rebase deploy** — kevin/live rebased onto upstream canary v0.4.2 (`02a029b2`, brings in #576 foundational refactor + ~18 stabilization fixes). All 12 PR / queued branches rebased onto the new canary cleanly. Tests 769/769 green. Same FE/BE behaviour as `0720`; this deploy is to verify the rebased base runs cleanly before opening any new PRs or starting move/rename work. |
  | `local-20260519-1108` | `c201ddd2` (merge of `800657df`) | **Per-file rename** on the audiobook detail page — pencil action on each Files-tab row opens a single-file rename modal. Modal calls `POST /api/v1/library/{id}/rename/preview` to get the authoritative current path (so it works whether `f.Path` is stored relative or absolute), lets the user edit only the filename portion (extension preserved, path separators rejected), then issues a single-file `FileRenameOperation` through `POST /api/v1/library/{id}/rename`. No backend changes — `RenameService` already accepted arbitrary `NewPath` in per-file operations; the controller already exposed the per-audiobook endpoint. Cut from canary on `feat/per-file-rename`; merged into kevin/live. 5 FE tests added. Deployed with `--skip-tests` (FE-only diff; backend untouched). **Browser-verify pending.** |

### Branch state

| Branch | Tip | Notes |
|---|---|---|
| `canary` | `02a029b2` v0.4.2 | Clean mirror of upstream. **Advanced 18 commits this session** — the foundational refactor (#576) and the post-refactor stabilization fixes are now in. |
| `kevin/live` | `c201ddd2` (per-file-rename merge) | Deployed. **67 commits above canary** (preserves the per-file-delete and per-file-rename merge structure via `--rebase-merges`). Pushed to `fork/kevin/live`. |
| `kevin/live-rebased` | `c201ddd2` | Kept in lockstep with `kevin/live` (same commit). The "rebased" distinction is no longer maintained — both branches always point at the same commit. |
| `fix/audiobook-files-natural-sort` | `9b910948` | **Rebased onto v0.4.2 canary**, pushed. Ready to `gh pr create --draft`. |
| `fix/backfill-search-fallback-to-title` | `11bbe264` | **Rebased onto v0.4.2 canary**, pushed. Two commits: broadening + exact-match collapse. Ready to `gh pr create --draft`. |
| `feat/per-file-delete` | `a1ed8570` | **Rebased onto v0.4.2 canary**, pushed. Single commit. Ready to `gh pr create --draft`. |
| `feat/per-file-rename` | `800657df` | **Cut fresh from v0.4.2 canary this session**, pushed. Single commit, FE-only. Ready to `gh pr create --draft` once the pacing window opens. |

**Post-rebase note (this session):** the entire branch tree was rebased onto upstream canary v0.4.2 (`02a029b2`) at the end of this session, after the foundational refactor (#576) merged upstream. All 12 PR branches and `kevin/live` itself were rebased cleanly. The 9 open upstream PRs need a `git push fork --force-with-lease` if GitHub hasn't auto-detected the rebase yet — done in this session, see commits at the rebased tips below.

### Upstream PRs (9 open, 1 closed)

Bumped from 5 → 9 since the rebase shipped.

| PR | Branch | Status | Notes |
|---|---|---|---|
| [#580](https://github.com/Listenarrs/Listenarr/pull/580) | `fix/nzbget-xmlrpc-auth` | **Draft** | NZBGet safe-redirect handler — rewritten and force-pushed this session after T4g1's review pointed out (a) URL-embedded creds would forward across cross-host redirects, defeating the protection rather than implementing it, and (b) `FetchDownloadsAsync`'s `/jsonrpc` call had the same gap. New approach: `AllowAutoRedirect=false` + `NzbgetSafeRedirectHandler` that re-applies the Authorization header only on same-host non-downgrade hops, throws a typed `NzbgetSafeRedirectException` with descriptive message on rule violation. Awaiting follow-up review. |
| [#585](https://github.com/Listenarrs/Listenarr/pull/585) | `feat/audiobooks-grouped-list-view` | **Draft** | List view for grouped author/series. Rebased onto new canary. |
| [#589](https://github.com/Listenarrs/Listenarr/pull/589) | `feat/audiobooks-collection-ready-count` | **Draft** | Stacked on #585. Rebased. |
| [#590](https://github.com/Listenarrs/Listenarr/pull/590) | `feat/audiobooks-view-mode-per-grouping` | **Draft** | Stacked on #585. Rebased. **Hosts the pacing comment thread** — maintainer's 2026-05-14 ask + my 2026-05-17 rebase-done update (no reply yet). |
| [#591](https://github.com/Listenarrs/Listenarr/pull/591) | `fix/automatic-search-relevance` | **Draft** | Relevance filter (+ auto-pick call-site fix). Category 3030 was dropped in a follow-up commit per Kevin's matching reasoning in the original. Title + body rewritten this session. |
| [#600](https://github.com/Listenarrs/Listenarr/pull/600) | `fix/images-recoverable-runtime-binder` | **Draft** | `ImagesController` swallows `RuntimeBinderException`. Pre-existing upstream bug surfaced during browser verification. Converted to draft this session for pacing consistency. |
| [#603](https://github.com/Listenarrs/Listenarr/pull/603) | `fix/modal-overlay-z-index` | **Draft** | `Modal` accepts optional `overlayZIndex` prop. No in-repo consumer in canary; framed in the PR body as defensive/additive. Converted to draft this session for pacing consistency. |
| [#604](https://github.com/Listenarrs/Listenarr/pull/604) | `fix/wwwroot-permissions` | **Draft** | Single Dockerfile chmod step. First of today's staggered draft wave. |
| [#605](https://github.com/Listenarrs/Listenarr/pull/605) | `fix/quality-profile-fallback` | **Draft** | Auto-search falls back to default quality profile + Wanted-view bulk-assign button. Second of today's staggered draft wave. |

| PR | Status | Notes |
|---|---|---|
| [#583](https://github.com/Listenarrs/Listenarr/pull/583) | **Closed 2026-05-14** | Pass A + opt-in rescan. Kevin closed after the maintainer asked "why opt-in?" Reasoning still holds; per session decision do not re-open as-is. |

**Pacing posture:** the maintainer's 2026-05-14 note on #590 said "you may not want to open too many at this time." Kevin's instruction is "set them as draft until we hear it's OK to make them live." All 9 open PRs are draft. Going forward: never open a PR upstream as non-draft without explicit clearance from Kevin.

**Post-rebase status (this session, 2026-05-19):** the foundational refactor (PR #576 "Download importation improvements + Clarification of project structure") merged upstream on 2026-05-15, followed by ~18 commits of T4g1's stabilization fixes through 2026-05-17. Canary is now at `v0.4.2` (`02a029b2`). All 12 of Kevin's PR / queued branches were rebased onto this new canary cleanly:

| Branch | New tip (post-rebase) | Status |
|---|---|---|
| `fix/wwwroot-permissions` | `cac533e3` | open PR #604, draft |
| `fix/quality-profile-fallback` | `6ca26baf` | open PR #605, draft |
| `fix/modal-overlay-z-index` | `6ba19474` | open PR #603, draft |
| `fix/images-recoverable-runtime-binder` | `c4596688` | open PR #600, draft |
| `fix/nzbget-xmlrpc-auth` | `88309552` | open PR #580, draft |
| `fix/automatic-search-relevance` | `60b14cbe` | open PR #591, draft |
| `feat/audiobooks-grouped-list-view` | `c365ece7` | open PR #585, draft (base of stack) |
| `feat/audiobooks-collection-ready-count` | `3492def8` | open PR #589, draft (stacked on #585) |
| `feat/audiobooks-view-mode-per-grouping` | `e9fd9f61` | open PR #590, draft (stacked on #585) |
| `fix/audiobook-files-natural-sort` | `9b910948` | queued, no PR yet |
| `fix/backfill-search-fallback-to-title` | `11bbe264` | queued, no PR yet |
| `feat/per-file-delete` | `a1ed8570` | queued, no PR yet |
| `feat/per-file-rename` | `800657df` | queued, no PR yet — **cut fresh from v0.4.2 canary this session (2026-05-19), not part of the original rebase batch** |

`kevin/live` was rebased with `--rebase-merges` to preserve the per-file-delete (and now per-file-rename) merge structure; tests 769/769 green on the rebased tip, 353/353 FE tests green after adding per-file-rename. **Watch for the green light to open the queue:** maintainer reply on #590 (no response yet to Kevin's 2026-05-17 rebase-done update), a draft review, or ~7 days of canary quiet after the last `[fix]` commit on 2026-05-17.

### kevin/live features still without an upstream PR (queue)

Wave 1 (2026-05-17) shipped K + J as drafts (#604, #605). Eight more queued, plus three branches already pushed to fork that just need `gh pr create --draft` (the third — `feat/per-file-rename` — was cut this session):

| Tier | # | Name | Branch / commits on `kevin/live` | Size | Notes |
|---|---|---|---|---|---|
| 2 (next session) | NS | Audiobook files natural-sort | `fix/audiobook-files-natural-sort` (`52aa1cf5`) — pushed; on `kevin/live` as `fef94958` | Small | Single shared `AudiobookFileOrdering.InNaturalOrder` helper applied at both `LibraryController.GetAudiobook` and `AudiobookDtoFactory`. Controller-level test exercising the actual API endpoint. Ready to `gh pr create --draft`. |
| 2 (next session) | SF | Backfill search fallback + exact-match collapse | `fix/backfill-search-fallback-to-title` (`d67df4b9`) — two commits, pushed | Small/Medium | AUTHOR_TITLE branch in `IntelligentSearchAsync` (1) supplements with title-only Audible search when narrow returns < 5 candidates, merging deduped by ASIN, and (2) collapses the merged list to exact title+author matches when any exist (multi-narrator keeps all matches; no exact match → return full merged list). Six-test regression suite. Ready to `gh pr create --draft`. |
| 2 (next session) | PFD | Per-file delete on audiobook detail page | `feat/per-file-delete` (`23430c7c`) — pushed; on `kevin/live` as `23430c7c` (merge `219703e1`) | Small | Trash button per Files-tab row + confirm modal with "Also delete the file from disk" checkbox (default on). Backend `DELETE /library/{id}/files/{fileId}?deleteFromDisk={bool}`; disk-delete failures non-fatal; writes "File Removed" history entry. 6 service + 4 controller tests. Ready to `gh pr create --draft`. |
| 2 (next session) | PFR | Per-file rename on audiobook detail page | `feat/per-file-rename` (`800657df`) — pushed; on `kevin/live` as merge `c201ddd2` | Small | Pencil action per Files-tab row → modal that previews via `POST /library/{id}/rename/preview`, lets the user edit only the filename portion, then submits one `FileRenameOperation` through `POST /library/{id}/rename`. FE-only — backend already accepted arbitrary `NewPath` in per-file ops. 5 FE tests. Pairs with PFD on the same row UI. Ready to `gh pr create --draft`. |
| 2 (next session) | H | Pre-ingest verification (music-shape) | `c65dd0f9` | Small | Single new file in `listenarr.application/Downloads/`. Defensive import-time check. |
| 2 (next session) | F | Auto-cache Audible series catalogs | `b4370cdd` | Small | Single new background service + 1-method interface addition. |
| 3 | B | In-browser audio preview | `46085a4d` + `d5e9a886` | Medium | New `FilePreviewModal` + streaming endpoint. **The feature today's modal-stacking saga was built around** — well exercised, ready to PR. |
| 3 | I | TitleMatcher (punctuation tolerance) | `5f9244ca` | Small | Depends on `SignificantTokens` from #591 — either land #591 first or include a self-contained copy of `SignificantTokens` in the PR. |
| 4 | D | Add-series bulk action | `c3188a9c` + `16f2dbbc` + `058da9cf` | Medium | New `AddSeriesModal` on the Add New page. Reuses existing add endpoint, no new backend. |
| 4 | E | Cache external cover art locally | `35e513d4` + `7fd1e488` + `a554769d` + `767cc326` | Medium | Two-half feature: on-save hook + admin sweep. Consider splitting into 2 PRs. |
| 5 | A | Library metrics dashboard | `cebf9291` → `6445dbb6` (7 feature commits + 4 docs) | **Large** | New `/dashboard` route, new backend endpoint, ApexCharts lazy-loaded. |
| 5 | C | Online metadata-backfill modal (the whole feature) | `b8746f02` + all the today-and-prior fixes | **Largest** | Touches FE heavily. Bundle today's fix commits (`fc755d07`, `98e846ae`, `8ce8fbfa`, `19ec433a`, `8eece9a8`, `6438c231`, `95ce9803`, `f7176493`, `57651939`) into the PR series. |

### Open issues (5)

| # | Title | State |
|---|---|---|
| [#1](https://github.com/kevinheneveld/Listenarr/issues/1) | List view missing for grouped authors/series | Closed by PR #585 (pending merge) |
| [#2](https://github.com/kevinheneveld/Listenarr/issues/2) | Ready / total book count with quality-color cue | Closed by PR #589 (pending merge) |
| [#3](https://github.com/kevinheneveld/Listenarr/issues/3) | Persist list/grid view mode per grouping | Closed by PR #590 (pending merge) |
| [#4](https://github.com/kevinheneveld/Listenarr/issues/4) | Automatic search matches music albums | Closed by PR #591 (pending merge) |
| [#5](https://github.com/kevinheneveld/Listenarr/issues/5) | **Add LibriVox as a metadata source** (deferred) | Open. Filed this session. Tier-1 backfill-only scope sketched in the issue body. |

### Known issues / follow-ups not yet ticketed

1. **"2010 — Odyssey Two" by Arthur C. Clarke** still returns only foreign editions in the backfill modal. Audible US doesn't carry the English audiobook. Remediations: expose `?region=uk` in the modal, or extend candidate discovery to OpenLibrary print editions. (Carried from previous sessions.)
2. **Two integration tests dropped during chunk 1** in `tests/Features/Api/Services/AudioFileServiceTests.cs` still have TODO comments. Unit promotion coverage is in `AudioFileService_PromotionTests.cs` and the new `AudioFileService_ForceMetadataRefreshTests.cs`. Worth re-porting against the new ctor shape.
3. **Two nullable-dereference warnings** in `listenarr.application/Audiobooks/AudiobookFileService.cs` lines 250 and 262 (pre-existing, not introduced by chunk 3).
4. **`[0.2.72]` CHANGELOG heading on `kevin/live`** has no release date — that's normal for in-flight unreleased changes. Tag and date it when you next cut a release.
5. **`StartupConfigServiceTests.SaveAsync_PreservesAuthenticationRequired` is flaky in the deploy script's parallel-test run.** Hits `IOException: Directory not empty` cleaning up `tests/bin/Debug/net10.0/config/cache/images` — a teardown race when another test writes to that path mid-`RemoveDirectoryRecursive`. Passes in isolation. Workaround when the deploy aborts: re-run with `--skip-tests` (already-vetted commits). Real fix: have the test create its own unique temp cache dir per `Guid.NewGuid()` instead of sharing `tests/bin/.../config/cache/images`.

## Operational notes

- **Deploy:** `./scripts/deploy-local.sh --skip-tests` from repo root. Use `run_in_background: true`. Memory `reference_listenarr_deploy.md` is current (rewritten this session — the old "deploy-local.sh is wrong" note was stale).
- **Browser-verify after every FE-bundle deploy** — HTTP smoke passing is necessary but not sufficient. Hard-refresh in incognito. Memory `feedback_browser_smoke_for_fe_deploys.md`.
- **Push targets:** `fork` only. Never `origin`.
- **Pacing:** stagger draft PRs ~1-2/day across the queue above. Convert ready PRs back to draft if the maintainer raises pacing again.
- **Wave 1 schedule:** today shipped K (#604) + J (#605). Per Kevin: H + F tomorrow, B + I day +2, D + E day +3, A day +4, C day +5.
- **Local pre-commit guard at `.git/hooks/pre-commit`** (added 2026-05-22 after the `.deploy-local.env` leak in b2080af5). Three layers: any staged file `.gitignore` matches; a name blocklist for deploy/secret files; content patterns for the media host's private IP, SSH-alias env vars with non-empty values, and the author's local hostname. Read the hook itself for the exact patterns. Not version-controlled — re-install on fresh clones. Root cause was a feature branch predating the gitignore line; the name + content layers catch that case even when `.gitignore` on the branch is silent.

## When in doubt

`CLAUDE.md` is the source of truth for branch strategy, deploy, and PR workflow. Ask Kevin before opening upstream PRs while the pacing comment on #590 remains unanswered.
