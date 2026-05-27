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
<!--
  Reviews online metadata field-by-field against the audiobook's current
  values, letting the user pick which fields to overwrite. Two phases:

    1. Identifier resolution. If the book already has an ASIN we skip
       straight to phase 2. Otherwise we search Audible by title + author and
       let the user pick the matching result.

    2. Preview + apply. We fetch the chosen ASIN's full metadata and render a
       comparison table. Checkboxes default to OFF for fields the book
       already has (overwriting existing data is opt-in) and ON for fields
       that are empty. Apply submits a PUT /library/{id} with only the
       selected fresh values.

  No new backend endpoints — this composes the existing search, metadata
  lookup, and library-update endpoints.
-->
<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import {
  PhX,
  PhSpinner,
  PhCheckCircle,
  PhWarning,
  PhMagnifyingGlass,
  PhArrowLeft,
  PhDownloadSimple,
  PhPlay,
} from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import FilePreviewModal from '@/components/domain/audiobook/FilePreviewModal.vue'
import { useToast } from '@/services/toastService'
import { logger } from '@/utils/logger'
import type {
  Audiobook,
  AudibleSearchResult,
  EmbeddedFileMetadata,
} from '@/types'

interface Props {
  visible: boolean
  audiobook: Audiobook | null
  region?: string
}

interface Emits {
  (e: 'close'): void
  (e: 'applied', updated: Audiobook): void
}

const props = withDefaults(defineProps<Props>(), {
  region: 'us',
})
const emit = defineEmits<Emits>()

const toast = useToast()

// ── Types ──────────────────────────────────────────────────────────────────

// What the Audible-by-ASIN endpoint returns. We map this into a normalised
// `fresh` shape that lines up with Audiobook fields.
interface FreshMetadata {
  asin?: string
  title?: string
  subtitle?: string
  authors?: string[]
  narrators?: string[]
  publisher?: string
  publishedDate?: string
  description?: string
  imageUrl?: string
  language?: string
  runtime?: number
  genres?: string[]
  series?: string
  seriesNumber?: string
  isbn?: string
}

type FieldKey =
  | 'title'
  | 'subtitle'
  | 'authors'
  | 'narrators'
  | 'publisher'
  | 'publishedDate'
  | 'description'
  | 'imageUrl'
  | 'language'
  | 'runtime'
  | 'genres'
  | 'series'
  | 'seriesNumber'
  | 'isbn'
  | 'asin'

const FIELD_LABELS: Record<FieldKey, string> = {
  title: 'Title',
  subtitle: 'Subtitle',
  authors: 'Authors',
  narrators: 'Narrators',
  publisher: 'Publisher',
  publishedDate: 'Publish date',
  description: 'Description',
  imageUrl: 'Cover art',
  language: 'Language',
  runtime: 'Runtime (min)',
  genres: 'Genres',
  series: 'Series',
  seriesNumber: 'Series #',
  isbn: 'ISBN',
  asin: 'ASIN',
}

// ── State ──────────────────────────────────────────────────────────────────

type Phase = 'idle' | 'searching' | 'pick-candidate' | 'fetching' | 'review' | 'applying'

const phase = ref<Phase>('idle')
const errorMessage = ref<string | null>(null)
// Informational notice surfaced when an ASIN lookup returned no usable
// metadata (404, empty payload, or transient error) and we automatically
// fell back to a title/author search. Distinct from errorMessage so the
// candidate list isn't framed as a failure — the user has a workable next
// step. Cleared on every reset() / new lookup.
const fallbackNotice = ref<string | null>(null)
const candidates = ref<AudibleSearchResult[]>([])
const chosenAsin = ref<string | null>(null)
const fresh = ref<FreshMetadata | null>(null)
const selected = ref<Set<FieldKey>>(new Set())

// Editable search terms shown above the candidate list so the user can refine
// the query (e.g. drop a middle initial, fix a typo) without closing this
// modal and reopening the edit-audiobook modal.
const overrideTitle = ref('')
const overrideAuthor = ref('')

// In-browser audio preview of the owned file. Lets the user audition the
// narrator before committing to a candidate — Audible often lists multiple
// editions of the same book under different ASINs (different narrators or
// abridgements) and the cover/title alone can't tell them apart.
const showPreview = ref(false)
const previewFile = computed(() => props.audiobook?.files?.[0] ?? null)

// Embedded ffprobe tags from the file's composer field — used to surface the
// file's narrator near the search inputs and to rank candidates whose narrator
// matches the file we actually own. Best-effort: failure to read embedded
// metadata is non-fatal (the rest of the modal still works).
const embedded = ref<EmbeddedFileMetadata | null>(null)

// Direct ASIN / Audible-URL paste — escape hatch for cases where Audible's
// search doesn't surface the right edition. Example from the wild:
// "Robots and Empire" (B0CSV7NJMB) is fully accessible via the per-ASIN
// metadata endpoint but never appears in Audible's keyword or author-page
// search results, so the user has no way to reach it through the candidate
// picker. Pasting the audible.com URL (or the bare ASIN) lets them load
// the metadata directly. The ASIN is the only thing we need — anything
// else in the URL is decoration.
const pasteAsinInput = ref('')
const pasteAsinError = ref<string | null>(null)
// 10-character alphanumeric Audible identifier. Audible URLs come in many
// shapes (different stores, ref/srsltid query params, with/without trailing
// slash) — extracting the first 10-char token in the URL is the most
// resilient parse. Audiobook ASINs typically start with B0 but older
// titles and some collections use other prefixes, so don't lock to B0.
const ASIN_TOKEN_REGEX = /\b([0-9A-Z]{10})\b/i
const parsedPasteAsin = computed(() => parseAsinFromInput(pasteAsinInput.value))

