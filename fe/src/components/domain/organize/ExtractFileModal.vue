<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU Affero General Public License for more details.

  You should have received a copy of the GNU Affero General Public License
  along with this program. If not, see <https://www.gnu.org/licenses/>.
-->
<template>
  <Modal :visible="visible" size="lg" :title="title" @close="onClose">
    <template #header>
      <ModalHeader :title="title" @close="onClose">
        <template #icon><PhArrowSquareOut /></template>
      </ModalHeader>
    </template>

    <ModalBody>
      <p v-if="loadingEmbedded" class="extract-loading">Reading file metadata…</p>

      <template v-else-if="step === 'picking' || step === 'searching'">
        <p class="extract-intro">
          Move this file out of
          <strong>{{ sourceAudiobookTitle || 'the current audiobook' }}</strong>
          and onto a different audiobook. Search Audible for what the file actually is — the title
          and author below are pre-filled from the file's embedded tags.
        </p>
        <div class="extract-file-path" v-if="embedded?.currentPath">
          <div class="extract-file-path-row">
            <span><strong>File:</strong> {{ embedded.currentPath }}</span>
            <button
              v-if="canPreviewFile && props.audiobookId && props.fileId"
              type="button"
              class="extract-preview-btn"
              title="Preview file"
              aria-label="Preview file"
              @click="onPreviewFile"
            >
              <PhPlay weight="fill" /> Preview
            </button>
          </div>
          <div v-if="embedded.narrator" class="extract-file-narrator">
            <strong>Narrator (from file tags):</strong> {{ embedded.narrator }}
            <span class="extract-file-narrator-hint">
              — candidates whose narrator matches will be shown first.
            </span>
          </div>
        </div>

        <div class="extract-search-row">
          <div class="extract-search-field">
            <label class="field-label" for="extract-title-input">Title</label>
            <input
              id="extract-title-input"
              v-model="searchTitle"
              type="text"
              class="form-control"
              :disabled="step === 'searching'"
              spellcheck="false"
              autocomplete="off"
              @keydown.enter.prevent="onSearch"
            />
          </div>
          <div class="extract-search-field">
            <label class="field-label" for="extract-author-input">Author</label>
            <input
              id="extract-author-input"
              v-model="searchAuthor"
              type="text"
              class="form-control"
              :disabled="step === 'searching'"
              spellcheck="false"
              autocomplete="off"
              @keydown.enter.prevent="onSearch"
            />
          </div>
          <div class="extract-search-field extract-region-field">
            <label class="field-label" for="extract-region-select">Region</label>
            <select
              id="extract-region-select"
              v-model="searchRegion"
              class="form-control"
              :disabled="step === 'searching'"
            >
              <option v-for="opt in REGION_OPTIONS" :key="opt.code" :value="opt.code">
                {{ opt.label }}
              </option>
            </select>
          </div>
          <button
            type="button"
            class="btn btn-primary extract-search-btn"
            :disabled="!canSearch"
            @click="onSearch"
          >
            <PhMagnifyingGlass /> Search
          </button>
        </div>

        <div class="extract-search-row extract-paste-row">
          <div class="extract-search-field">
            <label class="field-label" for="extract-asin-input"
              >Or paste an Audible link / ASIN</label
            >
            <input
              id="extract-asin-input"
              v-model="pasteAsinInput"
              type="text"
              class="form-control"
              placeholder="B0CSV7NJMB or https://www.audible.com/pd/…/B0CSV7NJMB"
              spellcheck="false"
              autocomplete="off"
              @keydown.enter.prevent="onLoadPastedAsin"
            />
          </div>
          <button
            type="button"
            class="btn extract-search-btn"
            :disabled="!pasteAsinInput.trim() || step === 'searching'"
            @click="onLoadPastedAsin"
          >
            Load
          </button>
        </div>

        <p v-if="regionNotice" class="extract-region-notice">{{ regionNotice }}</p>
        <p v-if="searchError" class="extract-error" role="alert">{{ searchError }}</p>

        <p v-if="step === 'searching'" class="extract-loading">Searching Audible…</p>

        <ul v-else-if="rankedCandidates.length" class="extract-candidate-list">
          <li
            v-for="candidate in rankedCandidates"
            :key="candidate.asin || candidate.title"
            class="extract-candidate"
            :class="{
              'extract-candidate--narrator-match': candidateMatchesEmbeddedNarrator(candidate),
            }"
            tabindex="0"
            role="button"
            @click="onPickCandidate(candidate)"
            @keydown.enter.prevent="onPickCandidate(candidate)"
            @keydown.space.prevent="onPickCandidate(candidate)"
          >
            <img
              v-if="candidate.imageUrl"
              class="extract-candidate-cover"
              :src="candidate.imageUrl"
              alt=""
              loading="lazy"
            />
            <div class="extract-candidate-body">
              <div class="extract-candidate-title">
                {{ candidate.title || '(untitled)' }}
                <span
                  v-if="candidateMatchesEmbeddedNarrator(candidate)"
                  class="extract-candidate-badge"
                >
                  Narrator matches
                </span>
              </div>
              <div class="extract-candidate-meta">
                <span v-if="candidateAuthorString(candidate)">{{
                  candidateAuthorString(candidate)
                }}</span>
                <span v-if="candidateNarratorString(candidate)"
                  >· Narrated by {{ candidateNarratorString(candidate) }}</span
                >
                <span v-if="candidate.releaseDate">· {{ formatYear(candidate.releaseDate) }}</span>
                <span v-if="candidate.asin">· ASIN {{ candidate.asin }}</span>
              </div>
            </div>
          </li>
        </ul>
        <p v-else-if="searchedOnce" class="extract-empty">
          No Audible results. Try editing the title or author and searching again.
        </p>
      </template>

      <template v-else-if="step === 'confirming' && selectedCandidate">
        <p class="extract-intro">
          Confirm the move. The file will be re-tagged in the library and physically moved on disk
          under the destination audiobook's folder.
        </p>
        <div class="extract-confirm-grid">
          <div class="extract-confirm-col">
            <h4>File's embedded tags</h4>
            <dl>
              <dt>Title</dt>
              <dd>{{ embedded?.title || '—' }}</dd>
              <dt>Author</dt>
              <dd>{{ embedded?.author || '—' }}</dd>
              <dt>Narrator</dt>
              <dd>{{ embedded?.narrator || '—' }}</dd>
              <dt>Year</dt>
              <dd>{{ embedded?.year || '—' }}</dd>
            </dl>
          </div>
          <div class="extract-confirm-col">
            <h4>Destination (Audible)</h4>
            <img
              v-if="selectedMetadata?.imageUrl"
              class="extract-confirm-cover"
              :src="selectedMetadata.imageUrl"
              alt=""
            />
            <dl>
              <dt>Title</dt>
              <dd>{{ selectedMetadata?.title || '—' }}</dd>
              <dt>Author</dt>
              <dd>{{ (selectedMetadata?.authors || []).join(', ') || '—' }}</dd>
              <dt>Narrator</dt>
              <dd>{{ (selectedMetadata?.narrators || []).join(', ') || '—' }}</dd>
              <dt>Year</dt>
              <dd>{{ selectedMetadata?.publishYear || '—' }}</dd>
              <dt>ASIN</dt>
              <dd>{{ selectedMetadata?.asin || '—' }}</dd>
            </dl>
          </div>
        </div>
        <p v-if="submitError" class="extract-error" role="alert">{{ submitError }}</p>
      </template>

      <template v-else-if="step === 'conflict' && conflict">
        <p class="extract-intro">
          Another audiobook in your library already uses ASIN
          <code>{{ conflict.existingAsin || '—' }}</code
          >:
          <strong>{{ conflict.existingTitle || 'Untitled' }}</strong>
          ({{ conflict.existingFileCount }} file{{ conflict.existingFileCount === 1 ? '' : 's' }})
          <span v-if="conflict.existingBasePath">
            at <code>{{ conflict.existingBasePath }}</code></span
          >. Pick what you want to do.
        </p>

        <div class="extract-conflict-files-compare">
          <details v-if="embedded?.currentPath" class="extract-conflict-existing-files" open>
            <summary>What you're moving</summary>
            <ul class="extract-existing-file-list">
              <li class="extract-existing-file">
                <code>{{ embedded.currentPath }}</code>
                <span class="extract-existing-file-meta">
                  <span v-if="embedded.format">{{ embedded.format.toUpperCase() }}</span>
                  <span v-if="embedded.size">· {{ formatBytes(embedded.size) }}</span>
                  <span v-if="embedded.durationSeconds"
                    >· {{ formatDurationShort(embedded.durationSeconds) }}</span
                  >
                  <span v-if="embedded.bitRate">· {{ embedded.bitRate }} kbps</span>
                </span>
              </li>
            </ul>
          </details>

          <details
            v-if="conflict.existingFiles && conflict.existingFiles.length"
            class="extract-conflict-existing-files"
            open
          >
            <summary>
              What's already in
              <em>{{ conflict.existingBasePath || conflict.existingTitle || 'that audiobook' }}</em>
              <span class="muted"> (if you Merge, this is where the file lands)</span>
            </summary>
            <ul class="extract-existing-file-list">
              <li
                v-for="file in conflict.existingFiles"
                :key="file.fileId"
                class="extract-existing-file"
              >
                <code>{{ file.path || '(unknown path)' }}</code>
                <span class="extract-existing-file-meta">
                  <span v-if="file.format">{{ file.format.toUpperCase() }}</span>
                  <span v-if="file.size">· {{ formatBytes(file.size) }}</span>
                  <span v-if="file.durationSeconds"
                    >· {{ formatDurationShort(file.durationSeconds) }}</span
                  >
                </span>
              </li>
              <li
                v-if="conflict.existingFileCount > (conflict.existingFiles?.length ?? 0)"
                class="extract-existing-file extract-existing-file--more"
              >
                … and {{ conflict.existingFileCount - (conflict.existingFiles?.length ?? 0) }} more
              </li>
            </ul>
          </details>
        </div>

        <div class="extract-conflict-options">
          <button
            type="button"
            class="extract-conflict-option"
            :class="{ recommended: normalizedRecommendation === 'merge' }"
            @click="onResolveConflict('merge')"
          >
            <span class="extract-conflict-option-title">Merge file into existing</span>
            <span class="muted">
              Adds this file to <em>{{ conflict.existingTitle || 'that audiobook' }}</em>
              <span v-if="conflict.existingBasePath"
                >at <code>{{ conflict.existingBasePath }}</code></span
              >.
            </span>
            <span v-if="normalizedRecommendation === 'merge'" class="badge">Recommended</span>
          </button>
          <button
            type="button"
            class="extract-conflict-option"
            :class="{ recommended: normalizedRecommendation === 'duplicate' }"
            @click="onResolveConflict('duplicate')"
          >
            <span class="extract-conflict-option-title">Make a duplicate</span>
            <span class="muted">
              Creates a separate audiobook record
              <span v-if="conflict.proposedDestinationFolder">
                at <code>{{ conflict.proposedDestinationFolder }}</code></span
              >.
            </span>
            <span v-if="normalizedRecommendation === 'duplicate'" class="badge">Recommended</span>
          </button>
        </div>
        <p v-if="conflict.recommendationReason" class="extract-hint">
          {{ conflict.recommendationReason }}
        </p>
        <p v-if="submitError" class="extract-error" role="alert">{{ submitError }}</p>
      </template>

      <p v-if="isSubmitting" class="extract-loading">Moving file…</p>
    </ModalBody>

    <template #footer>
      <button class="cancel-button btn" :disabled="isSubmitting" @click="onClose">Cancel</button>
      <button
        v-if="step === 'confirming'"
        class="btn"
        :disabled="isSubmitting"
        @click="backToResults"
      >
        Back to results
      </button>
      <button
        v-if="step === 'confirming'"
        class="btn btn-primary"
        :disabled="isSubmitting || !canSubmit"
        @click="onSubmitClicked"
      >
        <PhSpinner v-if="isSubmitting" class="spinner" /> Move file
      </button>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import { PhArrowSquareOut, PhMagnifyingGlass, PhPlay, PhSpinner } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import type {
  AudibleBookMetadata,
  AudibleSearchResult,
  EmbeddedFileMetadata,
  ExtractDuplicateStrategy,
  ExtractFileResult,
} from '@/types'

