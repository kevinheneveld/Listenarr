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
  <transition name="modal-fade">
    <div v-if="visible" class="modal-overlay" @click.self="onClose">
      <div class="modal duplicates-modal" role="dialog" aria-modal="true" aria-labelledby="dup-modal-title">
        <header class="modal-header">
          <h2 id="dup-modal-title">Review duplicate audiobooks</h2>
          <button class="modal-close" :disabled="merging" @click="onClose" aria-label="Close">
            <PhX />
          </button>
        </header>

        <div class="modal-body">
          <div v-if="loading" class="state-msg">Loading duplicate groups…</div>

          <div v-else-if="loadError" class="state-msg error">
            Failed to load duplicates: {{ loadError }}
          </div>

          <div v-else-if="groups.length === 0" class="state-msg">
            No same-ASIN duplicate audiobook rows found.
          </div>

          <template v-else>
            <p class="help-text">
              Every row starts as <strong>Skip</strong>. Pick an action per row:
              <strong>Keep</strong> (this row survives) →
              <strong>Merge</strong> (delete and reassign refs into the Keep row) →
              <strong>Clear ASIN</strong> (keep the row but un-dupe it).
              <strong>Files on disk are not touched</strong> — move them yourself first if the
              Keep row's folder isn't the one you want.
            </p>

            <div v-for="group in groups" :key="group.normalizedAsin" class="group">
              <div class="group-header">
                <span class="group-asin">ASIN {{ group.normalizedAsin }}</span>
                <span class="group-meta">{{ group.rows.length }} rows</span>
              </div>
              <p v-if="group.recommendationReason" class="group-reason">
                <strong>Suggestion:</strong> {{ group.recommendationReason }}
              </p>

              <div
                v-for="row in group.rows"
                :key="row.id"
                class="row"
                :class="{
                  'row-keep': rowActions[row.id] === 'keep',
                  'row-merge': rowActions[row.id] === 'merge',
                  'row-clear': rowActions[row.id] === 'clearAsin',
                }"
              >
                <div class="row-head">
                  <div class="row-thumb">
                    <img
                      v-if="row.imageUrl"
                      :src="protectedSrc(row)"
                      :alt="row.title || 'cover'"
                      loading="lazy"
                      @error="onThumbError"
                    />
                    <div v-else class="row-thumb-placeholder">—</div>
                  </div>

                  <div class="row-main">
                    <div class="row-title">
                      <span class="title-text">{{ safeText(row.title) || '(no title)' }}</span>
                      <span v-if="row.recommendedWinner" class="badge-suggested" :title="group.recommendationReason">
                        suggested
                      </span>
                      <a
                        class="row-link"
                        :href="`/audiobook/${row.id}`"
                        target="_blank"
                        rel="noopener"
                        title="Open audiobook detail in a new tab"
                        @click.stop
                      >
                        open in new tab ↗
                      </a>
                    </div>
                    <div class="row-sub">
                      <span v-if="row.series">{{ safeText(row.series) }}<span v-if="row.seriesNumber"> #{{ row.seriesNumber }}</span></span>
                      <span class="row-sep" v-if="row.series && row.basePath">·</span>
                      <span v-if="row.basePath" class="row-path">{{ row.basePath }}</span>
                    </div>
                    <div class="row-meta">
                      <span class="meta-chip" :class="{ on: row.hasAnyFile }">
                        {{ row.fileCount }} file{{ row.fileCount === 1 ? '' : 's' }}
                      </span>
                      <span class="meta-chip" :class="{ on: row.hasBookFolder }">
                        {{ row.hasBookFolder ? 'real folder' : 'no book folder' }}
                      </span>
                      <span v-if="row.totalSize > 0" class="meta-chip">{{ formatBytes(row.totalSize) }}</span>
                      <span v-if="row.runtime" class="meta-chip">{{ formatMinutes(row.runtime) }}</span>
                      <span class="meta-chip">id {{ row.id }}</span>
                    </div>
                  </div>

                  <div class="row-actions">
                    <label
                      v-for="opt in actionOptions(row, group)"
                      :key="opt.value"
                      class="action-pill"
                      :class="{
                        active: rowActions[row.id] === opt.value,
                        disabled: opt.disabled,
                      }"
                      :title="opt.tooltip"
                    >
                      <input
                        type="radio"
                        :name="`act-${row.id}`"
                        :value="opt.value"
                        :checked="rowActions[row.id] === opt.value"
                        :disabled="opt.disabled || merging"
                        @change="setRowAction(row, group, opt.value)"
                      />
                      <span>{{ opt.label }}</span>
                    </label>

                    <button
                      type="button"
                      class="expand-toggle"
                      :aria-expanded="expanded[row.id] ? 'true' : 'false'"
                      @click="expanded[row.id] = !expanded[row.id]"
                    >
                      <span v-if="expanded[row.id]">▾ details</span>
                      <span v-else>▸ details</span>
                    </button>
                  </div>
                </div>

                <div v-if="expanded[row.id]" class="row-details">
                  <div class="details-grid">
                    <div class="details-cell">
                      <div class="details-label">Authors</div>
                      <div class="details-value">{{ row.authors.join(', ') || '—' }}</div>
                    </div>
                    <div class="details-cell">
                      <div class="details-label">Narrators</div>
                      <div class="details-value">{{ row.narrators.join(', ') || '—' }}</div>
                    </div>
                    <div class="details-cell">
                      <div class="details-label">Runtime</div>
                      <div class="details-value">{{ row.runtime ? formatMinutes(row.runtime) : '—' }}</div>
                    </div>
                    <div class="details-cell">
                      <div class="details-label">Total size</div>
                      <div class="details-value">{{ row.totalSize > 0 ? formatBytes(row.totalSize) : '—' }}</div>
                    </div>
                  </div>

                  <div v-if="row.files.length > 0" class="files-list">
                    <div class="files-list-header">
                      <span>File</span><span>Size</span><span>Format</span><span>Bitrate</span><span>Duration</span>
                    </div>
                    <div v-for="f in row.files" :key="f.id" class="files-list-row">
                      <span class="file-path" :title="f.path || ''">{{ shortenPath(f.path) }}</span>
                      <span>{{ f.size ? formatBytes(f.size) : '—' }}</span>
                      <span>{{ f.format || f.codec || '—' }}</span>
                      <span>{{ f.bitrate ? `${Math.round(f.bitrate / 1000)} kbps` : '—' }}</span>
                      <span>{{ f.durationSeconds ? formatDuration(f.durationSeconds) : '—' }}</span>
                    </div>
                  </div>
                  <div v-else class="no-files">No tracked files on this row.</div>
                </div>
              </div>
            </div>
          </template>
        </div>

        <footer class="modal-footer">
          <div class="footer-summary">
            <span v-if="!loading && groups.length > 0">
              {{ plannedDeletions }} to delete · {{ plannedClears }} ASIN clear{{ plannedClears === 1 ? '' : 's' }}
              · {{ skippedCount }} group{{ skippedCount === 1 ? '' : 's' }} fully skipped
            </span>
            <span v-if="mergeError" class="error">{{ mergeError }}</span>
          </div>
          <div class="footer-actions">
            <button type="button" class="btn" :disabled="merging" @click="onClose">Cancel</button>
            <button
              type="button"
              class="btn btn-primary"
              :disabled="merging || loading || !canApply"
              @click="executeMerge"
            >
              {{ merging ? 'Applying…' : 'Apply' }}
            </button>
          </div>
        </footer>
      </div>
    </div>
  </transition>