function parseAsinFromInput(raw: string): string | null {
  const trimmed = (raw || '').trim()
  if (!trimmed) return null
  const match = trimmed.match(ASIN_TOKEN_REGEX)
  return match ? match[1].toUpperCase() : null
}

function loadFromPaste() {
  const asin = parsedPasteAsin.value
  if (!asin) {
    pasteAsinError.value = 'Paste an Audible URL or a 10-character ASIN (e.g. B0CSV7NJMB).'
    return
  }
  pasteAsinError.value = null
  // pickCandidate already records the chosen ASIN and triggers fetchPreview,
  // which transitions the modal into the comparison phase with the loaded
  // metadata. Reuse it so the paste path and the click-a-candidate path
  // converge on the same downstream behaviour.
  pickCandidate(asin)
}

// ── Open / reset ───────────────────────────────────────────────────────────

watch(
  () => props.visible,
  (visible) => {
    if (!visible) return
    void start()
  },
)

async function start() {
  reset()
  if (!props.audiobook) {
    errorMessage.value = 'No audiobook selected.'
    return
  }

  // Kick off embedded-tag fetch in parallel with the candidate search.
  // We don't await it: the narrator hint and ranking become available as
  // soon as the request resolves, but the search shouldn't block on it.
  void loadEmbeddedMetadata()

  const asin = (props.audiobook.asin || '').trim()
  if (asin) {
    chosenAsin.value = asin
    await fetchPreview(asin)
  } else {
    await searchCandidates()
  }
}

async function loadEmbeddedMetadata() {
  const book = props.audiobook
  const file = previewFile.value
  if (!book || !file) return
  try {
    embedded.value = await apiService.getFileEmbeddedMetadata(book.id, file.id)
  } catch (err) {
    // Non-fatal — the modal still works without the narrator hint or ranking.
    logger.warn('MetadataBackfillModal: embedded metadata fetch failed', err)
  }
}

function reset() {
  phase.value = 'idle'
  errorMessage.value = null
  fallbackNotice.value = null
  candidates.value = []
  chosenAsin.value = null
  fresh.value = null
  selected.value = new Set()
  // Seed the editable search fields from the current audiobook so the
  // candidate-picker's "Search again" button starts with sensible defaults.
  overrideTitle.value = (props.audiobook?.title || '').trim()
  overrideAuthor.value = ((props.audiobook?.authors && props.audiobook.authors[0]) || '').trim()
  // The paste-ASIN field is per-session; clear it on every open so a stale
  // value from a previous book doesn't surface when the modal reopens.
  pasteAsinInput.value = ''
  pasteAsinError.value = null
  embedded.value = null
}

// ── Candidate ranking by narrator overlap ──────────────────────────────────
//
// Two sources contribute narrator info we can match against:
//   1. The audiobook's current `narrators` DB field (set by a prior backfill,
//      manual edit, or upstream metadata import).
//   2. The file's embedded `composer` tag, surfaced as `embedded.narrator`.
//
// We combine both — a candidate matches if any of its narrators overlaps any
// known narrator from either source. This is the conservative choice: it
// catches the common case where the DB has been backfilled but the file tag
// still differs, and vice versa. Matching is case- and punctuation-insensitive
// with substring in either direction so "Pike" matches "Rosamund Pike".
const sourceNarrators = computed<string[]>(() => {
  const list: string[] = []
  for (const n of props.audiobook?.narrators || []) {
    if (typeof n === 'string' && n.trim()) list.push(n.trim())
  }
  const embeddedNarrator = embedded.value?.narrator?.trim()
  if (embeddedNarrator) list.push(embeddedNarrator)
  // De-dupe on the normalised form so a DB entry "Rosamund Pike" and an
  // embedded "rosamund pike" don't both render.
  const seen = new Set<string>()
  const unique: string[] = []
  for (const n of list) {
    const key = normalizeForMatch(n)
    if (!key || seen.has(key)) continue
    seen.add(key)
    unique.push(n)
  }
  return unique
})

const rankedCandidates = computed<AudibleSearchResult[]>(() => {
  const list = candidates.value.slice()
  if (sourceNarrators.value.length === 0) return list
  return list
    .map((candidate, index) => ({
      candidate,
      index,
      matches: candidateMatchesNarrator(candidate),
    }))
    .sort((a, b) => (b.matches ? 1 : 0) - (a.matches ? 1 : 0) || a.index - b.index)
    .map((entry) => entry.candidate)
})

