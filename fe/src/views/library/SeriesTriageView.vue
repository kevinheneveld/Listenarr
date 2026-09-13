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
  <div class="series-triage-view">
    <div class="page-header">
      <div class="page-title">
        <h1>
          <PhListChecks />
          Series you're not collecting
        </h1>
        <p v-if="summary" class="page-summary" data-testid="triage-summary">
          {{ summary.candidates }} series · {{ summary.ownedBooksInCandidates }} owned books ·
          {{ summary.singleBook }} single-book
          <span v-if="summary.dismissed > 0" class="muted"
            >· {{ summary.dismissed }} marked not interested</span
          >
        </p>
        <p v-else class="page-summary muted">
          Series you own books from but aren't monitoring. Collect the rest, or mark them not
          interested.
        </p>
      </div>
      <div class="triage-actions">
        <div class="filter-input-wrapper">
          <PhMagnifyingGlass class="filter-icon" />
          <input
            v-model="filterText"
            type="text"
            class="filter-input"
            placeholder="Filter by series or author..."
            data-testid="triage-filter"
          />
          <button v-if="filterText" class="filter-clear" @click="filterText = ''">
            <PhX />
          </button>
        </div>
        <label class="toggle">
          <input v-model="hideSingle" type="checkbox" data-testid="triage-hide-single" />
          <span>Hide single-book series</span>
        </label>
        <label class="toggle">
          <input v-model="showDismissed" type="checkbox" data-testid="triage-show-dismissed" />
          <span>Show not interested</span>
        </label>
        <select v-model="sortBy" class="sort-select" aria-label="Sort series">
          <option value="owned">Most owned first</option>
          <option value="completion">Closest to complete</option>
          <option value="name">Name A–Z</option>
        </select>
      </div>
    </div>

    <div v-if="selectedCount > 0" class="bulk-bar" data-testid="triage-bulk-bar">
      <span>{{ selectedCount }} selected</span>
      <button
        class="btn btn-secondary"
        :disabled="bulkBusy"
        data-testid="triage-bulk-dismiss"
        @click="dismissSelected"
      >
        <PhEyeSlash />
        Not interested ({{ selectedCount }})
      </button>
      <button class="btn-link" @click="selected = {}">Clear selection</button>
    </div>

    <LoadingState v-if="loading" message="Loading series..." />

    <div v-else-if="visibleRows.length > 0" class="triage-list">
      <div class="triage-head">
        <span class="col-check">
          <input
            type="checkbox"
            :checked="allVisibleSelected"
            aria-label="Select all visible series"
            @change="toggleSelectAll"
          />
        </span>
        <span class="col-series">Series</span>
        <span class="col-progress">Owned</span>
        <span class="col-actions"></span>
      </div>
      <div
        v-for="row in visibleRows"
        :key="row.name"
        class="triage-row"
        :class="{ dismissed: row.dismissed, busy: !!busy[row.name] }"
        data-testid="triage-row"
      >
        <span class="col-check">
          <input
            v-if="!row.dismissed"
            type="checkbox"
            :checked="!!selected[row.name]"
            :aria-label="`Select ${row.name}`"
            @change="toggleSelected(row.name)"
          />
        </span>
        <span class="col-series">
          <RouterLink
            :to="`/collection/series/${encodeURIComponent(row.name)}`"
            class="series-name"
            >{{ row.name }}</RouterLink
          >
          <span v-if="row.authors.length" class="series-authors muted">{{
            row.authors.join(', ')
          }}</span>
          <span v-if="row.dismissed" class="dismissed-tag">Not interested</span>
        </span>
        <span class="col-progress">
          <span class="series-bar">
            <span class="series-bar-fill" :style="{ width: `${barPercent(row)}%` }"></span>
          </span>
          <span class="series-counts">
            <strong>{{ row.owned }}</strong>
            <template v-if="row.catalogTotal != null">
              /{{ row.catalogTotal }}
              <small>({{ Math.max(0, row.catalogTotal - row.owned) }} more in catalog)</small>
            </template>
            <template v-else>
              owned
              <small
                class="series-hint"
                title="No cached catalog for this series yet — counts reflect tracked records only. The background catalog sweep fills this in over time."
                >no catalog yet</small
              >
            </template>
            <small v-if="(row.editions ?? 0) > 1" class="series-hint"
              >{{ row.editions }} editions</small
            >
          </span>
        </span>
        <span class="col-actions">
          <template v-if="row.dismissed">
            <button
              class="btn btn-secondary btn-sm"
              :disabled="!!busy[row.name]"
              data-testid="triage-undo"
              @click="undismiss(row)"
            >
              Undo
            </button>
          </template>
          <template v-else>
            <button
              class="btn btn-primary btn-sm"
              :disabled="!!busy[row.name]"
              :title="`Monitor ${row.name} and add its missing books`"
              data-testid="triage-collect"
              @click="collect(row)"
            >
              <PhSpinner v-if="busy[row.name] === 'collect'" class="ph-spin" :size="14" />
              <PhPlusCircle v-else :size="14" />
              Collect
            </button>
            <button
              class="btn btn-secondary btn-sm"
              :disabled="!!busy[row.name]"
              data-testid="triage-dismiss"
              @click="dismiss(row)"
            >
              <PhEyeSlash :size="14" />
              Not interested
            </button>
          </template>
        </span>
      </div>
    </div>

    <EmptyState
      v-else
      :title="filterText || hideSingle ? 'No matching series' : 'Nothing to decide'"
      :message="
        filterText || hideSingle
          ? 'No series match the current filter.'
          : 'Every series you own books from is either monitored or marked not interested.'
      "
    >
      <template #icon>
        <PhCheckCircle :size="48" />
      </template>
    </EmptyState>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { errorTracking } from '@/services/errorTracking'