</template>

<script setup lang="ts">
import { ref, computed, watch, reactive } from 'vue'
import { PhX } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import { useProtectedImages } from '@/composables/useProtectedImages'
import { safeText } from '@/utils/textUtils'
import { errorTracking } from '@/services/errorTracking'
import type {
  DuplicateGroup,
  DuplicateRow,
  DuplicateRowAction,
  DuplicatesMergePair,
  MergeDuplicatesResult,
} from '@/types'

const props = defineProps<{ visible: boolean }>()
const emit = defineEmits<{
  (e: 'close'): void
  (e: 'merged', result: MergeDuplicatesResult): void
}>()

const loading = ref(false)
const loadError = ref<string | null>(null)
const merging = ref(false)
const mergeError = ref<string | null>(null)
const groups = ref<DuplicateGroup[]>([])
// Per-row action keyed by audiobook id.
const rowActions = reactive<Record<number, DuplicateRowAction>>({})
const expanded = reactive<Record<number, boolean>>({})

const { getProtectedImageSrc } = useProtectedImages()

const plannedDeletions = computed(() => {
  let total = 0
  for (const g of groups.value) {
    for (const r of g.rows) {
      if (rowActions[r.id] === 'merge') total++
    }
  }
  return total
})

const plannedClears = computed(() => {
  let total = 0
  for (const g of groups.value) {
    for (const r of g.rows) {
      if (rowActions[r.id] === 'clearAsin') total++
    }
  }
  return total
})