function candidateMatchesNarrator(candidate: AudibleSearchResult): boolean {
  const sources = sourceNarrators.value
    .map(normalizeForMatch)
    .filter(Boolean) as string[]
  if (sources.length === 0) return false
  const candidateNames = (candidate.narrators || [])
    .map((n) => normalizeForMatch(n?.name))
    .filter(Boolean) as string[]
  if (candidateNames.length === 0) return false
  return candidateNames.some((cn) =>
    sources.some((sn) => cn.includes(sn) || sn.includes(cn)),
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

// ── Phase 1: candidate search ──────────────────────────────────────────────

async function searchCandidates() {
  const book = props.audiobook
  if (!book) return

  // Prefer the editable overrides (seeded from the audiobook on open). Fall
  // back to the audiobook's own values defensively so a stray empty-string
  // override doesn't lose the original search terms.
  const title = (overrideTitle.value || book.title || '').trim()
  const author = (overrideAuthor.value || (book.authors && book.authors[0]) || '').trim()
  // Keep the inputs in sync with what we actually searched for.
  overrideTitle.value = title
  overrideAuthor.value = author
  if (!title) {
    errorMessage.value = 'No title to search with — type a title above and try again.'
    phase.value = 'pick-candidate'
    return
  }

  phase.value = 'searching'
  errorMessage.value = null
  try {
    const response = await apiService.searchAudibleByTitleAndAuthor(
      title,
      author,
      1,
      25,
      props.region,
    )
    candidates.value = response?.results || []
    phase.value = 'pick-candidate'
    if (candidates.value.length === 0) {
      errorMessage.value =
        'No matches found for that title and author. Try editing them above and search again.'
    }
  } catch (err) {
    logger.error('MetadataBackfillModal: candidate search failed', err)
    errorMessage.value = err instanceof Error ? err.message : 'Search failed.'
    phase.value = 'pick-candidate'
  }
}

function pickCandidate(asin: string | undefined | null) {
  if (!asin) return
  chosenAsin.value = asin
  errorMessage.value = null
  void fetchPreview(asin)
}

function backToCandidates() {
  fresh.value = null
  chosenAsin.value = null
  if (candidates.value.length > 0) {
    phase.value = 'pick-candidate'
  } else {
    void searchCandidates()
  }
}

// ── Phase 2: preview fetch ─────────────────────────────────────────────────

async function fetchPreview(asin: string) {
  phase.value = 'fetching'
  errorMessage.value = null
  fallbackNotice.value = null

  // Try the canonical ASIN lookup first. Two failure modes feed the same
  // fallback path: a thrown error (404, network) and a 200 OK with an
  // empty payload (Audible occasionally returns just the ASIN echo for
  // titles that are regionally restricted, delisted, or under a different
  // store than `props.region`). Either way, we'd otherwise show the user
  // an empty Fresh column with "Apply 0 changes" — which is a dead end.
  let fetched: FreshMetadata | null = null
  let lookupError: string | null = null
  try {
    const raw = await apiService.getAudibleMetadata<unknown>(asin, props.region)
    const mapped = mapFresh(raw)
    if (hasUsableMetadata(mapped)) {
      fetched = mapped
    }
  } catch (err) {
    logger.warn('MetadataBackfillModal: ASIN lookup failed; will fall back to title/author search', err)
    lookupError = err instanceof Error ? err.message : 'ASIN lookup failed'
  }

  if (fetched) {
    fresh.value = fetched
    selected.value = defaultSelection(props.audiobook, fresh.value)
    phase.value = 'review'
    return
  }

  // ASIN lookup didn't give us anything usable. If the user actively picked
  // a candidate (`chosenAsin` was set explicitly via pickCandidate), surface
  // it as an error rather than silently falling back — they made a choice
  // we couldn't honour. If the ASIN came from the audiobook's existing
  // saved value (the auto-load path from `start()`), fall back to a
  // title/author search so the user has *something* to act on instead of
  // an empty Fresh column.
  const cameFromExistingAudiobookAsin = props.audiobook?.asin
    && (props.audiobook.asin || '').trim().toUpperCase() === asin.toUpperCase()
    && candidates.value.length === 0

  if (cameFromExistingAudiobookAsin) {
    fallbackNotice.value =
      `Audible returned no metadata for ASIN ${asin}. Showing closest matches by title and author so you can pick a different edition.`
    await searchCandidates()
    return
  }

  errorMessage.value = lookupError ?? 'Audible returned no usable metadata for that ASIN.'
  phase.value = candidates.value.length > 0 ? 'pick-candidate' : 'review'
}

// "Usable" = at least the core identity fields (title, authors) are present.
// Audible occasionally returns an envelope containing only the ASIN echo for
// titles it can't actually serve metadata for (regionally restricted, etc.).
// Without title+authors we can't show the user anything meaningful in the
// review pane, so we treat the response as a miss.
function hasUsableMetadata(m: FreshMetadata | null): boolean {
  if (!m) return false
  const title = (m.title ?? '').trim()
  const authors = Array.isArray(m.authors) ? m.authors.filter((a) => a && a.trim()) : []
  return title.length > 0 || authors.length > 0
}

// Pull fields out of the Audible response (the API returns nested types like
// `authors: [{ name }]`); flatten into a shape that lines up with Audiobook.
//
// GET /metadata/{asin} wraps its payload in an envelope: { metadata, source,
// sourceUrl }. Unwrap that here so callers can rely on a flat shape; fall
// back to raw access for the direct-shape endpoint variant.
function mapFresh(raw: unknown): FreshMetadata {
  const outer = (raw || {}) as Record<string, unknown>
  const r = (
    outer.metadata && typeof outer.metadata === 'object'
      ? (outer.metadata as Record<string, unknown>)
      : outer
  ) as Record<string, unknown>
  const arr = (key: string): unknown[] => (Array.isArray(r[key]) ? (r[key] as unknown[]) : [])

  return {
    asin: typeof r.asin === 'string' ? r.asin : undefined,
    title: typeof r.title === 'string' ? r.title : undefined,
    subtitle: typeof r.subtitle === 'string' ? r.subtitle : undefined,
    authors: arr('authors')
      .map((a) => (a as { name?: string }).name)
      .filter((n): n is string => !!n && !!n.trim()),
    narrators: arr('narrators')
      .map((n) => (n as { name?: string }).name)
      .filter((n): n is string => !!n && !!n.trim()),
    publisher: typeof r.publisher === 'string' ? r.publisher : undefined,
    publishedDate: (typeof r.publishDate === 'string' ? r.publishDate : undefined) ||
      (typeof r.releaseDate === 'string' ? r.releaseDate : undefined),
    description: typeof r.description === 'string' ? r.description : undefined,
    imageUrl: typeof r.imageUrl === 'string' ? r.imageUrl : undefined,
    language: typeof r.language === 'string' ? r.language : undefined,
    runtime: typeof r.lengthMinutes === 'number' ? r.lengthMinutes : undefined,
    genres: arr('genres')
      .map((g) => (g as { name?: string }).name)
      .filter((n): n is string => !!n && !!n.trim()),
    series: arr('series')[0] ? ((arr('series')[0] as { name?: string }).name ?? undefined) : undefined,
    seriesNumber: arr('series')[0]
      ? ((arr('series')[0] as { position?: string }).position ?? undefined)
      : undefined,
    isbn: typeof r.isbn === 'string' ? r.isbn : undefined,
  }
}

// ── Field helpers ──────────────────────────────────────────────────────────

const FIELD_ORDER: FieldKey[] = [
  'title',
  'subtitle',
  'authors',
  'narrators',
  'series',
  'seriesNumber',
  'description',
  'imageUrl',
  'publisher',
  'publishedDate',
  'language',
  'runtime',
  'genres',
  'isbn',
  'asin',
]

function isEmpty(v: unknown): boolean {
  if (v == null) return true
  if (typeof v === 'string') return v.trim().length === 0
  if (Array.isArray(v)) return v.filter((x) => x != null && String(x).trim().length > 0).length === 0
  return false
}

function getCurrent(book: Audiobook | null, key: FieldKey): unknown {
  if (!book) return undefined
  switch (key) {
    case 'isbn':
      return Array.isArray(book.isbn) ? book.isbn[0] : book.isbn
    default:
      return (book as unknown as Record<string, unknown>)[key]
  }
}

function getFresh(metadata: FreshMetadata | null, key: FieldKey): unknown {
  if (!metadata) return undefined
  return (metadata as unknown as Record<string, unknown>)[key]
}

function sameValue(a: unknown, b: unknown, key?: FieldKey): boolean {
  if (isEmpty(a) && isEmpty(b)) return true
  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) return false
    return a.every((v, i) => String(v).trim() === String(b[i]).trim())
  }
  // Date-shaped fields: compare the date portion only. Audible returns ISO
  // timestamps like 2019-06-04T07:00:00Z; the library stores bare YYYY-MM-DD.
  // String-trim equality would always mark them different and let the user
  // overwrite with a no-op.
  if (key === 'publishedDate') {
    return normalizeDateForCompare(a) === normalizeDateForCompare(b)
  }
  return String(a ?? '').trim() === String(b ?? '').trim()
}