import { logger } from '@/utils/logger'
import { EmptyState, LoadingState } from '@/components/base'
import type { SeriesTriageRow, SeriesTriageSummary } from '@/types'
import {
  PhListChecks,
  PhMagnifyingGlass,
  PhX,
  PhEyeSlash,
  PhPlusCircle,
  PhSpinner,
  PhCheckCircle,
} from '@phosphor-icons/vue'

const HIDE_SINGLE_KEY = 'listenarr.seriesTriage.hideSingle'

const toast = useToast()

const loading = ref(true)
const rows = ref<SeriesTriageRow[]>([])
const summary = ref<SeriesTriageSummary | null>(null)
const filterText = ref('')
const showDismissed = ref(false)
const sortBy = ref<'owned' | 'completion' | 'name'>('owned')
const hideSingle = ref(readHideSingle())
const busy = ref<Record<string, 'collect' | 'dismiss' | 'undo' | undefined>>({})
const selected = ref<Record<string, boolean>>({})
const bulkBusy = ref(false)

function readHideSingle(): boolean {
  try {
    return localStorage.getItem(HIDE_SINGLE_KEY) === '1'
  } catch {
    return false
  }
}

watch(hideSingle, (value) => {
  try {
    localStorage.setItem(HIDE_SINGLE_KEY, value ? '1' : '0')
  } catch {
    /* storage unavailable — the toggle still works for this page load */
  }
})

watch(showDismissed, () => {
  void load()
})

async function load() {
  loading.value = rows.value.length === 0
  try {
    const resp = await apiService.getSeriesTriage(showDismissed.value)
    rows.value = resp.rows
    summary.value = resp.summary
    // Drop selections for rows that are no longer present.
    const present = new Set(resp.rows.map((r) => r.name))
    for (const name of Object.keys(selected.value)) {
      if (!present.has(name)) delete selected.value[name]
    }
  } catch (err) {
    logger.warn('Failed to load series triage', err)
    toast.error('Could not load series', err instanceof Error ? err.message : 'Unknown error')
  } finally {
    loading.value = false
  }
}

const filteredRows = computed(() => {
  const query = filterText.value.trim().toLowerCase()
  return rows.value.filter((row) => {
    if (hideSingle.value && row.owned <= 1 && !row.dismissed) return false
    if (!query) return true
    return (
      row.name.toLowerCase().includes(query) ||
      row.authors.some((a) => a.toLowerCase().includes(query))
    )
  })
})

const visibleRows = computed(() => {
  const list = [...filteredRows.value]
  switch (sortBy.value) {
    case 'completion':
      list.sort(
        (a, b) =>
          (b.completion ?? -1) - (a.completion ?? -1) ||
          b.owned - a.owned ||
          a.name.localeCompare(b.name),
      )
      break
    case 'name':
      list.sort((a, b) => a.name.localeCompare(b.name))
      break
    default:
      list.sort((a, b) => b.owned - a.owned || a.name.localeCompare(b.name))
  }
  return list
})

const selectedCount = computed(() => Object.values(selected.value).filter(Boolean).length)
const allVisibleSelected = computed(() => {
  const candidates = visibleRows.value.filter((r) => !r.dismissed)
  return candidates.length > 0 && candidates.every((r) => selected.value[r.name])
})

function toggleSelected(name: string) {
  if (selected.value[name]) delete selected.value[name]
  else selected.value[name] = true
}

function toggleSelectAll() {
  const candidates = visibleRows.value.filter((r) => !r.dismissed)
  if (allVisibleSelected.value) {
    for (const r of candidates) delete selected.value[r.name]
  } else {
    for (const r of candidates) selected.value[r.name] = true
  }
}