const skippedCount = computed(() => {
  let count = 0
  for (const g of groups.value) {
    const allSkip = g.rows.every((r) => (rowActions[r.id] || 'skip') === 'skip')
    if (allSkip) count++
  }
  return count
})

const canApply = computed(() => plannedDeletions.value > 0 || plannedClears.value > 0)

function protectedSrc(row: DuplicateRow): string {
  if (!row.imageUrl) return ''
  return getProtectedImageSrc(row.imageUrl, `dup:${row.id}`) || row.imageUrl
}

function onThumbError(e: Event) {
  const img = e.target as HTMLImageElement | null
  if (img) img.style.display = 'none'
}

interface ActionOption {
  value: DuplicateRowAction
  label: string
  disabled: boolean
  tooltip: string
}

function actionOptions(row: DuplicateRow, group: DuplicateGroup): ActionOption[] {
  // Disable 'Keep' if another row in this group is already 'keep'.
  // Disable 'Merge' if no 'keep' row exists yet (you must pick a survivor first).
  const keepRow = group.rows.find((r) => rowActions[r.id] === 'keep')
  const anotherIsKeep = keepRow != null && keepRow.id !== row.id
  const noKeepSelected = keepRow == null

  return [
    {
      value: 'skip',
      label: 'Skip',
      disabled: false,
      tooltip: 'Leave this row untouched.',
    },
    {
      value: 'keep',
      label: 'Keep',
      disabled: anotherIsKeep,
      tooltip: anotherIsKeep
        ? `Already keeping id ${keepRow!.id}. Switch that one first.`
        : 'This row survives. Any Merge rows will fold into it.',
    },
    {
      value: 'merge',
      label: 'Merge',
      disabled: noKeepSelected,
      tooltip: noKeepSelected
        ? 'Pick a Keep row in this group first.'
        : `Delete this row; reassign Downloads/History/MoveJobs to id ${keepRow!.id}.`,
    },
    {
      value: 'clearAsin',
      label: 'Clear ASIN',
      disabled: false,
      tooltip: 'Keep the row, but null out its ASIN so it stops being detected as a duplicate.',
    },
  ]
}

function setRowAction(row: DuplicateRow, group: DuplicateGroup, action: DuplicateRowAction) {
  // When a row switches to 'keep', if another row was already 'keep', demote
  // that row back to 'skip' so there's at most one Keep per group at a time.
  if (action === 'keep') {
    for (const other of group.rows) {
      if (other.id !== row.id && rowActions[other.id] === 'keep') {
        rowActions[other.id] = 'skip'
      }
    }
  }
  // If the user moves away from 'keep' and other rows in the group are
  // currently 'merge', they'd become invalid (no winner). Demote them.
  if (action !== 'keep' && rowActions[row.id] === 'keep') {
    for (const other of group.rows) {
      if (other.id !== row.id && rowActions[other.id] === 'merge') {
        rowActions[other.id] = 'skip'
      }
    }
  }
  rowActions[row.id] = action
}

async function load() {
  loading.value = true
  loadError.value = null
  mergeError.value = null
  try {
    const resp = await apiService.getDuplicateAudiobooks()
    groups.value = resp.groups || []
    // Default every row to Skip (safe default — see kevinheneveld/Listenarr#6).
    for (const g of groups.value) {
      for (const r of g.rows) {
        rowActions[r.id] = 'skip'
      }
    }
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : 'Unknown error'
    errorTracking.captureException(err as Error, {
      component: 'DuplicatesReviewModal',
      operation: 'load',
    })
  } finally {
    loading.value = false
  }
}

