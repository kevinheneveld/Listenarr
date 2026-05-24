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
              {{ groups.length }} same-ASIN groups detected. Pick the row to keep in each
              group — every other row will be removed. <strong>Files on disk are not
              touched.</strong> If the chosen winner's folder isn't the one you want, move
              the files first (Audiobook detail → Files → Move).
            </p>

            <div v-for="group in groups" :key="group.normalizedAsin" class="group">
              <div class="group-header">
                <span class="group-asin">ASIN {{ group.normalizedAsin }}</span>
                <span class="group-meta">{{ group.rows.length }} rows</span>
              </div>
              <div
                v-for="row in group.rows"
                :key="row.id"
                class="row"
                :class="{ 'winner-selected': selectedWinners[group.normalizedAsin] === row.id }"
              >
                <label class="row-radio">
                  <input
                    type="radio"
                    :name="`grp-${group.normalizedAsin}`"
                    :value="row.id"
                    v-model="selectedWinners[group.normalizedAsin]"
                    :disabled="merging"
                  />
                  <span class="row-radio-mark" />
                </label>

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
                    {{ safeText(row.title) || '(no title)' }}
                    <span v-if="row.recommendedWinner" class="badge-suggested" title="Server suggestion">suggested</span>
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
                    <span class="meta-chip">id {{ row.id }}</span>
                  </div>
                </div>
              </div>
            </div>
          </template>
        </div>

        <footer class="modal-footer">
          <div class="footer-summary">
            <span v-if="!loading && groups.length > 0">
              {{ plannedDeletions }} row{{ plannedDeletions === 1 ? '' : 's' }} will be removed.
            </span>
            <span v-if="mergeError" class="error">{{ mergeError }}</span>
          </div>
          <div class="footer-actions">
            <button type="button" class="btn" :disabled="merging" @click="onClose">Cancel</button>
            <button
              type="button"
              class="btn btn-primary"
              :disabled="merging || loading || groups.length === 0"
              @click="executeMerge"
            >
              {{ merging ? 'Merging…' : `Merge ${groups.length} groups` }}
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
import type { DuplicateGroup, DuplicateRow, MergeDuplicatesResult } from '@/types'

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
// Per-group selected winner id, keyed by normalizedAsin.
const selectedWinners = reactive<Record<string, number>>({})

const { getProtectedImageSrc } = useProtectedImages()

const plannedDeletions = computed(() => {
  let total = 0
  for (const g of groups.value) {
    const winnerId = selectedWinners[g.normalizedAsin]
    if (winnerId == null) continue
    total += g.rows.filter((r) => r.id !== winnerId).length
  }
  return total
})

function protectedSrc(row: DuplicateRow): string {
  if (!row.imageUrl) return ''
  return getProtectedImageSrc(row.imageUrl, `dup:${row.id}`) || row.imageUrl
}

function onThumbError(e: Event) {
  const img = e.target as HTMLImageElement | null
  if (img) img.style.display = 'none'
}

async function load() {
  loading.value = true
  loadError.value = null
  mergeError.value = null
  try {
    const resp = await apiService.getDuplicateAudiobooks()
    groups.value = resp.groups || []
    // Default each group's selection to the server's recommended winner.
    for (const g of groups.value) {
      const rec = g.rows.find((r) => r.recommendedWinner)
      if (rec) selectedWinners[g.normalizedAsin] = rec.id
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
  const merges: { winnerId: number; loserIds: number[] }[] = []
  for (const g of groups.value) {
    const winnerId = selectedWinners[g.normalizedAsin]
    if (winnerId == null) continue
    const loserIds = g.rows.map((r) => r.id).filter((id) => id !== winnerId)
    if (loserIds.length === 0) continue
    merges.push({ winnerId, loserIds })
  }
  if (merges.length === 0) {
    mergeError.value = 'No winners selected — nothing to merge.'
    return
  }

  merging.value = true
  try {
    const result = await apiService.mergeDuplicateAudiobooks(merges)
    emit('merged', result)
    emit('close')
  } catch (err) {
    mergeError.value = err instanceof Error ? err.message : 'Merge failed'
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

watch(
  () => props.visible,
  (v) => {
    if (v) {
      groups.value = []
      for (const k of Object.keys(selectedWinners)) delete selectedWinners[k]
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
  max-width: 900px;
  max-height: 85vh;
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
  margin-bottom: 12px;
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
.row {
  display: grid;
  grid-template-columns: 28px 48px 1fr;
  gap: 10px;
  align-items: center;
  padding: 8px 12px;
  border-top: 1px solid rgba(255, 255, 255, 0.03);
}
.row:first-of-type {
  border-top: 0;
}
.row.winner-selected {
  background: rgba(46, 117, 232, 0.07);
}
.row-radio {
  display: flex;
  align-items: center;
  justify-content: center;
}
.row-radio input {
  width: 16px;
  height: 16px;
  cursor: pointer;
}
.row-thumb {
  width: 48px;
  height: 48px;
  border-radius: 4px;
  overflow: hidden;
  background: rgba(255, 255, 255, 0.04);
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
  font-size: 13px;
  color: #fff;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.badge-suggested {
  display: inline-block;
  font-size: 10px;
  color: #2a6fd0;
  background: rgba(42, 111, 208, 0.15);
  border: 1px solid rgba(42, 111, 208, 0.4);
  padding: 1px 6px;
  border-radius: 8px;
  margin-left: 6px;
  vertical-align: middle;
  letter-spacing: 0.04em;
  text-transform: uppercase;
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
.modal-fade-enter-active,
.modal-fade-leave-active {
  transition: opacity 0.15s;
}
.modal-fade-enter-from,
.modal-fade-leave-to {
  opacity: 0;
}
</style>