function normalizeDateForCompare(v: unknown): string {
  if (v == null) return ''
  const s = String(v).trim()
  if (!s) return ''
  // Match a leading YYYY-MM-DD (with optional time/zone suffix). Anything else
  // falls back to the trimmed string so non-ISO inputs still compare sensibly.
  const m = /^(\d{4}-\d{2}-\d{2})/.exec(s)
  return m ? m[1] : s
}

function defaultSelection(book: Audiobook | null, metadata: FreshMetadata | null): Set<FieldKey> {
  const set = new Set<FieldKey>()
  if (!book || !metadata) return set
  for (const key of FIELD_ORDER) {
    const cur = getCurrent(book, key)
    const f = getFresh(metadata, key)
    if (isEmpty(f)) continue
    if (sameValue(cur, f, key)) continue
    // Default ON only when the book's current value is empty.
    if (isEmpty(cur)) set.add(key)
  }
  return set
}

interface FieldRow {
  key: FieldKey
  label: string
  current: unknown
  fresh: unknown
  empty: boolean // current is empty
  unchanged: boolean // fresh matches current
  selectable: boolean // fresh has a value to offer
}

const rows = computed<FieldRow[]>(() => {
  const book = props.audiobook
  const metadata = fresh.value
  return FIELD_ORDER.map((key) => {
    const cur = getCurrent(book, key)
    const f = getFresh(metadata, key)
    const empty = isEmpty(cur)
    const unchanged = !isEmpty(f) && sameValue(cur, f, key)
    const selectable = !isEmpty(f) && !unchanged
    return {
      key,
      label: FIELD_LABELS[key],
      current: cur,
      fresh: f,
      empty,
      unchanged,
      selectable,
    }
  })
})

const selectableRows = computed(() => rows.value.filter((r) => r.selectable))
const allSelected = computed(
  () =>
    selectableRows.value.length > 0 &&
    selectableRows.value.every((r) => selected.value.has(r.key)),
)