async function executeMerge() {
  mergeError.value = null
  const merges: DuplicatesMergePair[] = []
  for (const g of groups.value) {
    const keepRow = g.rows.find((r) => rowActions[r.id] === 'keep')
    const losers = g.rows.filter((r) => rowActions[r.id] === 'merge').map((r) => r.id)
    const clears = g.rows.filter((r) => rowActions[r.id] === 'clearAsin').map((r) => r.id)
    if (losers.length === 0 && clears.length === 0) continue
    if (losers.length > 0 && !keepRow) {
      mergeError.value = `Group ${g.normalizedAsin} has Merge rows but no Keep row.`
      return
    }
    merges.push({
      winnerId: keepRow ? keepRow.id : null,
      loserIds: losers,
      clearAsinIds: clears,
    })
  }
  if (merges.length === 0) {
    mergeError.value = 'No actions selected.'
    return
  }

  merging.value = true
  try {
    const result = await apiService.mergeDuplicateAudiobooks(merges)
    emit('merged', result)
    emit('close')
  } catch (err) {
    mergeError.value = err instanceof Error ? err.message : 'Apply failed'
    errorTracking.captureException(err as Error, {
      component: 'DuplicatesReviewModal',
      operation: 'executeMerge',
    })
  } finally {
    merging.value = false
  }
}

function onClose() {
  if (merging.value) return
  emit('close')
}

function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unitIdx = 0
  while (value >= 1024 && unitIdx < units.length - 1) {
    value /= 1024
    unitIdx++
  }
  const decimals = value < 10 && unitIdx > 0 ? 1 : 0
  return `${value.toFixed(decimals)} ${units[unitIdx]}`
}

