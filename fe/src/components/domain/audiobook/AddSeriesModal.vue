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
  Bulk-adds every book in a series. Fetches the catalog via getSeriesCatalog,
  marks books the user already owns (by ASIN), and adds the rest with shared
  options (root folder, quality profile, monitored, auto-search). Optionally
  also monitors the series itself so future releases are picked up by automatic
  search.
-->
<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { PhPlus, PhX, PhCheckCircle, PhSpinner, PhWarning } from '@phosphor-icons/vue'
import RootFolderSelect from '@/components/form/RootFolderSelect.vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { useConfigurationStore } from '@/stores/configuration'
import { useLibraryStore } from '@/stores/library'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { logger } from '@/utils/logger'
import type {
  Audiobook,
  AudibleBookMetadata,
  QualityProfile,
  SeriesCatalogBook,
} from '@/types'

interface Props {
  visible: boolean
  seriesName: string
  seriesAsin?: string
  region?: string
}

interface Emits {
  (e: 'close'): void
  (e: 'added', summary: { addedCount: number; failedCount: number; addedBooks: Audiobook[] }): void
}

const props = withDefaults(defineProps<Props>(), {
  seriesAsin: undefined,
  region: 'us',
})
const emit = defineEmits<Emits>()

const toast = useToast()
const configStore = useConfigurationStore()
const libraryStore = useLibraryStore()
const rootStore = useRootFoldersStore()

// ── State ──────────────────────────────────────────────────────────────────

const loadingCatalog = ref(false)
const catalogError = ref<string | null>(null)
const catalog = ref<SeriesCatalogBook[]>([])
const selectedAsins = ref<Set<string>>(new Set())

const qualityProfiles = ref<QualityProfile[]>([])
const selectedRootId = ref<number | null>(null)
const customRootPath = ref('')
const monitored = ref(true)
const autoSearch = ref(true)
const monitorSeriesAfter = ref(true)
const qualityProfileId = ref<number | null>(null)

const adding = ref(false)
const progress = ref({ added: 0, failed: 0, total: 0 })

// ── Derived ────────────────────────────────────────────────────────────────

// Books already in the library (matched by ASIN, case-insensitive). Excluded
// from the default selection so we don't try to add duplicates.
const ownedAsins = computed(() => {
  const set = new Set<string>()
  for (const book of libraryStore.audiobooks || []) {
    if (book.asin) set.add(book.asin.toUpperCase())
  }
  return set
})

function isOwned(book: SeriesCatalogBook): boolean {
  return !!book.asin && ownedAsins.value.has(book.asin.toUpperCase())
}

const addableBooks = computed(() => catalog.value.filter((b) => !!b.asin && !isOwned(b)))
const ownedCount = computed(() => catalog.value.filter(isOwned).length)
const selectedCount = computed(() => selectedAsins.value.size)
const allAddableSelected = computed(
  () =>
    addableBooks.value.length > 0 &&
    addableBooks.value.every((b) => selectedAsins.value.has(b.asin!)),
)

// ── Load on open ───────────────────────────────────────────────────────────

watch(
  () => props.visible,
  async (visible) => {
    if (!visible) return
    resetForm()
    await Promise.all([loadCatalog(), loadConfigDefaults()])
  },
  { immediate: false },
)

async function loadCatalog() {
  loadingCatalog.value = true
  catalogError.value = null
  catalog.value = []
  try {
    const result = await apiService.getSeriesCatalog(props.seriesName, props.region || 'us')
    if (!result || !result.books?.length) {
      catalogError.value =
        'No catalog returned for this series. Audible may not have an entry for it.'
      return
    }
    catalog.value = result.books
    // Default selection: every catalog book the user doesn't already own.
    const defaults = new Set<string>()
    for (const book of result.books) {
      if (book.asin && !isOwned(book)) defaults.add(book.asin)
    }
    selectedAsins.value = defaults
  } catch (err) {
    logger.error('AddSeriesModal: catalog fetch failed', err)
    catalogError.value =
      err instanceof Error ? err.message : 'Failed to load the series catalog.'
  } finally {
    loadingCatalog.value = false
  }
}