const props = defineProps<{
  visible: boolean
  audiobookId: number | null
  fileId: number | null
  sourceAudiobookTitle?: string | null
  /** Parent sets this when it has an in-browser file-preview component wired up
   *  (kevin/live has FilePreviewModal; canary doesn't yet). Controls whether the
   *  "Preview file" button renders inside the modal. */
  canPreviewFile?: boolean
}>()

const emit = defineEmits<{
  (e: 'close'): void
  (e: 'done', result: ExtractFileResult): void
  /** Ask the parent to open the in-browser preview for this file. Parent should listen
   *  only when it has a file-preview implementation available; the button renders only
   *  when the parent has bound a listener. */
  (e: 'preview-file', payload: { audiobookId: number; fileId: number }): void
}>()

const title = 'Move file to another audiobook'

type Step = 'picking' | 'searching' | 'confirming' | 'conflict' | 'submitting'

const loadingEmbedded = ref(false)
const embedded = ref<EmbeddedFileMetadata | null>(null)
const searchTitle = ref('')
const searchAuthor = ref('')

// Region selection — parity with MetadataBackfillModal: 'auto' walks the
// fallback chain and stops at the first store with matches (regional stores
// carry editions the US store doesn't, e.g. delisted/older recordings).
const REGION_OPTIONS = [
  { code: 'auto', label: 'Auto (US, then UK/CA/AU)' },
  { code: 'us', label: 'US (audible.com)' },
  { code: 'uk', label: 'UK (audible.co.uk)' },
  { code: 'ca', label: 'CA (audible.ca)' },
  { code: 'au', label: 'AU (audible.com.au)' },
] as const
const REGION_FALLBACK_CHAIN: ReadonlyArray<string> = ['us', 'uk', 'ca', 'au']
const searchRegion = ref<string>('auto')
const searchedRegion = ref<string | null>(null)
const regionNotice = computed<string | null>(() => {
  if (searchRegion.value !== 'auto') return null
  if (!searchedRegion.value || searchedRegion.value === 'us') return null
  return `US returned no matches. Showing results from ${searchedRegion.value.toUpperCase()}.`
})