function formatMinutes(minutes: number): string {
  if (!minutes) return '—'
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

function formatDuration(seconds: number): string {
  if (!seconds || seconds <= 0) return '—'
  const totalMin = Math.round(seconds / 60)
  return formatMinutes(totalMin)
}

function shortenPath(p: string | null): string {
  if (!p) return '—'
  // Trim the leading /audiobooks/ to save horizontal space; users still see
  // the full path in the title attribute.
  const trimmed = p.replace(/^\/?audiobooks\//, '')
  return trimmed || p
}

watch(
  () => props.visible,
  (v) => {
    if (v) {
      groups.value = []
      for (const k of Object.keys(rowActions)) delete rowActions[Number(k)]
      for (const k of Object.keys(expanded)) delete expanded[Number(k)]
      void load()
    }
  },
  { immediate: true },
)
</script>

<style scoped>
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.55);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 3000;
  padding: 1rem;
}
.modal {
  background: #1a1a1a;
  border-radius: 8px;
  width: 100%;
  max-width: 1100px;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  border: 1px solid rgba(255, 255, 255, 0.08);
}
.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 14px 16px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
}
.modal-header h2 {
  font-size: 16px;
  margin: 0;
  color: #fff;
}
.modal-close {
  background: transparent;
  border: 0;
  color: #aaa;
  cursor: pointer;
  font-size: 18px;
}
.modal-close:hover {
  color: #fff;
}
.modal-body {
  flex: 1;
  overflow-y: auto;
  padding: 14px 16px;
}
.modal-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 16px;
  border-top: 1px solid rgba(255, 255, 255, 0.06);
  gap: 12px;
  flex-wrap: wrap;
}
.footer-summary {
  font-size: 12px;
  color: #aaa;
}
.footer-summary .error {
  color: #f06060;
  display: block;
  margin-top: 4px;
}
.footer-actions {
  display: flex;
  gap: 8px;
}
.btn {
  padding: 6px 14px;
  border-radius: 4px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.04);
  color: #ddd;
  font-size: 13px;
  cursor: pointer;
}
.btn:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.08);
}
.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.btn-primary {
  background: #2a6fd0;
  border-color: #2a6fd0;
  color: #fff;
}
.btn-primary:hover:not(:disabled) {
  background: #3380e5;
}
.state-msg {
  padding: 24px 8px;
  text-align: center;
  color: #aaa;
}
.state-msg.error {
  color: #f06060;
}
.help-text {
  font-size: 12px;
  color: #bbb;
  margin: 0 0 14px;
  line-height: 1.5;
}
.group {
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  margin-bottom: 14px;
  background: rgba(255, 255, 255, 0.015);
}
.group-header {
  display: flex;
  justify-content: space-between;
  padding: 8px 12px;
  font-size: 11px;
  color: #888;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  background: rgba(255, 255, 255, 0.02);
}
.group-asin {
  font-family: monospace;
  letter-spacing: 0.04em;
}
.group-reason {
  margin: 0;
  padding: 6px 12px;
  font-size: 11px;
  color: #9bb;
  background: rgba(42, 111, 208, 0.04);
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
}
.row {
  border-top: 1px solid rgba(255, 255, 255, 0.03);
  padding: 10px 12px;
}
.row:first-of-type {
  border-top: 0;
}
.row.row-keep {
  background: rgba(46, 117, 232, 0.06);
}
.row.row-merge {
  background: rgba(232, 99, 99, 0.05);
}
.row.row-clear {
  background: rgba(232, 200, 99, 0.04);
}
.row-head {
  display: grid;
  grid-template-columns: 48px 1fr auto;
  gap: 12px;
  align-items: flex-start;
}
.row-thumb {
  width: 48px;
  height: 48px;
  border-radius: 4px;
  overflow: hidden;
  background: rgba(255, 255, 255, 0.04);
  flex-shrink: 0;
}
.row-thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}
.row-thumb-placeholder {
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #555;
}
.row-main {
  min-width: 0;
}
.row-title {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.title-text {
  font-size: 13px;
  color: #fff;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.row-link {
  font-size: 11px;
  color: #6c9be8;
  text-decoration: none;
}
.row-link:hover {
  text-decoration: underline;
}
.badge-suggested {
  font-size: 10px;
  color: #2a6fd0;
  background: rgba(42, 111, 208, 0.15);
  border: 1px solid rgba(42, 111, 208, 0.4);
  padding: 1px 6px;
  border-radius: 8px;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  cursor: help;
}
.row-sub {
  font-size: 11px;
  color: #aaa;
  margin-top: 2px;
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
}
.row-sep {
  color: #555;
}
.row-path {
  font-family: monospace;
  color: #999;
}
.row-meta {
  display: flex;
  gap: 4px;
  margin-top: 4px;
  flex-wrap: wrap;
}
.meta-chip {
  font-size: 10px;
  padding: 2px 6px;
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.05);
  color: #888;
}
.meta-chip.on {
  background: rgba(80, 180, 100, 0.12);
  color: #6fc080;
}
.row-actions {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 4px;
  min-width: 0;
}
.action-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 10px;
  border-radius: 12px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.04);
  color: #aaa;
  font-size: 11px;
  cursor: pointer;
  user-select: none;
  white-space: nowrap;
}
.action-pill input {
  position: absolute;
  opacity: 0;
  pointer-events: none;
}
.action-pill:hover:not(.disabled) {
  background: rgba(255, 255, 255, 0.08);
  color: #ddd;
}
.action-pill.active {
  background: #2a6fd0;
  border-color: #2a6fd0;
  color: #fff;
}
.action-pill.disabled {
  opacity: 0.35;
  cursor: not-allowed;
}
.expand-toggle {
  margin-top: 4px;
  background: transparent;
  border: 0;
  color: #888;
  font-size: 11px;
  cursor: pointer;
  padding: 0;
}
.expand-toggle:hover {
  color: #ccc;
}
.row-details {
  margin-top: 10px;
  padding: 10px 12px;
  background: rgba(0, 0, 0, 0.2);
  border-radius: 4px;
  border: 1px solid rgba(255, 255, 255, 0.04);
}
.details-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 8px 16px;
  margin-bottom: 10px;
}
.details-cell {
  min-width: 0;
}
.details-label {
  font-size: 10px;
  color: #888;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.details-value {
  font-size: 12px;
  color: #ddd;
  word-break: break-word;
}
.files-list {
  border-top: 1px solid rgba(255, 255, 255, 0.04);
  padding-top: 8px;
  font-size: 11px;
}
.files-list-header {
  display: grid;
  grid-template-columns: minmax(0, 2.5fr) 70px 70px 80px 70px;
  gap: 8px;
  padding: 2px 0;
  color: #777;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  font-size: 10px;
}
.files-list-row {
  display: grid;
  grid-template-columns: minmax(0, 2.5fr) 70px 70px 80px 70px;
  gap: 8px;
  padding: 3px 0;
  color: #bbb;
  border-top: 1px solid rgba(255, 255, 255, 0.02);
}
.file-path {
  font-family: monospace;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  color: #ccc;
}
.no-files {
  font-size: 11px;
  color: #777;
  font-style: italic;
}
.modal-fade-enter-active,
.modal-fade-leave-active {
  transition: opacity 0.15s;
}
.modal-fade-enter-from,
.modal-fade-leave-to {
  opacity: 0;
}

@media (max-width: 760px) {
  .row-head {
    grid-template-columns: 48px 1fr;
  }
  .row-actions {
    grid-column: 1 / -1;
    align-items: flex-start;
    flex-direction: row;
    flex-wrap: wrap;
    margin-top: 6px;
  }
}
</style>
