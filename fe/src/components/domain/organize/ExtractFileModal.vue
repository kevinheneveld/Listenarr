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
          Move this file out of <strong>{{ sourceAudiobookTitle || 'the current audiobook' }}</strong>
          and onto a different audiobook. Search Audible for what the file actually is —
          the title and author below are pre-filled from the file's embedded tags.
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
          <button
            type="button"
            class="btn btn-primary extract-search-btn"
            :disabled="!canSearch"
            @click="onSearch"
          >
            <PhMagnifyingGlass /> Search
          </button>
        </div>

        <p v-if="searchError" class="extract-error" role="alert">{{ searchError }}</p>

        <p v-if="step === 'searching'" class="extract-loading">Searching Audible…</p>

        <ul v-else-if="candidates.length" class="extract-candidate-list">
          <li
            v-for="candidate in candidates"
            :key="candidate.asin || candidate.title"
            class="extract-candidate"
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
              <div class="extract-candidate-title">{{ candidate.title || '(untitled)' }}</div>
              <div class="extract-candidate-meta">
                <span v-if="candidateAuthorString(candidate)">{{ candidateAuthorString(candidate) }}</span>
                <span v-if="candidateNarratorString(candidate)">· Narrated by {{ candidateNarratorString(candidate) }}</span>
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
              <dt>Title</dt><dd>{{ embedded?.title || '—' }}</dd>
              <dt>Author</dt><dd>{{ embedded?.author || '—' }}</dd>
              <dt>Narrator</dt><dd>{{ embedded?.narrator || '—' }}</dd>
              <dt>Year</dt><dd>{{ embedded?.year || '—' }}</dd>
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
              <dt>Title</dt><dd>{{ selectedMetadata?.title || '—' }}</dd>
              <dt>Author</dt>
              <dd>{{ (selectedMetadata?.authors || []).join(', ') || '—' }}</dd>
              <dt>Narrator</dt>
              <dd>{{ (selectedMetadata?.narrators || []).join(', ') || '—' }}</dd>
              <dt>Year</dt><dd>{{ selectedMetadata?.publishYear || '—' }}</dd>
              <dt>ASIN</dt><dd>{{ selectedMetadata?.asin || '—' }}</dd>
            </dl>
          </div>
        </div>
        <p v-if="submitError" class="extract-error" role="alert">{{ submitError }}</p>
      </template>

      <template v-else-if="step === 'conflict' && conflict">
        <p class="extract-intro">
          An audiobook with this ASIN is already in your library:
          <strong>{{ conflict.existingTitle || 'Untitled' }}</strong>
          ({{ conflict.existingFileCount }} file{{ conflict.existingFileCount === 1 ? '' : 's' }}).
          Pick what you want to do.
        </p>
        <div class="extract-conflict-options">
          <button
            type="button"
            class="extract-conflict-option"
            :class="{ recommended: conflict.recommendedStrategy === 'merge' }"
            @click="onResolveConflict('merge')"
          >
            <strong>Merge file into existing</strong>
            <span class="muted">Adds this file to the existing audiobook; physically moves it under that folder.</span>
            <span v-if="conflict.recommendedStrategy === 'merge'" class="badge">Recommended</span>
          </button>
          <button
            type="button"
            class="extract-conflict-option"
            :class="{ recommended: conflict.recommendedStrategy === 'duplicate' }"
            @click="onResolveConflict('duplicate')"
          >
            <strong>Make a duplicate</strong>
            <span class="muted">Creates a separate audiobook record alongside the existing one.</span>
            <span v-if="conflict.recommendedStrategy === 'duplicate'" class="badge">Recommended</span>
          </button>
        </div>
        <p v-if="conflict.recommendationReason" class="extract-hint">{{ conflict.recommendationReason }}</p>
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
const searchError = ref<string | null>(null)
const searchedOnce = ref(false)
const candidates = ref<AudibleSearchResult[]>([])
const selectedCandidate = ref<AudibleSearchResult | null>(null)
const selectedMetadata = ref<AudibleBookMetadata | null>(null)
const submitError = ref<string | null>(null)
const step = ref<Step>('picking')
const conflict = ref<ExtractFileResult['conflict']>(undefined)

const canSearch = computed(() =>
  !!searchTitle.value.trim() && step.value !== 'searching' && step.value !== 'submitting',
)
const canSubmit = computed(() =>
  !!selectedMetadata.value && !!props.audiobookId && !!props.fileId,
)
// Extract the comparison into a computed so vue-tsc doesn't narrow `step` inside a
// `v-if="step === 'confirming'"` block and then complain that the inline
// `step === 'submitting'` comparison has no overlap with the narrowed type.
const isSubmitting = computed(() => step.value === 'submitting')

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
  try {
    const resp = await apiService.searchAudibleByTitleAndAuthor(
      searchTitle.value.trim(),
      searchAuthor.value.trim(),
    )
    candidates.value = resp?.results ?? []
    searchedOnce.value = true
  } catch (err) {
    searchError.value = err instanceof Error ? err.message : 'Audible search failed.'
  } finally {
    if (step.value === 'searching') step.value = 'picking'
  }
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
    const fetched = (await apiService.getAudibleMetadata<unknown>(candidate.asin)) as {
      metadata?: unknown
    } | null
    const inner = (fetched && typeof fetched === 'object' && 'metadata' in fetched
      ? (fetched as { metadata: unknown }).metadata
      : fetched) as Record<string, unknown> | null
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
  return (c.authors || []).map((a) => a?.name).filter(Boolean).join(', ')
}

function candidateNarratorString(c: AudibleSearchResult): string {
  return (c.narrators || []).map((n) => n?.name).filter(Boolean).join(', ')
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

.extract-search-row {
  display: grid;
  grid-template-columns: 1fr 1fr auto;
  gap: 0.75rem;
  align-items: end;
  margin-bottom: 1rem;
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

.extract-conflict-option {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  align-items: flex-start;
  padding: 0.75rem 1rem;
  border: 1px solid var(--border-color, #333);
  border-radius: 6px;
  background: transparent;
  text-align: left;
  cursor: pointer;
  position: relative;
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