function toggleField(key: FieldKey) {
  if (selected.value.has(key)) selected.value.delete(key)
  else selected.value.add(key)
  selected.value = new Set(selected.value)
}

function toggleAll() {
  if (allSelected.value) {
    selected.value = new Set()
  } else {
    selected.value = new Set(selectableRows.value.map((r) => r.key))
  }
}

function formatValue(v: unknown): string {
  if (isEmpty(v)) return ''
  if (Array.isArray(v)) return v.filter((x) => x != null && String(x).trim()).join(', ')
  return String(v)
}

// ── Apply ──────────────────────────────────────────────────────────────────

async function applyChanges() {
  const book = props.audiobook
  const metadata = fresh.value
  if (!book || !metadata) return

  if (selected.value.size === 0) {
    toast.warning('Nothing to apply', 'No fields are selected.')
    return
  }

  // Build a sparse Audiobook payload — only the selected fields' fresh values.
  // PUT /library/{id} treats non-null fields as "set this", null/missing as
  // "leave alone" (so we don't accidentally clear anything else).
  const payload: Partial<Audiobook> = {}
  for (const key of selected.value) {
    const v = getFresh(metadata, key)
    if (isEmpty(v)) continue
    switch (key) {
      case 'isbn': {
        // Backend expects List<string> even though Audible returns a single
        // string. Wrap it so the JSON deserialiser doesn't 400 the whole PUT.
        const isbnStr = String(v as string).trim()
        payload.isbn = (isbnStr ? [isbnStr] : []) as unknown as Audiobook['isbn']
        break
      }
      case 'runtime':
        payload.runtime = Number(v)
        break
      case 'authors':
      case 'narrators':
      case 'genres':
        ;(payload as Record<string, unknown>)[key] = (v as string[]).slice()
        break
      default:
        ;(payload as Record<string, unknown>)[key] = v
    }
  }

  // If the user is adopting a new ASIN from a candidate, include it explicitly.
  if (chosenAsin.value && (isEmpty(book.asin) || book.asin !== chosenAsin.value)) {
    // Only carry the chosen ASIN through if the user actually selected it,
    // or if the book was ASIN-less to begin with (so the new ASIN can stick).
    if (selected.value.has('asin') || isEmpty(book.asin)) {
      payload.asin = chosenAsin.value
    }
  }

  phase.value = 'applying'
  try {
    // When applying Audible metadata we always want the cover art cached
    // locally — Audible CDN URLs go stale and proxy-served covers are the
    // canonical library shape. No user-facing toggle here; the backfill
    // flow's whole point is to import fresh metadata into the library.
    const result = await apiService.updateAudiobook(book.id, payload, {
      cacheImageLocally: true,
    })
    toast.success(
      'Metadata updated',
      `Applied ${selected.value.size} field${selected.value.size === 1 ? '' : 's'} from Audible.`,
    )
    emit('applied', result.audiobook)
    emit('close')
  } catch (err) {
    logger.error('MetadataBackfillModal: apply failed', err)
    toast.error(
      'Update failed',
      err instanceof Error ? err.message : 'Failed to save the selected fields.',
    )
    phase.value = 'review'
  }
}

function close() {
  if (phase.value === 'applying') return
  emit('close')
}

// ── Misc helpers for the template ──────────────────────────────────────────

function candidateAuthors(c: AudibleSearchResult): string {
  return (c.authors || []).map((a) => a.name).filter(Boolean).join(', ')
}
function candidateNarrators(c: AudibleSearchResult): string {
  return (c.narrators || []).map((n) => n.name).filter(Boolean).join(', ')
}
function candidateYear(c: AudibleSearchResult): string {
  const d = c.releaseDate || ''
  return d.slice(0, 4)
}
</script>

