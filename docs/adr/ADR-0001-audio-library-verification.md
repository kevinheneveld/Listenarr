# ADR-0001: Audio-Based Library Verification (Speech-to-Text Identity Check)

**Status:** Proposed
**Date:** 2026-06-10
**Deciders:** Repo maintainers

## Context

Early builds of Listenarr searched for and imported content with weak vetting
guardrails. The result is a library containing a meaningful number of
mis-identified entries — wrong editions, music-scene releases grabbed for
audiobooks, substring/title-collision false matches, and orphaned grabs that
were never the book they claim to be. The existing text-based defenses
(`AudiobookOnlyFilter`, `RelevanceFilter` author-corroboration, `NormalizeTitle`)
operate on *release names and external metadata*, not on the audio itself, so a
release that is named correctly but contains the wrong content slips through.

The audio is the ground truth. Most commercial audiobooks open with spoken
credits in the first 10–30 seconds — *"[Title], by [Author], narrated by
[Narrator]. [Publisher]."* — and frequently repeat them at the end. Transcribing
that window and comparing it to the stored metadata is a high-signal way to
catch mis-identifications that text-only checks cannot.

We want this as (a) a **batch tool to walk and triage the existing library**
and (b) a **hook in the ingestion pipeline** for new grabs. We also want
durable, auditable **verification state** that distinguishes books a human has
confirmed from books an agent has confirmed.

### Constraints / forces

- **Existing FFmpeg infra.** `FfmpegService` already downloads a full static
  `ffmpeg`/`ffprobe` build per-arch and runs `ffprobe`. Audio extraction is
  effectively free to add.
- **Existing library-walker pattern.** `UnmatchedScanBackgroundService` already
  enumerates audio files, groups by stem, and streams progress over SignalR — a
  near-template for the batch verifier.
- **No plugin system exists.** The "plugin" framing resolves to a native
  background service + an ingestion hook, not a loadable module.
- **No existing LLM integration** anywhere in the codebase.
- **Privacy posture.** Listenarr is self-hosted; a user's library audio should
  not be sent to third parties without explicit opt-in. (See decision on
  locality below.)
- **Clean architecture layering.** Domain → Application → Infrastructure → Api,
  with EF Core migrations under `listenarr.infrastructure/Persistence/Migrations`.

### Decisions locked with stakeholder (2026-06-10)

1. **LLM tier:** designed-in via a clean provider boundary, but **off by
   default**. Phase 1 ships deterministic-only.
2. **Agent autonomy:** **flag only**. The agent writes a verdict and flags;
   every corrective action (delete / re-search / re-identify) stays manual
   through existing UI for now.
3. **Locality:** **local STT always**; the optional LLM tier *may* call a cloud
   provider (Claude API) when explicitly enabled. Audio never leaves the host;
   only short text transcript snippets do, and only when the cloud LLM tier is
   turned on.

## Decision

Introduce an **Audio Verification** subsystem:

1. **Audio extraction** — reuse FFmpeg to clip the first ~60s (and optionally the
   last ~30s) of a book's primary file to 16 kHz mono WAV.
2. **Local STT** — transcribe the clip with a bundled `whisper.cpp` binary +
   `ggml` model, invoked by shelling out (same *call* pattern as FFmpeg). No
   Python sidecar, no network at transcription time. **Packaging differs from
   FFmpeg, deliberately:** whisper.cpp is MIT and typically built from source
   rather than distributed as clean per-arch static builds (FFmpeg's
   runtime-download exists largely for licensing/size reasons that don't apply
   here). The default packaging is therefore **bake the binary + `ggml-base.en`
   model into the Dockerfile at build time**, sidestepping the per-arch
   runtime-download machinery. Runtime download is a fallback only if a reliable
   prebuilt source for the target arch is confirmed.
