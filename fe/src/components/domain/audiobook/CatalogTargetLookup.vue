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
  "The right book isn't in my library yet" escape hatch for destination
  pickers (move-files, split-collection): search the online catalog by title
  and author, add the picked edition as a new library record, and hand its id
  back to the parent to use as the destination (live case: splitting a
  Witch & Wizard collection, files tagged "04 The Kiss" had no destination
  because The Kiss was never added).

  Two guards keep it from creating duplicates: records already in the library
  whose title matches what was typed are offered first ("Select" instead of
  "Add"), and when the add itself comes back 409 "already exists" the existing
  record from the response is selected rather than surfacing the error (live
  case: splitting Mavericks, "specops" was typed here while SpecOps id 40 sat
  in the library; the add failed with a wall of JSON instead of picking it).
-->
<template>
  <div class="catalog-lookup">
    <button v-if="!open" type="button" class="catalog-toggle" @click="expand">
      Not in your library? Search the catalog…
    </button>
    <div v-else class="catalog-body">
      <div class="catalog-inputs">
        <input v-model="title" type="text" class="catalog-input" placeholder="Title" />
        <input v-model="author" type="text" class="catalog-input" placeholder="Author" />
        <button
          type="button"
          class="catalog-search-btn"
          :disabled="searching || !title.trim()"
          @click="search"
        >
          {{ searching ? 'Searching…' : 'Search' }}
        </button>
      </div>
      <div v-if="libraryMatches.length > 0" class="catalog-results catalog-library">
        <div class="catalog-section-label">Already in your library</div>
        <div v-for="b in libraryMatches" :key="b.id" class="catalog-result">
          <span class="catalog-result-text">
            <strong>{{ b.title }}</strong>
            <small>
              id {{ b.id }}
              <template v-if="(b.authors || []).length">
                · {{ (b.authors || []).slice(0, 2).join(', ') }}</template
              >
              · {{ b.fileCount > 0 ? `${b.fileCount} file(s)` : 'empty' }}
            </small>
          </span>
          <button
            type="button"
            class="catalog-add-btn catalog-select-btn"
            :disabled="addingAsin !== null"
            @click="selectExisting(b.record)"
          >
            Select
          </button>
        </div>
      </div>
      <div v-if="error" class="catalog-error">{{ error }}</div>
      <div v-if="searched && !searching && results.length === 0" class="catalog-empty">
        Nothing found in the catalog.
      </div>
      <div v-if="results.length > 0" class="catalog-results">
        <div v-for="r in results" :key="r.asin" class="catalog-result">
          <span class="catalog-result-text">
            <strong>{{ r.title }}</strong>
            <small>
              <template v-if="r.subtitle">{{ r.subtitle }} · </template>
              {{
                (r.authors || [])
                  .map((a) => a?.name)
                  .filter(Boolean)
                  .slice(0, 2)
                  .join(', ')
              }}
              <template v-if="firstNarrator(r)"> · read by {{ firstNarrator(r) }}</template>
              <template v-if="r.lengthMinutes"> · {{ Math.round(r.lengthMinutes / 60) }}h</template>
            </small>
          </span>
          <button
            type="button"
            class="catalog-add-btn"
            :disabled="addingAsin !== null"
            @click="addAndSelect(r)"
          >
            {{ addingAsin === r.asin ? 'Adding…' : 'Add & select' }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { useLibraryStore } from '@/stores/library'
import type { Audiobook, AudibleBookMetadata, AudibleSearchResult } from '@/types'

const props = defineProps<{
  defaultTitle?: string | null
  defaultAuthor?: string | null
  /** The record being split/moved from — never offered as its own destination. */
  excludeId?: number | null
}>()

const emit = defineEmits<{
  /** A brand-new record was added to the library and should become the destination. */
  (e: 'added', audiobook: Audiobook): void
  /** An existing library record should become the destination (no add happened). */
  (e: 'selected', audiobook: Audiobook): void
}>()

const toast = useToast()
const libraryStore = useLibraryStore()

const STOPWORDS = new Set(['the', 'a', 'an', 'of', 'and', 'in', 'on', 'to', 'book', 'novel'])

function tokenize(text: string): string[] {
  return (text || '')
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter((t) => (t.length > 1 || /^\d$/.test(t)) && !STOPWORDS.has(t))
}

/**
 * Library records whose title shares tokens with the typed title, best first.
 * "specops" finds "SpecOps"; "spec ops" finds it too via the squashed form.
 */
const libraryMatches = computed(() => {
  const raw = title.value.trim()
  if (!open.value || raw.length < 2) return []
  const tokens = tokenize(raw)
  const squashed = raw.toLowerCase().replace(/[^a-z0-9]+/g, '')
  if (tokens.length === 0 && squashed.length < 3) return []
  return libraryStore.audiobooks
    .filter((b) => b.id !== props.excludeId)
    .map((b) => {
      const bookTitle = b.title || ''
      const titleTokens = new Set(tokenize(bookTitle))
      const bookSquashed = bookTitle.toLowerCase().replace(/[^a-z0-9]+/g, '')
      let score = tokens.reduce(
        (s, t) => s + (titleTokens.has(t) ? (/^\d+$/.test(t) ? 2 : 1) : 0),
        0,
      )
      if (squashed.length >= 3 && bookSquashed === squashed) score += 3
      else if (squashed.length >= 4 && bookSquashed.includes(squashed)) score += 1
      return {
        id: b.id,
        title: bookTitle,
        authors: b.authors,
        fileCount: b.fileCount ?? b.files?.length ?? 0,
        score,
        record: b,
      }
    })
    .filter((x) => x.score > 0)
    .sort((a, b) => b.score - a.score || a.title.localeCompare(b.title))
    .slice(0, 5)
})

function selectExisting(book: Audiobook) {
  emit('selected', book)
}

/** Pull `{ message, audiobook }` out of an API error body when the server sent JSON. */
function parseErrorBody(err: unknown): { message?: string; audiobook?: Audiobook } | null {
  const body = (err as { body?: unknown })?.body
  if (typeof body !== 'string' || body.length === 0) return null
  try {
    const parsed = JSON.parse(body) as { message?: string; audiobook?: Audiobook }
    return parsed && typeof parsed === 'object' ? parsed : null
  } catch {
    return null
  }
}

const open = ref(false)
const title = ref('')
const author = ref('')
const searching = ref(false)
const searched = ref(false)
const error = ref<string | null>(null)
const results = ref<AudibleSearchResult[]>([])
const addingAsin = ref<string | null>(null)

const REGIONS = ['us', 'uk', 'ca', 'au']

function expand() {
  open.value = true
  title.value = (props.defaultTitle || '').trim()
  author.value = (props.defaultAuthor || '').trim()
  if (libraryStore.audiobooks.length === 0) {
    void libraryStore.fetchLibrary().catch(() => {})
  }
}

function firstNarrator(r: AudibleSearchResult): string | null {
  const name = (r.narrators || []).map((n) => n?.name).filter(Boolean)[0]
  return name || null
}

async function search() {
  searching.value = true
  error.value = null
  results.value = []
  try {
    for (const region of REGIONS) {
      const resp = await apiService.searchAudibleByTitleAndAuthor(
        title.value.trim(),
        author.value.trim(),
        1,
        20,
        region,
      )
      const found = (resp?.results ?? []).filter((r) => r.asin)
      if (found.length > 0) {
        results.value = found.slice(0, 8)
        break
      }
    }
    searched.value = true
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Catalog search failed.'
  } finally {
    searching.value = false
  }
}

async function addAndSelect(candidate: AudibleSearchResult) {
  if (!candidate.asin) return
  addingAsin.value = candidate.asin
  try {
    const metadata = {
      asin: candidate.asin,
      title: candidate.title || '',
      subtitle: candidate.subtitle,
      authors: (candidate.authors || []).map((a) => a?.name).filter(Boolean) as string[],
      narrators: (candidate.narrators || []).map((n) => n?.name).filter(Boolean) as string[],
      imageUrl: candidate.imageUrl,
      language: candidate.language,
      runtime: candidate.lengthMinutes,
      releaseDate: candidate.releaseDate,
    } as AudibleBookMetadata
    const result = await apiService.addToLibrary(metadata, { monitored: true, autoSearch: false })
    toast.success('Record added', `"${result.audiobook.title}" is now in your library.`)
    emit('added', result.audiobook)
  } catch (err) {
    const status = (err as { status?: number })?.status
    const body = parseErrorBody(err)
    if (status === 409 && body?.audiobook?.id) {
      // The library already has this edition: use it rather than failing.
      const existing = body.audiobook
      toast.info(
        'Already in your library',
        `Selected the existing record "${existing.title || `id ${existing.id}`}".`,
      )
      emit('selected', existing)
      return
    }
    const detail =
      body?.message ||
      (err instanceof Error ? err.message.replace(/^API error: \d+\s*/, '') : '') ||
      'unknown error'
    toast.error('Add failed', detail.length > 300 ? `${detail.slice(0, 300)}…` : detail)
  } finally {
    addingAsin.value = null
  }
}
</script>

<style scoped>
.catalog-section-label {
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  opacity: 0.7;
  margin-bottom: 0.25rem;
}
.catalog-library {
  margin-bottom: 0.5rem;
}

.catalog-lookup {
  margin-top: 0.5rem;
}

.catalog-toggle {
  background: transparent;
  border: none;
  color: #4dabf7;
  font-size: 0.85rem;
  cursor: pointer;
  padding: 0;
}

.catalog-toggle:hover {
  text-decoration: underline;
}

.catalog-inputs {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.catalog-input {
  flex: 1 1 140px;
  padding: 0.45rem 0.6rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.9rem;
}

.catalog-search-btn {
  padding: 0.45rem 0.9rem;
  background-color: rgba(var(--brand-rgb), 0.1);
  border: 1px solid var(--brand-500);
  border-radius: 6px;
  color: var(--brand-500);
  cursor: pointer;
}

.catalog-search-btn:disabled,
.catalog-add-btn:disabled {
  opacity: 0.6;
  cursor: default;
}

.catalog-error {
  margin-top: 0.4rem;
  color: #e74c3c;
  font-size: 0.85rem;
}

.catalog-empty {
  margin-top: 0.4rem;
  color: #8a93a0;
  font-size: 0.85rem;
}

.catalog-results {
  margin-top: 0.5rem;
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  max-height: 220px;
  overflow-y: auto;
}

.catalog-result {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.6rem;
  padding: 0.45rem 0.6rem;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
}

.catalog-result-text {
  display: flex;
  flex-direction: column;
}

.catalog-result-text small {
  color: #8a93a0;
}

.catalog-add-btn {
  padding: 2px 10px;
  font-size: 0.8rem;
  background: rgba(46, 204, 113, 0.1);
  border: 1px solid rgba(46, 204, 113, 0.4);
  border-radius: 4px;
  color: #2ecc71;
  cursor: pointer;
  white-space: nowrap;
}
</style>