<template>
  <Teleport to="body">
    <div v-if="visible" class="modal-overlay metadata-backfill-overlay" @click.self="close">
      <div class="modal" role="dialog" aria-modal="true">
        <header class="modal-header">
          <h2>
            <PhDownloadSimple />
            Compare with online metadata
          </h2>
          <button class="modal-close" :disabled="phase === 'applying'" @click="close" aria-label="Close">
            <PhX />
          </button>
        </header>

        <div class="modal-body">
          <div v-if="audiobook" class="book-name">
            <p class="book-name-text">
              <strong>{{ audiobook.title }}</strong>
              <span v-if="audiobook.authors?.length" class="muted">
                · {{ audiobook.authors.join(', ') }}
              </span>
            </p>
            <button
              v-if="previewFile"
              type="button"
              class="btn btn-secondary btn-sm preview-btn"
              :title="(audiobook.files?.length ?? 0) > 1
                ? `Audition the first audio file (${audiobook.files?.length} files in this book) so you can identify the narrator before picking a candidate`
                : 'Audition the audio file so you can identify the narrator before picking a candidate'"
              @click="showPreview = true"
            >
              <PhPlay :size="14" />
              Preview my file
            </button>
          </div>

          <div v-if="phase === 'searching'" class="status-row">
            <PhSpinner class="ph-spin" />
            <span>{{ phase === 'searching' ? 'Searching online sources…' : 'Fetching metadata…' }}</span>
          </div>

          <!-- Phase 1: candidate picker -->
          <template v-if="phase === 'pick-candidate' || phase === 'searching'">
            <p class="phase-help">
              This book doesn't have an ASIN. Refine the title or author below if needed, then pick
              the matching result to load its metadata.
            </p>
            <p v-if="sourceNarrators.length" class="narrator-hint">
              <strong>Narrator:</strong> {{ sourceNarrators.join(', ') }}
              <span class="narrator-hint-source muted">
                ({{ embedded?.narrator ? (audiobook?.narrators?.length ? 'file tags + library' : 'from file tags') : 'from library' }})
              </span>
              <span class="narrator-hint-detail muted">
                — candidates whose narrator matches will be shown first.
              </span>
            </p>
            <form
              class="candidate-search-form"
              @submit.prevent="searchCandidates"
            >
              <label class="candidate-search-field">
                <span class="candidate-search-label">Title</span>
                <input
                  v-model="overrideTitle"
                  type="text"
                  class="form-input"
                  :disabled="phase === 'searching'"
                  placeholder="Book title"
                />
              </label>
              <label class="candidate-search-field">
                <span class="candidate-search-label">Author</span>
                <input
                  v-model="overrideAuthor"
                  type="text"
                  class="form-input"
                  :disabled="phase === 'searching'"
                  placeholder="Author name (optional)"
                />
              </label>
              <button
                type="submit"
                class="btn btn-secondary search-again-btn"
                :disabled="phase === 'searching' || !overrideTitle.trim()"
              >
                <PhMagnifyingGlass />
                Search again
              </button>
            </form>

            <!-- Escape hatch when search can't find the right edition. Some
                 Audible records (especially recent releases or alternate
                 editions) exist in the per-ASIN metadata endpoint but never
                 surface in keyword / author-page search results, leaving the
                 user with no way to reach them through the candidate list.
                 Pasting the audible.com URL (or just the ASIN) loads the
                 metadata directly via the same path the candidate-click flow
                 uses. -->
            <form class="paste-asin-form" @submit.prevent="loadFromPaste">
              <label class="paste-asin-field">
                <span class="paste-asin-label">Or paste an Audible link / ASIN</span>
                <input
                  v-model="pasteAsinInput"
                  type="text"
                  class="form-input"
                  :disabled="phase === 'searching'"
                  placeholder="B0CSV7NJMB or https://www.audible.com/pd/.../B0CSV7NJMB"
                />
              </label>
              <button
                type="submit"
                class="btn btn-secondary paste-asin-btn"
                :disabled="phase === 'searching' || !parsedPasteAsin"
                :title="parsedPasteAsin ? `Load metadata for ${parsedPasteAsin}` : 'Paste an Audible URL or ASIN first'"
              >
                <PhDownloadSimple />
                Load
              </button>
            </form>
            <p v-if="pasteAsinError" class="paste-asin-error">{{ pasteAsinError }}</p>
          </template>

          <div v-if="errorMessage && phase !== 'review'" class="status-row error">
            <PhWarning />
            <span>{{ errorMessage }}</span>
          </div>

          <div
            v-if="fallbackNotice && phase === 'pick-candidate'"
            class="status-row notice"
            role="status"
          >
            <PhWarning />
            <span>{{ fallbackNotice }}</span>
          </div>

          <!-- Phase 1: candidate picker -->
          <template v-if="phase === 'pick-candidate'">
            <ul v-if="rankedCandidates.length" class="candidate-list">
              <li
                v-for="c in rankedCandidates"
                :key="c.asin || c.title"
                class="candidate-item"
                :class="{
                  disabled: !c.asin,
                  'candidate-item--narrator-match': candidateMatchesNarrator(c),
                }"
                @click="pickCandidate(c.asin)"
              >
                <div class="candidate-cover-slot">
                  <img v-if="c.imageUrl" :src="c.imageUrl" :alt="c.title || ''" loading="lazy" />
                </div>
                <div class="candidate-meta">
                  <div class="candidate-title">
                    {{ c.title }}
                    <span v-if="candidateMatchesNarrator(c)" class="candidate-badge">
                      Narrator matches
                    </span>
                  </div>
                  <div class="candidate-sub">
                    <span v-if="candidateAuthors(c)">{{ candidateAuthors(c) }}</span>
                    <span v-if="candidateNarrators(c)" class="muted">
                      · narr. {{ candidateNarrators(c) }}
                    </span>
                    <span v-if="candidateYear(c)" class="muted">· {{ candidateYear(c) }}</span>
                    <span v-if="c.asin" class="badge">{{ c.asin }}</span>
                    <span v-else class="badge badge-warn">No ASIN</span>
                  </div>
                </div>
              </li>
            </ul>
            <!-- The editable title/author form above already exposes a "Search
                 again" submit button, so no duplicate retry control is needed
                 here. -->
          </template>

          <!-- Phase 2: field comparison -->
          <template v-if="phase === 'review' || phase === 'applying'">
            <div class="review-header">
              <button
                v-if="!audiobook?.asin"
                class="btn btn-link"
                :disabled="phase === 'applying'"
                @click="backToCandidates"
              >
                <PhArrowLeft />
                Pick a different candidate
              </button>
              <button
                v-else
                class="btn btn-link"
                :disabled="phase === 'applying'"
                @click="backToCandidates"
                title="Search by title/author for a different match"
              >
                <PhMagnifyingGlass />
                Not this book? Search by title
              </button>
              <label class="select-all">
                <input
                  type="checkbox"
                  :checked="allSelected"
                  :disabled="selectableRows.length === 0 || phase === 'applying'"
                  @change="toggleAll"
                />
                Select all that differ
              </label>
            </div>

            <table class="compare-table">
              <thead>
                <tr>
                  <th class="col-pick"></th>
                  <th class="col-field">Field</th>
                  <th class="col-value">Current</th>
                  <th class="col-value">Fresh from Audible</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="row in rows"
                  :key="row.key"
                  :class="{ unchanged: row.unchanged, 'no-fresh': !row.selectable && !row.unchanged }"
                >
                  <td class="col-pick">
                    <input
                      v-if="row.selectable"
                      type="checkbox"
                      :checked="selected.has(row.key)"
                      :disabled="phase === 'applying'"
                      @change="toggleField(row.key)"
                    />
                    <PhCheckCircle v-else-if="row.unchanged" class="match-icon" />
                  </td>
                  <td class="col-field">{{ row.label }}</td>
                  <td class="col-value">
                    <img
                      v-if="row.key === 'imageUrl' && !isEmpty(row.current)"
                      :src="formatValue(row.current)"
                      class="value-thumb"
                      alt="current cover"
                      loading="lazy"
                    />
                    <span v-else-if="!isEmpty(row.current)">{{ formatValue(row.current) }}</span>
                    <span v-else class="muted">—</span>
                  </td>
                  <td class="col-value">
                    <img
                      v-if="row.key === 'imageUrl' && !isEmpty(row.fresh)"
                      :src="formatValue(row.fresh)"
                      class="value-thumb"
                      alt="fresh cover"
                      loading="lazy"
                    />
                    <span v-else-if="!isEmpty(row.fresh)">{{ formatValue(row.fresh) }}</span>
                    <span v-else class="muted">—</span>
                  </td>
                </tr>
              </tbody>
            </table>
          </template>
        </div>

        <footer v-if="phase === 'review' || phase === 'applying'" class="modal-footer">
          <button class="btn btn-secondary" :disabled="phase === 'applying'" @click="close">
            Cancel
          </button>
          <button
            class="btn btn-primary"
            :disabled="phase === 'applying' || selected.size === 0"
            @click="applyChanges"
          >
            <PhSpinner v-if="phase === 'applying'" class="ph-spin" />
            Apply {{ selected.size }} change{{ selected.size === 1 ? '' : 's' }}
          </button>
        </footer>
      </div>
    </div>
  </Teleport>

  <FilePreviewModal
    :visible="showPreview"
    :audiobook-id="audiobook?.id ?? null"
    :file="previewFile"
    :audiobook-title="audiobook?.title ?? null"
    :overlay-z-index="3200"
    @close="showPreview = false"
  />