3. **Verdict engine** behind an `IIdentityVerifier` interface with two tiers:
   - **Tier 1 — Deterministic (default, offline).** Match transcript tokens
     against the book's stored title/author/narrator/publisher → **per-field**
     match results (not a single blob), each with its own score, plus an
     aggregate verdict. Title words use exact-ish/normalized matching
     (`NormalizeTitle`); **author/narrator names use phonetic + edit-distance**
     matching, because STT mangles proper nouns far more than common words.
     Fields are weighted by transcription reliability.
   - **Tier 2 — LLM (opt-in, pluggable).** When Tier 1 is `Uncertain` and the
     LLM tier is enabled, send the transcript + stored metadata to a configured
     provider (local Ollama **or** Claude API) for a structured verdict. Off by
     default; provider abstracted behind `IVerificationLlmProvider`.
4. **Verdict** = `Match` / `Mismatch` / `Uncertain` + confidence + extracted
   fields + transcript snippet, persisted for audit.
5. **Verification state** on the `Audiobook` entity, distinguishing manual vs
   agentic verification, with manual outranking agentic.
6. **Two entry points** sharing one verifier: a `LibraryVerificationBackgroundService`
   batch walker (Phase 1) and an ingestion-completion hook (Phase 2).

The agent **flags only**. No automated deletion or re-search in this ADR's scope.

## Options Considered

### Decision 1 — STT engine

#### Option A: `whisper.cpp` binary, shelled out *(chosen)*
| Dimension | Assessment |
|-----------|------------|
| Complexity | Low–Med — shell-out call pattern mirrors FFmpeg; packaging differs |
| Cost | Free; CPU-only, ~1–3s per clip with `base.en` |
| Privacy | Fully local, no network at inference |
| Team familiarity | High for the *invocation*; packaging is build-time bake |

**Pros:** Self-contained; no Python runtime; CPU-only is fine for 60s clips.
**Cons:** Another binary + model (~140 MB for `base.en`) to ship and version.
**Packaging note (corrects the naive "mirror FFmpeg" assumption):** FFmpeg uses
runtime per-arch download because of johnvansickle's static-build ecosystem and
FFmpeg's licensing/size. whisper.cpp is MIT and primarily build-from-source, so
the precedent's *reasoning* doesn't transfer — **bake it into the Dockerfile at
build time** unless a reliable prebuilt binary for the Docker target arch is
confirmed. This must be resolved before writing the Phase-1 installer
(blocks action item below).

#### Option B: `faster-whisper` Python HTTP sidecar
| Dimension | Assessment |
|-----------|------------|
| Complexity | High — new runtime, service lifecycle, container changes |
| Cost | Free; faster on GPU |
| Privacy | Local |
| Team familiarity | Low — introduces Python into a .NET deployment |

**Pros:** Best local accuracy/speed, especially with GPU.
**Cons:** Adds a Python service to a .NET app; new failure mode, new container
surface; over-engineered for a 60-second clip.

#### Option C: Cloud STT (Whisper API / others)
**Pros:** Zero local compute; high accuracy.
**Cons:** Violates the locality decision — ships library audio to a third party.
Rejected.

### Decision 2 — Verdict engine shape

#### Option A: Deterministic core + opt-in LLM behind an interface *(chosen)*
| Dimension | Assessment |
|-----------|------------|
| Complexity | Med |
| Cost | Zero to run (Tier 1); pay-per-use only if cloud LLM enabled |
| Scalability | Walks a full library offline with no external calls |
| Team familiarity | High (reuses existing fuzzy-match primitives) |

**Pros:** Works out-of-the-box with no dependencies; LLM is a clean,
switch-on-later upgrade with no rework; cheapest path for the common case where
the transcript obviously matches or obviously doesn't.
**Cons:** Deterministic tier alone will leave genuinely ambiguous cases in
`Uncertain` until the LLM tier is enabled.

#### Option B: LLM-core, fuzzy-match as pre-filter
**Pros:** Strongest on gray-zone cases from day one.
**Cons:** Cannot function without a model provider; couples the whole feature to
an external/optional dependency; contradicts "off by default." Rejected.