// Direct ASIN / Audible-URL paste — escape hatch for editions Audible's search
// never surfaces (same rationale as the backfill modal's paste field).
const pasteAsinInput = ref('')
const ASIN_TOKEN_REGEX = /\b([0-9A-Z]{10})\b/i
const searchError = ref<string | null>(null)
const searchedOnce = ref(false)
const candidates = ref<AudibleSearchResult[]>([])
const selectedCandidate = ref<AudibleSearchResult | null>(null)
const selectedMetadata = ref<AudibleBookMetadata | null>(null)
const submitError = ref<string | null>(null)
const step = ref<Step>('picking')
const conflict = ref<ExtractFileResult['conflict']>(undefined)

const canSearch = computed(
  () => !!searchTitle.value.trim() && step.value !== 'searching' && step.value !== 'submitting',
)
const canSubmit = computed(() => !!selectedMetadata.value && !!props.audiobookId && !!props.fileId)
// Extract the comparison into a computed so vue-tsc doesn't narrow `step` inside a
// `v-if="step === 'confirming'"` block and then complain that the inline
// `step === 'submitting'` comparison has no overlap with the narrowed type.
const isSubmitting = computed(() => step.value === 'submitting')

// Sort search candidates so any whose narrator overlaps the file's embedded narrator
// float to the top. Stable order preserved within each group so a non-match doesn't
// jump above a non-match. Matching uses normalised substring (case- and
// punctuation-insensitive) on either direction so "Rosamund Pike" matches
// "rosamund pike" but also "Pike" alone if that's all the file tag has.
const rankedCandidates = computed<AudibleSearchResult[]>(() => {
  const list = candidates.value.slice()
  const embeddedNarrator = embedded.value?.narrator?.trim()
  if (!embeddedNarrator) return list
  return list
    .map((candidate, index) => ({
      candidate,
      index,
      matches: candidateMatchesEmbeddedNarrator(candidate),
    }))
    .sort((a, b) => (b.matches ? 1 : 0) - (a.matches ? 1 : 0) || a.index - b.index)
    .map((entry) => entry.candidate)
})

