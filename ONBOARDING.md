# Listenarr session handoff — 2026-05-25 (Find Duplicates title/author tab + branch cleanup)

Kevin's fork of [Listenarrs/Listenarr](https://github.com/Listenarrs/Listenarr). See `CLAUDE.md` for the project guide.

This session shipped two fixes + one feature to `kevin/live` and cleaned out the
stale local feat/* + fix/* branch graveyard (their content had already been
integrated; the branch labels were leftovers). The fork remote branches that
anchor open upstream PRs were left untouched.

## Where things stand

### Live deploy

- **Live image:** `listenarr:local-20260525-…` (head `885d7b18` on `kevin/live`,
  pushed to `fork/kevin/live`).
- **Live URL:** https://your-host.example.
- This session's commits on top of the prior live tip (`da5d2cf2`):

  | Tag | Head | What it added |
  |---|---|---|
  | this session | `c38d964e` | `fix(library)`: skip rows with empty `BasePath` AND zero files from the organize-library preview, so the default "select all" → Apply doesn't queue per-book moves that would all fail with "Source path does not exist." |
  | this session | `cc9c1ca5` (merge `e7c070a3`) | `fix/pattern-preview-padding` merged in — the FE pattern-preview regex now matches `\{Key:(0+)\}` and pads to the captured width, so `{Title}-{DiskNumber:000}` previews as `Title-001/002/003` instead of rendering the literal token. Backend renderer was always correct; this was preview-only. |
  | this session | `885d7b18` | `feat(library)`: title/author collision pass in Find Duplicates. `GET /library/duplicates` now runs a second detection pass that groups rows by computed canonical folder target via `FolderNamingPattern` — catches edition variants and wrong-metadata rows with distinct ASINs the same-ASIN dedup pass can't see. `DuplicatesReviewModal` grows three tabs (**By ASIN / By title/author / All**). The title/author tab is Skip-only (the merge endpoint already rejects cross-ASIN attempts; cleanup is "open the row → delete from audiobook detail page"). New DTO discriminator `Kind` + `CollisionKey` field. 4 new tests; 824/824 backend pass. |

### Branch state

| Branch | Tip | Notes |
|---|---|---|
| `canary` | `02a029b2` v0.4.2 | Clean mirror of upstream. No advancement this session. |
| `kevin/live` | `885d7b18` | Deployed. Pushed to `fork/kevin/live`. |
| `pr-553` | (Robbie's branch) | Upstream-maintainer review checkout. Not ours. Left in place. |

All local `feat/*` and `fix/*` branches were deleted this session after verifying
by **content presence on kevin/live** (grep for distinctive symbols, not `git
cherry` which lies after squash/rebase) that every one's work is integrated.
`fork/*` remote branches are untouched — they still anchor the open upstream PRs.

### Upstream PRs (9 open, 1 closed) — unchanged from prior session

| PR | Branch | Status | Notes |
|---|---|---|---|
| [#580](https://github.com/Listenarrs/Listenarr/pull/580) | `fix/nzbget-xmlrpc-auth` | **Draft** | NZBGet safe-redirect handler. Awaiting follow-up review. |
| [#585](https://github.com/Listenarrs/Listenarr/pull/585) | `feat/audiobooks-grouped-list-view` | **Draft** | List view for grouped author/series (base of stack). |
| [#589](https://github.com/Listenarrs/Listenarr/pull/589) | `feat/audiobooks-collection-ready-count` | **Draft** | Stacked on #585. |
| [#590](https://github.com/Listenarrs/Listenarr/pull/590) | `feat/audiobooks-view-mode-per-grouping` | **Draft** | Stacked on #585. **Hosts the maintainer's pacing comment thread**. |
| [#591](https://github.com/Listenarrs/Listenarr/pull/591) | `fix/automatic-search-relevance` | **Draft** | Relevance filter (+ auto-pick call-site fix). |
| [#600](https://github.com/Listenarrs/Listenarr/pull/600) | `fix/images-recoverable-runtime-binder` | **Draft** | `ImagesController` swallows `RuntimeBinderException`. |
| [#603](https://github.com/Listenarrs/Listenarr/pull/603) | `fix/modal-overlay-z-index` | **Draft** | `Modal.overlayZIndex` prop. |
| [#604](https://github.com/Listenarrs/Listenarr/pull/604) | `fix/wwwroot-permissions` | **Draft** | Dockerfile chmod step. |
| [#605](https://github.com/Listenarrs/Listenarr/pull/605) | `fix/quality-profile-fallback` | **Draft** | Auto-search default-quality fallback + Wanted-view bulk-assign button. |

| PR | Status | Notes |
|---|---|---|
| [#583](https://github.com/Listenarrs/Listenarr/pull/583) | **Closed** | Pass A + opt-in rescan. Per session decision do not re-open as-is. |

**Pacing posture:** maintainer's 2026-05-14 note on #590 said "you may not want
to open too many at this time." All 9 open PRs remain draft. Never open a PR
upstream as non-draft (or open new PRs) without explicit clearance from Kevin —
memory `feedback_listenarr_upstream_pr_pacing.md`.

### kevin/live-only work without an upstream PR

A meaningful queue of features has shipped to `kevin/live` and never been
PR'd upstream, including (incomplete list):

- Library metrics dashboard (`/dashboard` + backend stats endpoint, ApexCharts,
  drill-down filters, auto-cache series catalogs)
- Online metadata-backfill modal (the whole feature, including the paste-an-
  Audible-URL escape hatch and the in-browser audio preview)
- Add-series bulk action on the Add New page
- Cache external cover art locally (on-save hook + admin sweep)
- Per-file delete + rename on the audiobook detail page
- Pre-ingest verification (music-shape rejection)
- Per-ASIN add-race serialization (`AudiobookAddLockManager`)
- TitleMatcher / SignificantTokens (punctuation tolerance)
- Library filters preserved in URL + on detail-page back/delete
- Audiobook files natural-sort
- AUTHOR_TITLE backfill search fallback + exact-match collapse
- Per-file delete / rename on the audiobook detail page
- **Organize library folders** (preview + apply, `GET/POST /library/organize/*`,
  `OrganizeLibraryModal`, including this session's fileless-row skip fix)
- **Find duplicate audiobooks** including this session's title/author pass

The local branches that previously anchored these for upstreaming have been
deleted now that the content lives on `kevin/live`. When the pacing window
opens for a new PR, cut a fresh branch from the latest `canary` (or from
whichever feature branch the new work stacks on) and stage the diff there.

### Open issues (5)

| # | Title | State |
|---|---|---|
| [#1](https://github.com/kevinheneveld/Listenarr/issues/1) | List view missing for grouped authors/series | Closed by PR #585 (pending merge) |
| [#2](https://github.com/kevinheneveld/Listenarr/issues/2) | Ready / total book count with quality-color cue | Closed by PR #589 (pending merge) |
| [#3](https://github.com/kevinheneveld/Listenarr/issues/3) | Persist list/grid view mode per grouping | Closed by PR #590 (pending merge) |
| [#4](https://github.com/kevinheneveld/Listenarr/issues/4) | Automatic search matches music albums | Closed by PR #591 (pending merge) |
| [#5](https://github.com/kevinheneveld/Listenarr/issues/5) | **Add LibriVox as a metadata source** (deferred) | Open. Tier-1 backfill-only scope sketched in the issue body. |

### Known issues / follow-ups not yet ticketed

1. **"2010 — Odyssey Two" by Arthur C. Clarke** still returns only foreign
   editions in the backfill modal. Audible US doesn't carry the English
   audiobook. Remediations: expose `?region=uk` in the modal, or extend
   candidate discovery to OpenLibrary print editions.
2. **Two integration tests dropped during chunk 1** in
   `tests/Features/Api/Services/AudioFileServiceTests.cs` still have TODO
   comments. Unit promotion coverage is in `AudioFileService_PromotionTests.cs`
   and `AudioFileService_ForceMetadataRefreshTests.cs`. Worth re-porting against
   the new ctor shape.
3. **Two nullable-dereference warnings** in
   `listenarr.application/Audiobooks/AudiobookFileService.cs` lines 250 and 262
   (pre-existing).
4. **`[0.2.72]` CHANGELOG heading on `kevin/live`** has no release date — normal
   for in-flight unreleased changes. Date it when the next release is cut.
5. **`StartupConfigServiceTests.SaveAsync_PreservesAuthenticationRequired` is
   flaky** in the deploy script's parallel-test run. Real fix: have the test
   create its own unique temp cache dir per `Guid.NewGuid()`.
6. **Title/author dedup tab is read-only (Skip + open-in-new-tab).** Write
   actions for title/author groups (e.g. discard-without-merge) would need a
   new endpoint that doesn't require shared ASIN — out of scope for this
   session, candidate for a follow-up if the manual workflow proves too slow.

## Operational notes

- **Deploy:** `./scripts/deploy-local.sh --skip-tests` from repo root. Use
  `run_in_background: true`. Memory `reference_listenarr_deploy.md` is current.
- **Browser-verify after every FE-bundle deploy** — HTTP smoke passing is
  necessary but not sufficient. Hard-refresh in incognito. Memory
  `feedback_browser_smoke_for_fe_deploys.md`.
- **Push targets:** `fork` only. Never `origin`.
- **Pacing:** all open PRs are draft. Don't open more until maintainer signals
  the queue can advance.
- **Local pre-commit guard at `.git/hooks/pre-commit`** — added 2026-05-22
  after the `.deploy-local.env` leak. Three layers: staged-file `.gitignore`
  match; name blocklist for deploy/secret files; content patterns for the
  media host's private IP, SSH-alias env vars, the author's local hostname.
  Not version-controlled — re-install on fresh clones.

## When in doubt

`CLAUDE.md` is the source of truth for branch strategy, deploy, and PR
workflow. Ask Kevin before opening upstream PRs while the pacing comment on
#590 remains unanswered.
