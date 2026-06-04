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
  Lets the user correct a wrong/ambiguous series resolution. Fetches candidate
  series for the collection's name (owned-book-derived first, then name-search),
  pre-selects the best guess, and on confirm persists the choice via selectSeries
  so it sticks on later loads. Emits the resolved catalog back to the parent.
-->
<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { PhX, PhSpinner, PhCheckCircle, PhBooks } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { logger } from '@/utils/logger'
import type { SeriesCandidate, SeriesCatalogResponse } from '@/types'

interface Props {
  visible: boolean
  /** The series name as the collection page was reached by (the slug). */
  seriesName: string
  region?: string
  /** Currently-resolved series ASIN, marked in the list when present. */
  currentAsin?: string
}

interface Emits {
  (e: 'close'): void
  (e: 'selected', catalog: SeriesCatalogResponse): void
}

const props = withDefaults(defineProps<Props>(), { region: 'us', currentAsin: undefined })
const emit = defineEmits<Emits>()

const toast = useToast()

const loading = ref(false)
const error = ref<string | null>(null)
const candidates = ref<SeriesCandidate[]>([])
const selectedAsin = ref<string | null>(null)
const submitting = ref(false)
// Manual fallbacks: re-search Audible by a typed name, or paste a known series ASIN. These
// rescue the case where the page's slug doesn't resolve (e.g. a renamed/orphaned series) and
// the owned-book-derived candidates come back empty.
const searchQuery = ref('')
const manualAsin = ref('')

const hasCandidates = computed(() => candidates.value.length > 0)
const trimmedManualAsin = computed(() => manualAsin.value.trim())

function close() {
  if (submitting.value) return
  emit('close')
}

async function loadCandidates(query?: string) {
  const lookupName = (query ?? props.seriesName)?.trim()
  if (!lookupName) {
    error.value = 'Enter a series name to search for.'
    return
  }
  loading.value = true
  error.value = null
  candidates.value = []
  selectedAsin.value = null
  try {
    const result = await apiService.getSeriesCandidates(lookupName, props.region || 'us')
    if (!result || result.candidates.length === 0) {
      error.value =
        'No candidate series found. Try a different name above, or paste the Audible series ASIN below.'
      return
    }
    candidates.value = result.candidates
    // Pre-select the best guess, falling back to the first candidate.
    selectedAsin.value =
      result.bestGuessAsin ?? result.candidates[0]?.asin ?? null
  } catch (err) {
    logger.error('SeriesPickerModal: candidate fetch failed', err)
    error.value = err instanceof Error ? err.message : 'Failed to load candidate series.'
  } finally {
    loading.value = false
  }
}

function runSearch() {
  if (submitting.value || loading.value) return
  void loadCandidates(searchQuery.value)
}

async function applyAsin(asin: string) {
  const chosen = asin?.trim()
  if (!chosen || submitting.value) return
  submitting.value = true
  try {
    const catalog = await apiService.selectSeries(
      props.seriesName,
      chosen,
      props.region || 'us',
    )
    if (!catalog || !catalog.series?.asin) {
      toast.error('Could not load that series', 'Audible returned no catalog for the selected series.')
      return
    }
    toast.success('Series updated', `Now showing "${catalog.series.name}".`)
    emit('selected', catalog)
  } catch (err) {
    logger.error('SeriesPickerModal: select failed', err)
    toast.error('Failed to update series', err instanceof Error ? err.message : 'Unknown error')
  } finally {
    submitting.value = false
  }
}

function confirm() {
  if (!selectedAsin.value) return
  void applyAsin(selectedAsin.value)
}

function confirmManualAsin() {
  if (!trimmedManualAsin.value) return
  void applyAsin(trimmedManualAsin.value)
}

function badgeLabel(candidate: SeriesCandidate): string {
  if (candidate.source === 'library') {
    return candidate.ownedMatchCount > 1
      ? `From ${candidate.ownedMatchCount} of your books`
      : 'From a book you own'
  }
  return 'Audible search match'
}

watch(
  () => props.visible,
  (visible) => {
    if (visible) {
      searchQuery.value = props.seriesName ?? ''
      manualAsin.value = ''
      void loadCandidates()
    }
  },
  { immediate: true },
)
</script>