function candidateMatchesEmbeddedNarrator(candidate: AudibleSearchResult): boolean {
  const embeddedNarrator = embedded.value?.narrator?.trim()
  if (!embeddedNarrator) return false
  const normalizedEmbedded = normalizeForMatch(embeddedNarrator)
  if (!normalizedEmbedded) return false
  const candidateNames = (candidate.narrators || [])
    .map((n) => normalizeForMatch(n?.name))
    .filter(Boolean) as string[]
  return candidateNames.some(
    (n) => n.includes(normalizedEmbedded) || normalizedEmbedded.includes(n),
  )
}

function normalizeForMatch(value: unknown): string {
  if (typeof value !== 'string') return ''
  return value
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[^\p{Letter}\p{Number}\s]/gu, '')
    .replace(/\s+/g, ' ')
    .trim()
}

// Backend writes recommendedStrategy as a lowercase string today, but the wire
// shape allows PascalCase too. Lower-case once for the equality checks in the
// template so the "Recommended" badge survives a future serialisation change.
const normalizedRecommendation = computed(() =>
  (conflict.value?.recommendedStrategy ?? '').toLowerCase(),
)

function formatBytes(bytes: number | undefined): string {
  if (!bytes || bytes <= 0) return ''
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let size = bytes
  let unit = 0
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024
    unit++
  }
  return `${size.toFixed(size >= 100 || unit === 0 ? 0 : 1)} ${units[unit]}`
}