function barPercent(row: SeriesTriageRow): number {
  if (row.catalogTotal && row.catalogTotal > 0) {
    return Math.min(100, Math.round((row.owned / row.catalogTotal) * 100))
  }
  const tracked = row.owned + row.missingTracked
  return tracked > 0 ? Math.min(100, Math.round((row.owned / tracked) * 100)) : 0
}

function removeRow(name: string) {
  rows.value = rows.value.filter((r) => r.name !== name)
  delete selected.value[name]
}

async function collect(row: SeriesTriageRow) {
  if (busy.value[row.name]) return
  busy.value[row.name] = 'collect'
  try {
    const response = await apiService.monitorSeries({
      name: row.name,
      asin: row.seriesAsin ?? undefined,
    })
    // Collecting supersedes any earlier "not interested"; clear it quietly.
    if (row.dismissed) {
      await apiService.undismissSeries(row.name).catch(() => undefined)
    }
    const added = response.addedCount ?? 0
    toast.success(
      `Now collecting ${row.name}`,
      added > 0
        ? `Added ${added} missing book${added === 1 ? '' : 's'} from the series catalog.`
        : 'No new books needed to be added from the series catalog.',
    )
    if (response.failedCount > 0 || response.errorMessage) {
      toast.warning(
        'Monitoring completed with warnings',
        response.errorMessage ||
          `${response.failedCount} book${response.failedCount === 1 ? '' : 's'} could not be added automatically.`,
      )
    }
    removeRow(row.name)
    if (summary.value) {
      summary.value = {
        ...summary.value,
        candidates: Math.max(0, summary.value.candidates - (row.dismissed ? 0 : 1)),
        ownedBooksInCandidates: Math.max(
          0,
          summary.value.ownedBooksInCandidates - (row.dismissed ? 0 : row.owned),
        ),
        singleBook: Math.max(
          0,
          summary.value.singleBook - (!row.dismissed && row.owned === 1 ? 1 : 0),
        ),
        dismissed: Math.max(0, summary.value.dismissed - (row.dismissed ? 1 : 0)),
      }
    }
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'SeriesTriageView',
      operation: 'collect',
      metadata: { series: row.name },
    })
    toast.error('Could not start collecting', err instanceof Error ? err.message : 'Unknown error')
  } finally {
    delete busy.value[row.name]
  }
}

async function dismissOne(row: SeriesTriageRow): Promise<boolean> {
  try {
    await apiService.dismissSeries({ seriesName: row.name, seriesAsin: row.seriesAsin })
    if (showDismissed.value) {
      row.dismissed = true
      row.dismissedAt = new Date().toISOString()
      delete selected.value[row.name]
    } else {
      removeRow(row.name)
    }
    if (summary.value) {
      summary.value = {
        ...summary.value,
        candidates: Math.max(0, summary.value.candidates - 1),
        ownedBooksInCandidates: Math.max(0, summary.value.ownedBooksInCandidates - row.owned),
        singleBook: Math.max(0, summary.value.singleBook - (row.owned === 1 ? 1 : 0)),
        dismissed: summary.value.dismissed + 1,
      }
    }
    return true
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'SeriesTriageView',
      operation: 'dismiss',
      metadata: { series: row.name },
    })
    toast.error(
      `Could not dismiss ${row.name}`,
      err instanceof Error ? err.message : 'Unknown error',
    )
    return false
  }
}

async function dismiss(row: SeriesTriageRow) {
  if (busy.value[row.name]) return
  busy.value[row.name] = 'dismiss'
  try {
    await dismissOne(row)
  } finally {
    delete busy.value[row.name]
  }
}

async function dismissSelected() {
  const targets = rows.value.filter((r) => selected.value[r.name] && !r.dismissed)
  if (targets.length === 0 || bulkBusy.value) return
  bulkBusy.value = true
  let done = 0
  try {
    for (const row of targets) {
      busy.value[row.name] = 'dismiss'
      if (await dismissOne(row)) done += 1
      delete busy.value[row.name]
    }
    toast.success(
      'Marked not interested',
      `${done} of ${targets.length} series will no longer be suggested.`,
    )
  } finally {
    bulkBusy.value = false
  }
}

async function undismiss(row: SeriesTriageRow) {
  if (busy.value[row.name]) return
  busy.value[row.name] = 'undo'
  try {
    await apiService.undismissSeries(row.name)
    row.dismissed = false
    row.dismissedAt = null
    row.note = null
    if (summary.value) {
      summary.value = {
        ...summary.value,
        candidates: summary.value.candidates + 1,
        ownedBooksInCandidates: summary.value.ownedBooksInCandidates + row.owned,
        singleBook: summary.value.singleBook + (row.owned === 1 ? 1 : 0),
        dismissed: Math.max(0, summary.value.dismissed - 1),
      }
    }
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'SeriesTriageView',
      operation: 'undismiss',
      metadata: { series: row.name },
    })
    toast.error(
      `Could not undo for ${row.name}`,
      err instanceof Error ? err.message : 'Unknown error',
    )
  } finally {
    delete busy.value[row.name]
  }
}

