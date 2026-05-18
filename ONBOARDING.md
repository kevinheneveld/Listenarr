# Listenarr session handoff — 2026-05-17 (post-rebase + feature-PR-drafting wave 1)

Kevin's fork of [Listenarrs/Listenarr](https://github.com/Listenarrs/Listenarr). See `CLAUDE.md` for the project guide.

The big multi-session rebase is done, deployed, and verified. Today's tail end shipped four small fixes triggered by browser-verification of the metadata-backfill modal, opened two upstream PRs from canary for the defensive ones, then started a **staggered wave of draft PRs** for the ~11 features that exist on `kevin/live` with no upstream visibility. Two of those drafts went out today; the rest are queued.

## Where things stand

### Live deploy

- **Live image:** `listenarr:local-20260517-1651` (head `a712b49a` on `kevin/live`).
- **Live URL:** https://your-host.example.
- **One-step rollback:** `listenarr:local-20260517-1634` (image-500 fix only, no z-index fix).
- **Two-step rollback:** `listenarr:local-20260517-1617` (no image-500 fix either).
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

### Branch state

| Branch | Tip | Notes |
|---|---|---|
| `canary` | `31b6c628` v0.4.1 | Clean mirror of upstream. |
| `kevin/live` | `a712b49a` | Deployed. **52 commits above canary.** Pushed to `fork/kevin/live`. |
| `kevin/live-rebased` | `a712b49a` | Identical to `kevin/live`. Pushed. Kept as a safety harbor; can be deleted once you're confident in the live deploy. |

### Upstream PRs (9 open, 1 closed)

Bumped from 5 → 9 since the rebase shipped.

| PR | Branch | Status | Notes |
|---|---|---|---|
| [#580](https://github.com/Listenarrs/Listenarr/pull/580) | `fix/nzbget-xmlrpc-auth` | Open, ready | nzbget XML-RPC URL credentials. Awaiting maintainer review. |
| [#585](https://github.com/Listenarrs/Listenarr/pull/585) | `feat/audiobooks-grouped-list-view` | **Draft** | List view for grouped author/series. Rebased onto new canary. |
| [#589](https://github.com/Listenarrs/Listenarr/pull/589) | `feat/audiobooks-collection-ready-count` | **Draft** | Stacked on #585. Rebased. |
| [#590](https://github.com/Listenarrs/Listenarr/pull/590) | `feat/audiobooks-view-mode-per-grouping` | **Draft** | Stacked on #585. Rebased. **Hosts the pacing comment thread** — maintainer's 2026-05-14 ask + my 2026-05-17 rebase-done update (no reply yet). |
| [#591](https://github.com/Listenarrs/Listenarr/pull/591) | `fix/automatic-search-relevance` | **Draft** | Relevance filter (+ auto-pick call-site fix). Category 3030 was dropped in a follow-up commit per Kevin's matching reasoning in the original. Title + body rewritten this session. |
| [#600](https://github.com/Listenarrs/Listenarr/pull/600) | `fix/images-recoverable-runtime-binder` | Open, ready | `ImagesController` swallows `RuntimeBinderException`. Pre-existing upstream bug surfaced during browser verification. |
| [#603](https://github.com/Listenarrs/Listenarr/pull/603) | `fix/modal-overlay-z-index` | Open, ready | `Modal` accepts optional `overlayZIndex` prop. No in-repo consumer in canary; framed in the PR body as defensive/additive. |
| [#604](https://github.com/Listenarrs/Listenarr/pull/604) | `fix/wwwroot-permissions` | **Draft** | Single Dockerfile chmod step. First of today's staggered draft wave. |
| [#605](https://github.com/Listenarrs/Listenarr/pull/605) | `fix/quality-profile-fallback` | **Draft** | Auto-search falls back to default quality profile + Wanted-view bulk-assign button. Second of today's staggered draft wave. |

| PR | Status | Notes |
|---|---|---|
| [#583](https://github.com/Listenarrs/Listenarr/pull/583) | **Closed 2026-05-14** | Pass A + opt-in rescan. Kevin closed after the maintainer asked "why opt-in?" Reasoning still holds; per session decision do not re-open as-is. |

**Pacing posture:** the maintainer's 2026-05-14 note on #590 said "you may not want to open too many at this time." Kevin's reply to today's #604/#605 question was "set them as draft until we hear it's OK to make them live." Note that #600 and #603 were opened earlier today as **non-draft** before that instruction crystallised — consider converting them to draft if you want full pacing consistency. (`gh pr ready --undo --repo Listenarrs/Listenarr 600` and likewise for 603.)

### kevin/live features still without an upstream PR (queue)

Wave 1 (today) shipped K + J as drafts (#604, #605). Eight more queued:

| Tier | # | Name | Commits on `kevin/live` | Size | Notes |
|---|---|---|---|---|---|
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

## Operational notes

- **Deploy:** `./scripts/deploy-local.sh --skip-tests` from repo root. Use `run_in_background: true`. Memory `reference_listenarr_deploy.md` is current (rewritten this session — the old "deploy-local.sh is wrong" note was stale).
- **Browser-verify after every FE-bundle deploy** — HTTP smoke passing is necessary but not sufficient. Hard-refresh in incognito. Memory `feedback_browser_smoke_for_fe_deploys.md`.
- **Push targets:** `fork` only. Never `origin`.
- **Pacing:** stagger draft PRs ~1-2/day across the queue above. Convert ready PRs back to draft if the maintainer raises pacing again.
- **Wave 1 schedule:** today shipped K (#604) + J (#605). Per Kevin: H + F tomorrow, B + I day +2, D + E day +3, A day +4, C day +5.

## When in doubt

`CLAUDE.md` is the source of truth for branch strategy, deploy, and PR workflow. Ask Kevin before opening upstream PRs while the pacing comment on #590 remains unanswered.