function formatDurationShort(seconds: number | undefined): string {
  if (!seconds || seconds <= 0) return ''
  const total = Math.floor(seconds)
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

watch(
  () => [props.visible, props.audiobookId, props.fileId] as const,
  ([visible, audiobookId, fileId]) => {
    if (!visible) return
    void hydrate(audiobookId, fileId)
  },
  { immediate: true },
)

async function hydrate(audiobookId: number | null, fileId: number | null) {
  reset()
  if (!audiobookId || !fileId) return
  loadingEmbedded.value = true
  try {
    const meta = await apiService.getFileEmbeddedMetadata(audiobookId, fileId)
    embedded.value = meta
    searchTitle.value = stripSubtitlePart(meta.title || '')
    searchAuthor.value = meta.author || ''
    void nextTick(() => {
      // Auto-trigger search when we have enough to go on
      if (canSearch.value) void onSearch()
    })
  } catch (err) {
    searchError.value = err instanceof Error ? err.message : 'Failed to read file metadata.'
  } finally {
    loadingEmbedded.value = false
  }
}

function reset() {
  embedded.value = null
  searchTitle.value = ''
  searchAuthor.value = ''
  searchError.value = null
  searchedOnce.value = false
  searchedRegion.value = null
  pasteAsinInput.value = ''
  candidates.value = []
  selectedCandidate.value = null
  selectedMetadata.value = null
  submitError.value = null
  conflict.value = undefined
  step.value = 'picking'
  loadingEmbedded.value = false
}

async function onSearch() {
  if (!canSearch.value) return
  step.value = 'searching'
  searchError.value = null
  candidates.value = []
  searchedRegion.value = null
  try {
    const regions = searchRegion.value === 'auto' ? REGION_FALLBACK_CHAIN : [searchRegion.value]
    for (const region of regions) {
      const resp = await apiService.searchAudibleByTitleAndAuthor(
        searchTitle.value.trim(),
        searchAuthor.value.trim(),
        1,
        50,
        region,
      )
      const results = resp?.results ?? []
      if (results.length > 0) {
        candidates.value = results
        searchedRegion.value = region
        break
      }
    }
    searchedOnce.value = true
  } catch (err) {
    searchError.value = err instanceof Error ? err.message : 'Audible search failed.'
  } finally {
    if (step.value === 'searching') step.value = 'picking'
  }
}

function onLoadPastedAsin() {
  const match = (pasteAsinInput.value || '').trim().match(ASIN_TOKEN_REGEX)
  if (!match) {
    searchError.value = 'Paste an Audible URL or a 10-character ASIN (e.g. B0CSV7NJMB).'
    return
  }
  searchError.value = null
  // Reuse the candidate-pick path: it fetches the per-ASIN metadata and moves
  // to the confirm step; the search-side fields just stay empty.
  void onPickCandidate({ asin: match[1].toUpperCase() } as AudibleSearchResult)
}

async function onPickCandidate(candidate: AudibleSearchResult) {
  if (!candidate.asin) {
    searchError.value = 'This candidate has no ASIN and cannot be used.'
    return
  }
  selectedCandidate.value = candidate
  submitError.value = null
  try {
    // GET /metadata/{asin} returns an envelope { metadata, source, sourceUrl } where the
    // inner shape is AudibleBookResponse (authors / narrators as objects, releaseDate as
    // string, isbn as a single string, etc.) — not AudibleBookMetadata. Unwrap and then
    // build an AudibleBookMetadata-shaped payload by combining the inner response with
    // the picked candidate (search-side data fills in anything the per-ASIN call lacks).
    // Resolve against the store the candidate came from — a UK/AU candidate's
    // ASIN is only guaranteed resolvable in its own region.
    const pickRegion =
      searchedRegion.value || (searchRegion.value !== 'auto' ? searchRegion.value : 'us')
    const fetched = (await apiService.getAudibleMetadata<unknown>(candidate.asin, pickRegion)) as {
      metadata?: unknown
    } | null
    const inner = (
      fetched && typeof fetched === 'object' && 'metadata' in fetched
        ? (fetched as { metadata: unknown }).metadata
        : fetched
    ) as Record<string, unknown> | null
    selectedMetadata.value = buildMetadataFromCandidate(candidate, inner)
    step.value = 'confirming'
  } catch (err) {
    searchError.value = err instanceof Error ? err.message : 'Failed to load Audible metadata.'
    selectedCandidate.value = null
  }
}

/**
 * Build the AudibleBookMetadata payload we'll send to the backend by merging two
 * sources: the picked AudibleSearchResult (always populated — comes from the search)
 * and the unwrapped per-ASIN metadata (richer fields like description, isbn, but
 * shaped differently — uses AudibleAuthor/AudibleNarrator objects).
 *
 * Preference order per field: the per-ASIN response wins when present, else fall back
 * to the candidate. Authors and narrators are normalised to string[].
 */
function buildMetadataFromCandidate(
  candidate: AudibleSearchResult,
  inner: Record<string, unknown> | null,
): AudibleBookMetadata {
  const innerAuthors = extractNames(inner?.authors)
  const innerNarrators = extractNames(inner?.narrators)
  const candidateAuthors = (candidate.authors || []).map((a) => a?.name).filter(isNonEmptyString)
  const candidateNarrators = (candidate.narrators || [])
    .map((n) => n?.name)
    .filter(isNonEmptyString)
  const releaseDate = pickString(inner?.releaseDate, inner?.publishDate, candidate.releaseDate)
  const publishYear = releaseDate ? releaseDate.slice(0, 4) : undefined
  const innerIsbn = inner?.isbn
  const isbnList = Array.isArray(innerIsbn)
    ? (innerIsbn.filter(isNonEmptyString) as string[])
    : isNonEmptyString(innerIsbn)
      ? [innerIsbn]
      : []

  return {
    asin: candidate.asin || pickString(inner?.asin) || '',
    title: pickString(inner?.title, candidate.title) || '',
    subtitle: pickString(inner?.subtitle),
    authors: innerAuthors.length ? innerAuthors : candidateAuthors,
    narrators: innerNarrators.length ? innerNarrators : candidateNarrators,
    imageUrl: pickString(inner?.imageUrl, candidate.imageUrl),
    publishedDate: releaseDate,
    publishYear: publishYear && /^\d{4}$/.test(publishYear) ? publishYear : undefined,
    description: pickString(inner?.description),
    publisher: pickString(inner?.publisher, candidate.publisher),
    language: pickString(inner?.language, candidate.language),
    runtime: pickNumber(inner?.lengthMinutes, candidate.lengthMinutes),
    isbn: isbnList.length ? isbnList[0] : undefined,
  } as AudibleBookMetadata
}

function extractNames(value: unknown): string[] {
  if (!Array.isArray(value)) return []
  return value
    .map((item) => (item && typeof item === 'object' ? (item as { name?: unknown }).name : item))
    .filter(isNonEmptyString) as string[]
}

function pickString(...values: unknown[]): string | undefined {
  for (const v of values) {
    if (isNonEmptyString(v)) return v
  }
  return undefined
}

function pickNumber(...values: unknown[]): number | undefined {
  for (const v of values) {
    if (typeof v === 'number' && Number.isFinite(v)) return v
  }
  return undefined
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0
}

function backToResults() {
  step.value = 'picking'
  selectedMetadata.value = null
  selectedCandidate.value = null
  submitError.value = null
}

function onSubmitClicked() {
  void onSubmit('none')
}

function onPreviewFile() {
  if (!props.audiobookId || !props.fileId) return
  emit('preview-file', { audiobookId: props.audiobookId, fileId: props.fileId })
}

async function onSubmit(strategy: ExtractDuplicateStrategy = 'none') {
  if (!canSubmit.value || !props.audiobookId || !props.fileId || !selectedMetadata.value) return
  const previousStep = step.value
  step.value = 'submitting'
  submitError.value = null
  try {
    const result = await apiService.extractFileToNewAudiobook(props.audiobookId, props.fileId, {
      metadata: selectedMetadata.value,
      duplicateStrategy: strategy,
    })
    if (result.success) {
      emit('done', result)
      return
    }
    if (result.conflict) {
      conflict.value = result.conflict
      step.value = 'conflict'
      return
    }
    submitError.value = result.error || 'Failed to move file.'
    step.value = previousStep
  } catch (err) {
    submitError.value = err instanceof Error ? err.message : 'Failed to move file.'
    step.value = previousStep
  }
}

function onResolveConflict(strategy: 'merge' | 'duplicate') {
  void onSubmit(strategy)
}

function onClose() {
  if (step.value === 'submitting') return
  emit('close')
}

function candidateAuthorString(c: AudibleSearchResult): string {
  return (c.authors || [])
    .map((a) => a?.name)
    .filter(Boolean)
    .join(', ')
}

function candidateNarratorString(c: AudibleSearchResult): string {
  return (c.narrators || [])
    .map((n) => n?.name)
    .filter(Boolean)
    .join(', ')
}

function formatYear(date?: string): string {
  if (!date) return ''
  const year = date.slice(0, 4)
  return /^\d{4}$/.test(year) ? year : ''
}

// Embedded titles often include "Title: Subtitle" form. Trim everything after the first
// colon as a starting point for the search — the user can edit before searching.
function stripSubtitlePart(title: string): string {
  const idx = title.indexOf(':')
  return idx > 0 ? title.slice(0, idx).trim() : title.trim()
}
</script>

<style scoped>
.extract-loading {
  margin: 0 0 1rem 0;
  color: var(--text-muted, #888);
}

.extract-intro {
  margin: 0 0 1rem 0;
}

.extract-file-path {
  margin: 0 0 1rem 0;
  padding: 0.5rem 0.75rem;
  background: rgba(255, 255, 255, 0.04);
  border-radius: 4px;
  font-family: monospace;
  font-size: 13px;
  word-break: break-all;
}

.extract-file-path-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  justify-content: space-between;
}

.extract-preview-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  flex-shrink: 0;
  background: transparent;
  border: 1px solid var(--border-color, #3a3a3a);
  border-radius: 4px;
  color: #ddd;
  cursor: pointer;
  padding: 0.25rem 0.6rem;
  font-family: inherit;
  font-size: 12px;
}

.extract-preview-btn:hover,
.extract-preview-btn:focus-visible {
  background: var(--brand-focus, #3b82f6);
  border-color: var(--brand-focus, #3b82f6);
  color: #fff;
}

.extract-file-narrator {
  margin-top: 0.5rem;
  font-family: var(--font-family, sans-serif);
  font-size: 13px;
}

.extract-file-narrator-hint {
  color: var(--text-muted, #888);
  margin-left: 0.5rem;
}

.extract-candidate--narrator-match {
  border-color: var(--brand-focus, #3b82f6);
  background: rgba(59, 130, 246, 0.06);
}

.extract-candidate-badge {
  display: inline-block;
  margin-left: 0.5rem;
  padding: 0.1rem 0.45rem;
  background: var(--brand-focus, #3b82f6);
  color: #fff;
  border-radius: 10px;
  font-size: 11px;
  font-weight: 600;
  vertical-align: middle;
}

.extract-search-row {
  display: grid;
  grid-template-columns: 1fr 1fr auto;
  gap: 0.75rem;
  align-items: end;
  margin-bottom: 1rem;
}

/* Title | Author | Region | Search */
.extract-search-row:not(.extract-paste-row) {
  grid-template-columns: 1fr 1fr minmax(150px, auto) auto;
}

.extract-paste-row {
  grid-template-columns: 1fr auto;
  margin-top: -0.25rem;
}

.extract-region-notice {
  margin: 0 0 0.75rem;
  padding: 0.5rem 0.75rem;
  border-radius: 6px;
  background: rgba(243, 156, 18, 0.08);
  border: 1px solid rgba(243, 156, 18, 0.2);
  color: #f39c12;
  font-size: 0.85rem;
}

.extract-search-field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.field-label {
  font-weight: 600;
}

.extract-search-btn {
  white-space: nowrap;
}

.extract-error {
  color: var(--error-color, #c53030);
}

.extract-empty {
  color: var(--text-muted, #888);
  font-style: italic;
}

.extract-hint {
  color: var(--text-muted, #888);
  font-size: 13px;
  margin: 0.5rem 0 0 0;
}

.extract-candidate-list {
  list-style: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  max-height: 50vh;
  overflow-y: auto;
}

.extract-candidate {
  display: flex;
  gap: 0.75rem;
  padding: 0.5rem;
  border: 1px solid var(--border-color, #333);
  border-radius: 6px;
  cursor: pointer;
  background: transparent;
}

.extract-candidate:hover,
.extract-candidate:focus-visible {
  background: rgba(255, 255, 255, 0.04);
  border-color: var(--brand-focus, #3b82f6);
}

.extract-candidate-cover {
  width: 64px;
  height: 64px;
  object-fit: cover;
  border-radius: 4px;
  flex-shrink: 0;
}

.extract-candidate-body {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  min-width: 0;
}

.extract-candidate-title {
  font-weight: 600;
}

.extract-candidate-meta {
  color: var(--text-muted, #888);
  font-size: 13px;
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
}

.extract-confirm-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 1.5rem;
  margin-bottom: 1rem;
}

.extract-confirm-col h4 {
  margin: 0 0 0.5rem 0;
  font-size: 14px;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--text-muted, #888);
}

.extract-confirm-col dl {
  display: grid;
  grid-template-columns: max-content 1fr;
  column-gap: 0.75rem;
  row-gap: 0.35rem;
  margin: 0;
}

.extract-confirm-col dt {
  color: var(--text-muted, #888);
  font-weight: 600;
}

.extract-confirm-col dd {
  margin: 0;
  word-break: break-word;
}

.extract-confirm-cover {
  width: 120px;
  height: 120px;
  object-fit: cover;
  border-radius: 6px;
  margin-bottom: 0.75rem;
}

.extract-conflict-options {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  margin-bottom: 0.5rem;
}

.extract-conflict-existing-files {
  margin: 0 0 1rem 0;
  padding: 0.5rem 0.75rem;
  background: rgba(255, 255, 255, 0.03);
  border-radius: 4px;
  border: 1px solid var(--border-color, #333);
}

.extract-conflict-existing-files > summary {
  cursor: pointer;
  font-weight: 600;
  font-size: 13px;
  color: var(--text-muted, #aaa);
}

.extract-conflict-existing-files[open] > summary {
  margin-bottom: 0.5rem;
}

.extract-existing-file-list {
  list-style: none;
  padding: 0;
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  max-height: 35vh;
  overflow-y: auto;
}

.extract-existing-file {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  font-size: 13px;
}

.extract-existing-file code {
  font-family: var(--font-family-monospace, monospace);
  word-break: break-all;
}

.extract-existing-file-meta {
  color: var(--text-muted, #888);
  font-size: 12px;
  display: flex;
  gap: 0.4rem;
}

.extract-existing-file--more {
  color: var(--text-muted, #888);
  font-style: italic;
}

.extract-conflict-option {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  align-items: flex-start;
  padding: 0.85rem 1rem;
  border: 1px solid var(--border-color, #333);
  border-radius: 6px;
  background: transparent;
  text-align: left;
  cursor: pointer;
  position: relative;
}

.extract-conflict-option-title {
  font-weight: 700;
  font-size: 15px;
  color: var(--text-primary, #fff);
}

.extract-conflict-option:hover,
.extract-conflict-option:focus-visible {
  background: rgba(255, 255, 255, 0.04);
  border-color: var(--brand-focus, #3b82f6);
}

.extract-conflict-option.recommended {
  border-color: var(--brand-focus, #3b82f6);
}

.extract-conflict-option .muted {
  color: var(--text-muted, #888);
  font-size: 13px;
}

.extract-conflict-option .badge {
  position: absolute;
  top: 0.5rem;
  right: 0.75rem;
  background: var(--brand-focus, #3b82f6);
  color: #fff;
  padding: 0.15rem 0.5rem;
  border-radius: 12px;
  font-size: 11px;
  font-weight: 600;
}

.spinner {
  animation: extract-spin 1s linear infinite;
  margin-right: 0.35rem;
}

@keyframes extract-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