#### Option C: Deterministic-only, no LLM abstraction
**Pros:** Simplest.
**Cons:** Adding model verdicts later means reworking `IIdentityVerifier`'s
contract and call sites. Rejected in favor of designing the seam now.

### Decision 3 — Agent autonomy

#### Option A: Flag only, human confirms *(chosen)*
**Pros:** Smallest blast radius while the library is still messy and STT
accuracy on this corpus is unproven; reuses existing manual delete/re-search UI;
builds a labeled dataset (agent verdict vs. human decision) to later justify
automation.
**Cons:** Slower cleanup; human stays in the loop for every correction.

#### Option B: Auto-act above a confidence threshold
**Cons:** A single STT misfire could auto-delete a correct book. Premature
before we have accuracy data on the real library. Deferred (the verdict pipeline
is designed so an auto-action policy can be added later without rework).

## Trade-off Analysis

The central trade-off is **dependency footprint vs. gray-zone accuracy**. Going
LLM-first or cloud-first maximizes accuracy on ambiguous books but makes the
feature unusable without external services and ships personal data off-host. The
chosen design front-loads the **cheap, offline, high-precision** path (extract →
transcribe → fuzzy-match) that already catches the obvious junk this library is
full of, while building the **seam** for an LLM tier so the harder cases can be
handled later with zero refactor.

The second trade-off is **cleanup speed vs. safety**. Flag-only is slower but
correct for a library we don't yet trust the agent to mutate. Designing the
verdict record as the single source of truth lets an auto-action policy bolt on
later without touching the verifier.

A real limitation to accept up front: self-narrated indies, foreign-language
titles, music/ambient intros, and books without spoken credits will land in
`Uncertain`. These must route to manual review, not be guessed at. The agent is
a **triage filter**, not an oracle.

## Consequences

**Easier:**
- Catching content-level mis-identifications that text-only checks miss.
- Triaging the existing library in bulk via a background job with SignalR
  progress (same UX as unmatched scan).
- Auditing *why* a book was flagged (stored transcript + extracted fields).
- Distinguishing human-confirmed from agent-confirmed books in the UI.

**Harder / new burden:**
- A second bundled binary + model to install, version, and license-track
  (whisper.cpp is MIT; `ggml` model licenses must be noted alongside the
  existing FFmpeg licensing notice).
- Disk/CPU cost of a full-library transcription pass (mitigated: 60s clips,
  cached verdicts, idempotent re-runs that skip already-verified books).
- A new config surface (enable/disable, model size, LLM tier + provider creds).

**To revisit later:**
- Whether to promote high-confidence verdicts to auto-action (Decision 3 Option B).
- Whether to verify against the *credits transcript* as an additional metadata
  source (e.g. recover a correct narrator the metadata got wrong).
- Multi-language model selection when non-English titles are common.

## Domain / schema changes

Add to `listenarr.domain/Models/Audiobook.cs` (one EF migration):

```csharp
public VerificationStatus VerificationStatus { get; set; } = VerificationStatus.Unverified;
public double? VerificationConfidence { get; set; }   // 0..1
public DateTime? VerifiedAt { get; set; }
public string? VerifiedBy { get; set; }               // "agent:whisper-base.en" | username
public string? VerificationMethod { get; set; }       // "deterministic" | "llm:ollama:..." | "manual"
public string? VerificationTranscript { get; set; }   // audit: what STT heard (first/last window)
```

New enum `listenarr.domain/Models/Enumerations/VerificationStatus.cs`:

```csharp
public enum VerificationStatus
{
    Unverified = 0,     // never checked
    AgentVerified = 1,  // agent confirmed metadata matches audio
    AgentFlagged = 2,   // agent thinks metadata is wrong -> needs human review
    ManuallyVerified = 3, // human confirmed (outranks all agent states)
    Rejected = 4        // human confirmed it's wrong / not an audiobook
}
```

**Rule:** an agent pass must never downgrade or overwrite `ManuallyVerified` or
`Rejected`. Manual state is sticky.