async function loadConfigDefaults() {
  try {
    if (!configStore.qualityProfiles?.length) {
      await configStore.loadQualityProfiles()
    }
    qualityProfiles.value = configStore.qualityProfiles || []
    const defaultProfile = qualityProfiles.value.find((p) => p.isDefault)
    qualityProfileId.value = defaultProfile?.id ?? null

    if (!rootStore.folders?.length) {
      await rootStore.load()
    }
    const defaultRoot = rootStore.folders.find((f) => f.isDefault)
    selectedRootId.value = defaultRoot?.id ?? null
  } catch (err) {
    logger.error('AddSeriesModal: failed to load defaults', err)
  }
}

function resetForm() {
  catalog.value = []
  selectedAsins.value = new Set()
  catalogError.value = null
  adding.value = false
  progress.value = { added: 0, failed: 0, total: 0 }
}

// ── Selection helpers ──────────────────────────────────────────────────────

function toggleBook(asin: string) {
  if (selectedAsins.value.has(asin)) {
    selectedAsins.value.delete(asin)
  } else {
    selectedAsins.value.add(asin)
  }
  // Reassign so reactivity fires for the Set.
  selectedAsins.value = new Set(selectedAsins.value)
}

function toggleAllAddable() {
  if (allAddableSelected.value) {
    selectedAsins.value = new Set()
  } else {
    selectedAsins.value = new Set(addableBooks.value.map((b) => b.asin!))
  }
}

// ── Submit ─────────────────────────────────────────────────────────────────

function buildMetadata(book: SeriesCatalogBook): AudibleBookMetadata {
  return {
    asin: book.asin!,
    title: book.title,
    subtitle: book.subtitle,
    authors: book.authors || [],
    publishedDate: book.publishedDate,
    series: book.series,
    seriesNumber: book.seriesNumber,
    seriesAsin: props.seriesAsin,
    description: undefined,
    genres: book.genres,
    narrators: book.narrators,
    isbn: book.isbn,
    publisher: book.publisher,
    language: book.language,
    runtime: book.runtime,
    imageUrl: book.imageUrl,
    metadataSource: book.metadataSource,
  }
}

function resolveDestinationPath(): string | undefined {
  if (selectedRootId.value === 0) {
    return customRootPath.value.trim() || undefined
  }
  if (selectedRootId.value && selectedRootId.value > 0) {
    const found = rootStore.folders.find((f) => f.id === selectedRootId.value)
    return found?.path || undefined
  }
  return undefined
}

async function handleSubmit() {
  const targets = addableBooks.value.filter((b) => selectedAsins.value.has(b.asin!))
  if (targets.length === 0) {
    toast.warning('Nothing to add', 'No books are selected.')
    return
  }

  adding.value = true
  progress.value = { added: 0, failed: 0, total: targets.length }
  const destination = resolveDestinationPath()
  const addedBooks: Audiobook[] = []

  for (const book of targets) {
    try {
      const result = await apiService.addToLibrary(buildMetadata(book), {
        monitored: monitored.value,
        qualityProfileId: qualityProfileId.value ?? undefined,
        autoSearch: autoSearch.value,
        destinationPath: destination,
      })
      addedBooks.push(result.audiobook)
      progress.value.added++
    } catch (err) {
      logger.error(`AddSeriesModal: failed to add "${book.title}"`, err)
      progress.value.failed++
    }
  }

  // Optionally enable series-level monitoring so future entries auto-search.
  if (monitorSeriesAfter.value) {
    try {
      await apiService.monitorSeries({
        name: props.seriesName,
        asin: props.seriesAsin,
        region: props.region || 'us',
      })
    } catch (err) {
      logger.warn('AddSeriesModal: monitorSeries call failed', err)
    }
  }

  adding.value = false
  const { added, failed } = progress.value
  if (failed === 0) {
    toast.success('Series added', `Added ${added} book${added === 1 ? '' : 's'} to your library.`)
  } else if (added > 0) {
    toast.warning(
      'Series partially added',
      `${added} succeeded, ${failed} failed. Check the logs for details.`,
    )
  } else {
    toast.error('Series add failed', `All ${failed} adds failed. Check the logs for details.`)
  }
  emit('added', { addedCount: added, failedCount: failed, addedBooks })
  if (failed === 0) close()
}