onMounted(() => {
  void load()
})

defineExpose({ visibleRows, collect, dismiss, undismiss })
</script>

<style scoped>
.series-triage-view {
  padding: 1.5rem;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 1rem;
  flex-wrap: wrap;
  margin-bottom: 1.25rem;
}

.page-header h1 {
  margin: 0;
  color: white;
  font-size: 2rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
}

.page-summary {
  margin: 0.35rem 0 0;
  color: #adb5bd;
  font-size: 0.9rem;
}

.muted {
  color: #868e96;
}

.triage-actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.filter-input-wrapper {
  position: relative;
  display: flex;
  align-items: center;
}

.filter-icon {
  position: absolute;
  left: 0.6rem;
  color: #868e96;
  pointer-events: none;
}

.filter-input {
  background: #2a2a2a;
  border: 1px solid #444;
  color: #fff;
  border-radius: 6px;
  padding: 0.45rem 1.9rem 0.45rem 2rem;
  min-width: 16rem;
  font-size: 0.9rem;
}

.filter-input:focus {
  outline: none;
  border-color: var(--brand, #5aa9e6);
}

.filter-clear {
  position: absolute;
  right: 0.35rem;
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  display: flex;
}

.toggle {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  color: #adb5bd;
  font-size: 0.85rem;
  cursor: pointer;
  user-select: none;
}

.sort-select {
  background: #2a2a2a;
  border: 1px solid #444;
  color: #fff;
  border-radius: 6px;
  padding: 0.4rem 0.6rem;
  font-size: 0.85rem;
}

.bulk-bar {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.6rem 0.9rem;
  margin-bottom: 0.75rem;
  border-radius: 8px;
  background: rgba(var(--brand-rgb, 90, 169, 230), 0.1);
  border: 1px solid rgba(var(--brand-rgb, 90, 169, 230), 0.35);
  color: #dee2e6;
  font-size: 0.9rem;
}

.btn-link {
  background: none;
  border: none;
  color: #adb5bd;
  cursor: pointer;
  text-decoration: underline;
  font-size: 0.85rem;
}

.triage-list {
  display: flex;
  flex-direction: column;
  border: 1px solid #333;
  border-radius: 8px;
  overflow: hidden;
}

.triage-head,
.triage-row {
  display: grid;
  grid-template-columns: 2rem minmax(12rem, 2fr) minmax(12rem, 2fr) auto;
  align-items: center;
  gap: 0.75rem;
  padding: 0.6rem 0.9rem;
}

.triage-head {
  background: #1f1f1f;
  color: #868e96;
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.triage-row {
  border-top: 1px solid #2c2c2c;
  background: #232323;
}

.triage-row:hover {
  background: #2a2a2a;
}

.triage-row.dismissed {
  opacity: 0.6;
}

.triage-row.busy {
  opacity: 0.7;
}

.col-series {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  min-width: 0;
}

.series-name {
  color: #fff;
  text-decoration: none;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.series-name:hover {
  color: var(--brand, #5aa9e6);
}

.series-authors {
  font-size: 0.8rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.dismissed-tag {
  font-size: 0.7rem;
  color: #adb5bd;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.col-progress {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  min-width: 0;
}

.series-bar {
  flex: 1;
  height: 6px;
  border-radius: 3px;
  background: #3a3a3a;
  overflow: hidden;
  min-width: 4rem;
}

.series-bar-fill {
  display: block;
  height: 100%;
  background: var(--brand, #5aa9e6);
}

.series-counts {
  color: #dee2e6;
  font-size: 0.9rem;
  white-space: nowrap;
}

.series-counts small,
.series-hint {
  color: #868e96;
  font-size: 0.75rem;
  margin-left: 0.3rem;
}

.col-actions {
  display: flex;
  gap: 0.4rem;
  justify-content: flex-end;
}

.btn-sm {
  padding: 0.3rem 0.65rem;
  font-size: 0.8rem;
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
}

.ph-spin {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

@media (max-width: 768px) {
  .triage-head {
    display: none;
  }

  .triage-row {
    grid-template-columns: 2rem 1fr;
    grid-template-rows: auto auto auto;
  }

  .col-progress,
  .col-actions {
    grid-column: 2;
    justify-content: flex-start;
  }
}
</style>