</template>

<style scoped>
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.6);
  display: flex;
  align-items: center;
  justify-content: center;
  /* Above the shared Modal component (which uses z-index 3000) so this
     backfill modal isn't hidden behind the parent edit modal it's opened from. */
  z-index: 3100;
  padding: 1rem;
}

.modal {
  background: #1e1e1e;
  border: 1px solid #333;
  border-radius: 8px;
  width: 100%;
  max-width: 960px;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 1rem 1.25rem;
  border-bottom: 1px solid #333;
}

.modal-header h2 {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  margin: 0;
  color: #fff;
  font-size: 1.15rem;
}

.modal-close {
  background: transparent;
  border: none;
  color: #999;
  cursor: pointer;
  padding: 0.25rem;
  border-radius: 4px;
}
.modal-close:hover:not(:disabled) {
  color: #fff;
  background: rgba(255, 255, 255, 0.06);
}

.modal-body {
  padding: 1rem 1.25rem;
  overflow-y: auto;
  flex: 1;
}

.book-name {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 0.75rem;
  flex-wrap: wrap;
  margin: 0 0 1rem;
  color: #ddd;
}
.book-name-text {
  margin: 0;
}
.book-name strong {
  color: #fff;
}
.preview-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  flex-shrink: 0;
}
.muted {
  color: #888;
}

.status-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #ccc;
  padding: 0.75rem 0;
}
.status-row.error {
  color: #e74c3c;
}
.status-row.notice {
  color: #d4b04a;
  background: rgba(212, 176, 74, 0.08);
  border-radius: 4px;
  padding: 0.5rem 0.75rem;
}

.phase-help {
  color: #bbb;
  font-size: 0.9rem;
  margin: 0 0 0.75rem;
}

.narrator-hint {
  margin: 0 0 0.75rem;
  padding: 0.5rem 0.75rem;
  background: rgba(255, 255, 255, 0.04);
  border-radius: 4px;
  font-size: 0.9rem;
  color: #ddd;
}
.narrator-hint strong {
  color: #fff;
}
.narrator-hint-source {
  margin-left: 0.35rem;
  font-size: 0.85rem;
}
.narrator-hint-detail {
  display: block;
  margin-top: 0.15rem;
  font-size: 0.8rem;
}