function close() {
  if (adding.value) return
  emit('close')
}
</script>

<template>
  <transition name="modal-fade">
    <div v-if="visible" class="modal-overlay" @click.self="close">
      <div class="modal modal-md" role="dialog" aria-modal="true">
        <header class="modal-header">
          <h2>Add series</h2>
          <button class="modal-close" :disabled="adding" @click="close" aria-label="Close">
            <PhX />
          </button>
        </header>

        <div class="modal-body">
          <p class="series-name">
            <strong>{{ seriesName }}</strong>
            <span v-if="seriesAsin" class="series-asin">{{ seriesAsin }}</span>
          </p>

          <!-- Catalog list -->
          <div class="catalog-section">
            <div v-if="loadingCatalog" class="status-row">
              <PhSpinner class="ph-spin" />
              <span>Loading catalog from Audible…</span>
            </div>

            <div v-else-if="catalogError" class="status-row error">
              <PhWarning />
              <span>{{ catalogError }}</span>
            </div>

            <template v-else-if="catalog.length">
              <div class="catalog-header">
                <label class="select-all">
                  <input
                    type="checkbox"
                    :checked="allAddableSelected"
                    :disabled="addableBooks.length === 0"
                    @change="toggleAllAddable"
                  />
                  <span>
                    Select all
                    <span class="muted"
                      >({{ addableBooks.length }} addable, {{ ownedCount }} already in library)</span
                    >
                  </span>
                </label>
              </div>
              <ul class="catalog-list">
                <li v-for="book in catalog" :key="book.asin || book.title" class="catalog-item">
                  <input
                    v-if="book.asin && !isOwned(book)"
                    type="checkbox"
                    :checked="selectedAsins.has(book.asin)"
                    @change="toggleBook(book.asin)"
                  />
                  <span v-else class="checkbox-placeholder" aria-hidden="true">
                    <PhCheckCircle v-if="isOwned(book)" class="owned-icon" />
                  </span>
                  <img
                    v-if="book.imageUrl"
                    :src="book.imageUrl"
                    :alt="book.title"
                    class="catalog-cover"
                    loading="lazy"
                  />
                  <div class="catalog-meta">
                    <div class="catalog-title">
                      <span class="catalog-pos" v-if="book.seriesNumber">#{{ book.seriesNumber }}</span>
                      {{ book.title }}
                    </div>
                    <div class="catalog-sub">
                      <span v-if="book.authors?.length">{{ book.authors.join(', ') }}</span>
                      <span v-if="isOwned(book)" class="badge badge-owned">Already in library</span>
                      <span v-else-if="!book.asin" class="badge badge-skip">No ASIN — skipped</span>
                    </div>
                  </div>
                </li>
              </ul>
            </template>
          </div>

          <!-- Shared options -->
          <div class="options-section">
            <div class="option-group">
              <label class="form-label">Root folder</label>
              <RootFolderSelect
                v-model:rootId="selectedRootId"
                v-model:customPath="customRootPath"
                hideLabel
              />
            </div>

            <div class="option-group">
              <label class="form-label">Quality profile</label>
              <select v-model="qualityProfileId" class="form-select">
                <option :value="null">Use default profile</option>
                <option v-for="p in qualityProfiles" :key="p.id" :value="p.id">
                  {{ p.name }}{{ p.isDefault ? ' (Default)' : '' }}
                </option>
              </select>
            </div>

            <label class="checkbox-row">
              <input type="checkbox" v-model="monitored" />
              <span>Monitor each book (search for files automatically)</span>
            </label>
            <label class="checkbox-row">
              <input type="checkbox" v-model="autoSearch" />
              <span>Trigger an initial search after adding</span>
            </label>
            <label class="checkbox-row">
              <input type="checkbox" v-model="monitorSeriesAfter" />
              <span>Monitor the series itself (pick up future releases)</span>
            </label>
          </div>

          <!-- Progress -->
          <div v-if="adding || progress.added || progress.failed" class="progress-row">
            <PhSpinner v-if="adding" class="ph-spin" />
            <PhCheckCircle v-else class="success" />
            <span>
              {{ progress.added }} added, {{ progress.failed }} failed of {{ progress.total }}
            </span>
          </div>
        </div>

        <footer class="modal-footer">
          <button class="btn btn-secondary" :disabled="adding" @click="close">Cancel</button>
          <button
            class="btn btn-primary"
            :disabled="adding || selectedCount === 0 || loadingCatalog"
            @click="handleSubmit"
          >
            <PhPlus />
            Add {{ selectedCount }} book{{ selectedCount === 1 ? '' : 's' }}
          </button>
        </footer>
      </div>
    </div>
  </transition>