<template>
  <transition name="modal-fade">
    <div v-if="visible" class="modal-overlay" @click.self="close">
      <div class="modal modal-md" role="dialog" aria-modal="true">
        <header class="modal-header">
          <h2>Pick the correct series</h2>
          <button class="modal-close" :disabled="submitting" @click="close" aria-label="Close">
            <PhX />
          </button>
        </header>

        <div class="modal-body">
          <p class="picker-intro">
            Choose the right series for
            <strong>{{ seriesName }}</strong
            >. Matches derived from books you already own are listed first.
          </p>

          <div class="picker-search">
            <input
              v-model="searchQuery"
              class="picker-input"
              type="text"
              placeholder="Search Audible by series name…"
              :disabled="submitting"
              @keyup.enter="runSearch"
            />
            <button class="btn" :disabled="loading || submitting" @click="runSearch">
              Search
            </button>
          </div>

          <div v-if="loading" class="picker-state">
            <PhSpinner class="spin" :size="22" />
            <span>Finding candidate series…</span>
          </div>

          <div v-else-if="error" class="picker-state picker-error">{{ error }}</div>

          <ul v-else-if="hasCandidates" class="candidate-list">
            <li v-for="candidate in candidates" :key="candidate.asin">
              <label class="candidate" :class="{ selected: selectedAsin === candidate.asin }">
                <input
                  type="radio"
                  name="series-candidate"
                  :value="candidate.asin"
                  v-model="selectedAsin"
                />
                <img
                  v-if="candidate.image"
                  :src="candidate.image"
                  :alt="`${candidate.name} cover`"
                  class="candidate-cover"
                  loading="lazy"
                />
                <div v-else class="candidate-cover candidate-cover-placeholder">
                  <PhBooks :size="22" />
                </div>
                <div class="candidate-info">
                  <span class="candidate-name">{{ candidate.name || 'Unknown series' }}</span>
                  <span class="candidate-meta">
                    <span class="candidate-badge" :class="`is-${candidate.source}`">{{
                      badgeLabel(candidate)
                    }}</span>
                    <span v-if="candidate.asin === currentAsin" class="candidate-current"
                      >currently shown</span
                    >
                    <span class="candidate-asin">ASIN {{ candidate.asin }}</span>
                  </span>
                </div>
                <PhCheckCircle
                  v-if="selectedAsin === candidate.asin"
                  class="candidate-check"
                  :size="20"
                />
              </label>
            </li>
          </ul>

          <div class="picker-manual">
            <label class="picker-manual-label" for="series-manual-asin">
              Know the Audible series ASIN? Paste it to use it directly:
            </label>
            <div class="picker-manual-row">
              <input
                id="series-manual-asin"
                v-model="manualAsin"
                class="picker-input"
                type="text"
                placeholder="e.g. B07YCKVJJT"
                :disabled="submitting"
                @keyup.enter="confirmManualAsin"
              />
              <button
                class="btn btn-primary"
                :disabled="!trimmedManualAsin || submitting"
                @click="confirmManualAsin"
              >
                <PhSpinner v-if="submitting" class="spin" :size="16" />
                <span>Use this ASIN</span>
              </button>
            </div>
          </div>
        </div>

        <footer class="modal-footer">
          <button class="btn" :disabled="submitting" @click="close">Cancel</button>
          <button
            class="btn btn-primary"
            :disabled="!selectedAsin || submitting"
            @click="confirm"
          >
            <PhSpinner v-if="submitting" class="spin" :size="16" />
            <span>{{ submitting ? 'Applying…' : 'Use this series' }}</span>
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
  background: var(--color-surface, #1e1e1e);
  border-radius: 12px;
  width: 100%;
  max-width: 640px;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  box-shadow: 0 12px 40px rgba(0, 0, 0, 0.45);
}

.modal-header,
.modal-footer {
  display: flex;
  align-items: center;
  padding: 1rem 1.25rem;
}

.modal-header {
  justify-content: space-between;
  border-bottom: 1px solid var(--color-border, #333);
}

.modal-header h2 {
  margin: 0;
  font-size: 1.15rem;
}

.modal-close {
  background: none;
  border: none;
  color: inherit;
  cursor: pointer;
  padding: 0.25rem;
  border-radius: 6px;
}

.modal-close:hover:not(:disabled) {
  background: var(--color-surface-hover, #2a2a2a);
}

.modal-body {
  padding: 1rem 1.25rem;
  overflow-y: auto;
}

.modal-footer {
  justify-content: flex-end;
  gap: 0.5rem;
  border-top: 1px solid var(--color-border, #333);
}

.picker-intro {
  margin: 0 0 0.75rem;
  color: var(--color-text-secondary, #bbb);
}

.picker-state {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 1rem 0;
  color: var(--color-text-secondary, #bbb);
}

.picker-error {
  color: var(--color-danger, #e57373);
}

.picker-search {
  display: flex;
  gap: 0.5rem;
  margin-bottom: 0.75rem;
}

.picker-input {
  flex: 1;
  min-width: 0;
  padding: 0.45rem 0.6rem;
  background: var(--color-surface-alt, #2a2a2a);
  border: 1px solid var(--color-border, #333);
  border-radius: 6px;
  color: inherit;
  font-size: 0.9rem;
}

.picker-input:focus {
  outline: none;
  border-color: var(--color-primary, #2196f3);
}

.picker-manual {
  margin-top: 1rem;
  padding-top: 0.85rem;
  border-top: 1px solid var(--color-border, #333);
}

.picker-manual-label {
  display: block;
  margin-bottom: 0.4rem;
  font-size: 0.82rem;
  color: var(--color-text-secondary, #bbb);
}

.picker-manual-row {
  display: flex;
  gap: 0.5rem;
}

.candidate-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.candidate {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.5rem 0.75rem;
  border: 1px solid var(--color-border, #333);
  border-radius: 8px;
  cursor: pointer;
}

.candidate:hover {
  background: var(--color-surface-hover, #2a2a2a);
}

.candidate.selected {
  border-color: var(--color-primary, #2196f3);
  background: var(--color-surface-hover, #2a2a2a);
}

.candidate input[type='radio'] {
  flex: none;
}

.candidate-cover {
  width: 44px;
  height: 44px;
  object-fit: cover;
  border-radius: 4px;
  flex: none;
}

.candidate-cover-placeholder {
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--color-surface-alt, #2a2a2a);
  color: var(--color-text-secondary, #888);
}

.candidate-info {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
  min-width: 0;
  flex: 1;
}

.candidate-name {
  font-weight: 600;
}

.candidate-meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.8rem;
  color: var(--color-text-secondary, #999);
}

.candidate-badge {
  padding: 0.05rem 0.4rem;
  border-radius: 999px;
  font-size: 0.72rem;
}

.candidate-badge.is-library {
  background: rgba(76, 175, 80, 0.18);
  color: #81c784;
}

.candidate-badge.is-audible {
  background: rgba(33, 150, 243, 0.18);
  color: #64b5f6;
}

.candidate-current {
  font-style: italic;
}

.candidate-check {
  color: var(--color-primary, #2196f3);
  flex: none;
}

.spin {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