## Action Items

### Phase 0 — Foundations (this ADR)
1. [x] Land this ADR; establish `docs/adr/` convention.
2. [x] Add `VerificationStatus` enum + `Audiobook` fields + EF migration.
3. [x] Define `IIdentityVerifier` and the `VerificationVerdict` record. **The
       verdict carries per-field results** — `TitleMatch`, `AuthorMatch`,
       `NarratorMatch`, `PublisherMatch`, each `{ score, matchedText }` — plus an
       aggregate status/confidence, the transcript snippet, and method. Per-field
       structure is required by the "which field diverged?" triage UI and by
       threshold tuning; it is expensive to retrofit after the migration ships,
       so design it now. Input is the book + the **correctly-ordered first audio
       file** (see #5), not "primary file."

### Phase 1 — Local pipeline, deterministic, batch (the immediate triage tool)
4. [x] **(Packaging resolved 2026-06-10: whisper.cpp v1.8.6 publishes NO Linux
       binaries — only Windows zips and an Apple xcframework — so the Dockerfile
       builds it from source in a dedicated stage; arch-agnostic for amd64/arm64.)**
       `WhisperService` shell-out wrapper; install via Dockerfile build-time bake
       (binary + `ggml-base.en` model + license notice).
5. [x] Audio extractor: select the **correctly-ordered first file** for
       multi-file books — sort by track number (natural sort, so "Chapter 2"
       precedes "Chapter 10"), not alphabetically — then clip via FFmpeg to
       16 kHz mono WAV (temp file, reuse FFmpeg temp dir). The sample window is a
       **configurable strategy** (default e.g. first 90s + last 30s), not a
       hardcoded 60s, since credits may sit behind a publisher ident/cold open or
       only appear at the end.
6. [x] `DeterministicIdentityVerifier` — transcript vs. stored metadata producing
       per-field results: title via `NormalizeTitle`/exact-ish; author + narrator
       via **phonetic + edit-distance** (names are the discriminating field and
       the one STT mangles most). Frame the high-confidence signal as **gross
       mismatch detection** (the obvious junk), with match-confirmation treated as
       the lower-confidence path.
7. [x] `LibraryVerificationBackgroundService` + queue, modeled on
       `UnmatchedScanBackgroundService`; idempotent (skips manual/already-verified),
       SignalR progress.
8. [x] API: trigger batch verify, per-book verify, set/clear manual verification.
9. [x] FE: verification badge on book detail + Books list; "Verify library" action;
       a "Needs review" (AgentFlagged + Uncertain) filter/view; manual
       verify / reject buttons.
10. [x] Tests: extractor windowing, deterministic matcher (match/mismatch/uncertain
        fixtures), sticky manual-state rule, batch idempotency.

### Phase 2 — LLM tier (opt-in) + ingestion hook
11. [ ] `IVerificationLlmProvider` with `OllamaProvider` (local) and
        `ClaudeProvider` (cloud, off by default); config + credential surface.
12. [ ] `LlmIdentityVerifier` invoked only when Tier 1 is `Uncertain` and the tier
        is enabled; structured-output prompt; provider-agnostic verdict mapping.
13. [ ] Hook the verifier into import completion so new grabs are auto-checked
        (still flag-only).
14. [ ] Docs: privacy note (audio local; transcript leaves host only with cloud LLM on).

### Deferred (explicitly out of scope here)
- Auto-action on high-confidence mismatches (revisit with real accuracy data).
- Using the credits transcript to *correct* metadata, not just verify it.

## Open questions for review
- Default STT model: `base.en` (smaller/faster) vs `small.en` (more accurate)?
  Proposed default `base.en`, configurable.
- Confidence thresholds for `Match` / `Uncertain` / `Mismatch` — set initial
  values, then tune against the labeled human-vs-agent data Phase 1 produces.
- Where the temp WAV clips live and their cleanup policy (reuse FFmpeg temp dir).