</template>

<style scoped>
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.6);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  padding: 1rem;
}

.modal {
  background: #1e1e1e;
  border: 1px solid #333;
  border-radius: 8px;
  width: 100%;
  max-width: 720px;
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

.series-name {
  margin: 0 0 1rem;
  color: #ddd;
}

.series-name strong {
  color: #fff;
}

.series-asin {
  color: #777;
  font-size: 0.8rem;
  margin-left: 0.5rem;
}

.catalog-section {
  margin-bottom: 1.5rem;
}

.status-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #ccc;
  padding: 1rem;
}

.status-row.error {
  color: #e74c3c;
}

.catalog-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.5rem;
}

.select-all {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  color: #ddd;
  cursor: pointer;
  font-size: 0.9rem;
}

.muted {
  color: #888;
  font-size: 0.8rem;
}

.catalog-list {
  list-style: none;
  padding: 0;
  margin: 0;
  border: 1px solid #333;
  border-radius: 6px;
  max-height: 320px;
  overflow-y: auto;
}

.catalog-item {
  display: grid;
  grid-template-columns: 24px 48px 1fr;
  align-items: center;
  gap: 0.75rem;
  padding: 0.5rem 0.75rem;
  border-bottom: 1px solid #2a2a2a;
}

.catalog-item:last-child {
  border-bottom: none;
}

.checkbox-placeholder {
  display: inline-flex;
  justify-content: center;
  color: #51cf66;
}

.owned-icon {
  font-size: 1.1rem;
}

.catalog-cover {
  width: 48px;
  height: 48px;
  object-fit: cover;
  border-radius: 3px;
  background: #2a2a2a;
}

.catalog-meta {
  min-width: 0;
}

.catalog-title {
  color: #fff;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
}

.catalog-pos {
  color: #999;
  font-weight: 400;
  margin-right: 0.4rem;
}

.catalog-sub {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem;
  color: #999;
  font-size: 0.85rem;
  margin-top: 0.2rem;
}

.badge {
  padding: 0.1rem 0.5rem;
  border-radius: 4px;
  font-size: 0.75rem;
}

.badge-owned {
  background: rgba(81, 207, 102, 0.15);
  color: #51cf66;
  border: 1px solid rgba(81, 207, 102, 0.3);
}

.badge-skip {
  background: rgba(255, 255, 255, 0.06);
  color: #888;
  border: 1px solid #333;
}

.options-section {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
  padding-top: 0.5rem;
  border-top: 1px solid #2a2a2a;
}

.option-group {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.form-label {
  color: #bbb;
  font-size: 0.85rem;
}

.form-select {
  background: #1a1a1a;
  border: 1px solid #333;
  border-radius: 4px;
  color: #fff;
  padding: 0.5rem;
}

.checkbox-row {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  color: #ddd;
  cursor: pointer;
  font-size: 0.9rem;
}

.progress-row {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  margin-top: 1rem;
  padding: 0.5rem 0.75rem;
  background: rgba(var(--brand-rgb), 0.1);
  border: 1px solid rgba(var(--brand-rgb), 0.25);
  border-radius: 4px;
  color: #ddd;
}

.progress-row .success {
  color: #51cf66;
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