.candidate-item--narrator-match {
  background: rgba(var(--brand-rgb), 0.08);
  box-shadow: inset 3px 0 0 var(--brand-400, #3b82f6);
}
.candidate-item--narrator-match:hover:not(.disabled) {
  background: rgba(var(--brand-rgb), 0.14);
}
.candidate-badge {
  display: inline-block;
  margin-left: 0.5rem;
  padding: 0.05rem 0.45rem;
  background: var(--brand-400, #3b82f6);
  color: #fff;
  border-radius: 10px;
  font-size: 0.7rem;
  font-weight: 600;
  vertical-align: middle;
  font-family: var(--font-family, sans-serif);
}

.candidate-list {
  list-style: none;
  margin: 0 0 0.75rem;
  padding: 0;
  border: 1px solid #333;
  border-radius: 6px;
  max-height: 420px;
  overflow-y: auto;
}

.candidate-item {
  display: grid;
  grid-template-columns: 56px 1fr;
  gap: 0.75rem;
  padding: 0.6rem 0.75rem;
  border-bottom: 1px solid #2a2a2a;
  cursor: pointer;
  transition: background 0.15s;
}
.candidate-item:last-child {
  border-bottom: none;
}
.candidate-item:hover:not(.disabled) {
  background: rgba(255, 255, 255, 0.04);
}
.candidate-item.disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.candidate-cover-slot {
  width: 56px;
  height: 56px;
  border-radius: 4px;
  background: #2a2a2a;
  overflow: hidden;
}
.candidate-cover-slot img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.candidate-meta {
  min-width: 0;
}
.candidate-title {
  color: #fff;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
}
.candidate-sub {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.4rem;
  color: #aaa;
  font-size: 0.85rem;
  margin-top: 0.2rem;
}
.badge {
  padding: 0.05rem 0.4rem;
  font-size: 0.7rem;
  background: rgba(var(--brand-rgb), 0.18);
  color: var(--brand-300);
  border-radius: 3px;
  font-family: ui-monospace, SFMono-Regular, monospace;
}
.badge-warn {
  background: rgba(255, 165, 0, 0.18);
  color: #ffa500;
}

.search-again-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
}

.candidate-search-form {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto;
  gap: 0.6rem 0.75rem;
  align-items: end;
  margin: 0.5rem 0 0.75rem;
}
.candidate-search-form .search-again-btn {
  align-self: end;
  height: 2.4rem;
}
.candidate-search-field {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  min-width: 0;
}
.candidate-search-label {
  font-size: 0.8rem;
  color: var(--text-muted, #888);
}
@media (max-width: 540px) {
  .candidate-search-form {
    grid-template-columns: 1fr;
  }
  .candidate-search-form .search-again-btn {
    justify-self: start;
  }
}

/* Paste-an-ASIN / Audible-URL escape hatch. Mirrors the candidate-search
   layout so the two forms read as a related pair, separated by a thin top
   border so the user understands it as an alternative input. */
.paste-asin-form {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 0.6rem 0.75rem;
  align-items: end;
  margin: 0 0 0.5rem;
  padding-top: 0.6rem;
  border-top: 1px solid var(--color-border, rgba(255, 255, 255, 0.08));
}
.paste-asin-field {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  min-width: 0;
}
.paste-asin-label {
  font-size: 0.8rem;
  color: var(--text-muted, #888);
}
.paste-asin-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  align-self: end;
  height: 2.4rem;
}
.paste-asin-error {
  color: #ff8a8a;
  font-size: 0.85rem;
  margin: 0.25rem 0 0.5rem;
}
@media (max-width: 540px) {
  .paste-asin-form {
    grid-template-columns: 1fr;
  }
  .paste-asin-btn {
    justify-self: start;
  }
}

.review-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.75rem;
  gap: 1rem;
  flex-wrap: wrap;
}
.btn-link {
  background: transparent;
  border: none;
  color: var(--brand-400);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  padding: 0.25rem 0;
  font-size: 0.9rem;
}
.btn-link:hover:not(:disabled) {
  color: var(--brand-300);
  text-decoration: underline;
}

.select-all {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  color: #ccc;
  font-size: 0.9rem;
  cursor: pointer;
}

.compare-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 0.9rem;
  /* Pin column widths so the browser's auto-layout doesn't crush one value
     column to ~1ch when the other contains a wider element (e.g. the 72px
     cover thumbnail on the imageUrl row). With auto layout + word-break,
     that crush manifests as text rendering one character per line. */
  table-layout: fixed;
}
.compare-table th,
.compare-table td {
  text-align: left;
  vertical-align: top;
  padding: 0.5rem 0.6rem;
  border-bottom: 1px solid #2a2a2a;
  color: #ddd;
}
.compare-table th {
  color: #888;
  font-size: 0.78rem;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  font-weight: 500;
  border-bottom-color: #333;
}
.col-pick {
  width: 36px;
}
.col-field {
  width: 130px;
  color: #bbb;
}
.col-value {
  /* Two value columns share the remaining width equally. */
  width: calc((100% - 166px) / 2);
  /* Wrap by word; only break inside words as a last resort so we don't go
     letter-per-line on long URLs. */
  overflow-wrap: anywhere;
  word-break: normal;
}
.compare-table tr.unchanged {
  opacity: 0.55;
}
.compare-table tr.no-fresh .col-value:last-child {
  color: #666;
}
.match-icon {
  color: #51cf66;
  font-size: 1rem;
}
.value-thumb {
  max-width: 72px;
  max-height: 72px;
  object-fit: cover;
  border-radius: 3px;
  background: #2a2a2a;
}

.modal-footer {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  padding: 0.75rem 1.25rem;
  border-top: 1px solid #333;
  background: #1a1a1a;
}

.btn {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
}

.ph-spin {
  animation: spin 1s linear infinite;
}
</style>
