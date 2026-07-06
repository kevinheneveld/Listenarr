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
  Review list for books the MusicSmellDetector flagged as "likely music".
  Detector proposes, a HUMAN disposes: every sweep runs the not-audiobook
  flow (files deleted from disk, delivering release blocklisted, book
  re-monitored + re-searched) and only ever behind an explicit confirm.
-->
<template>
  <div class="music-review">
    <div class="music-review-head">
      <span class="music-review-title">
        Likely music — review before sweeping. Sweeping deletes the files from disk, blocklists the
        delivering release, and re-searches for the real book.
      </span>
      <button
        v-if="rows.length > 1"
        type="button"
        class="sweep-all-btn"
        :disabled="sweeping"
        @click="sweepAll"
      >
        <PhSpinner v-if="sweeping" class="ph-spin" />
        <PhBroom v-else />
        Sweep all ({{ rows.length }})
      </button>
    </div>

    <div v-for="row in rows" :key="row.id" class="music-row">
      <RouterLink :to="`/audiobooks/${row.id}`" class="music-row-title">
        {{ row.title || `Book ${row.id}` }}
      </RouterLink>
      <span class="music-row-evidence">
        <strong>{{ Math.round(row.score * 100) }}%</strong>
        · {{ row.fileCount }} files · median {{ formatMedian(row.medianDurationSeconds) }}
        <template v-if="row.reasons.length"> · {{ row.reasons.join(' · ') }}</template>
      </span>
      <button type="button" class="sweep-btn" :disabled="sweeping" @click="sweepOne(row)">
        Sweep
      </button>
    </div>

    <div v-if="progressText" class="music-progress">{{ progressText }}</div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { PhBroom, PhSpinner } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { showConfirm } from '@/composables/useConfirm'
import type { MusicCandidate } from '@/types'

const props = defineProps<{ rows: MusicCandidate[] }>()
const emit = defineEmits<{ (e: 'swept', ids: number[]): void }>()

const toast = useToast()
const sweeping = ref(false)
const progressText = ref('')

function formatMedian(seconds: number): string {
  if (!seconds) return '?'
  return `${(seconds / 60).toFixed(1)} min`
}

async function sweepOne(row: MusicCandidate) {
  const ok = await showConfirm(
    `Sweep "${row.title || `Book ${row.id}`}"? The files are deleted from disk, the delivering ` +
      `release is blocklisted, and a search for the real book starts immediately. This cannot be undone.`,
    'Wrong content — sweep this book?',
    { danger: true, confirmText: 'Sweep', cancelText: 'Cancel' },
  )
  if (!ok) return
  await runSweep([row])
}

async function sweepAll() {
  const ok = await showConfirm(
    `Sweep all ${props.rows.length} likely-music books? For EVERY book in this list the files ` +
      `are deleted from disk, the delivering releases are blocklisted, and searches for the ` +
      `real books start immediately. This cannot be undone.`,
    `Sweep all ${props.rows.length} books?`,
    { danger: true, confirmText: `Sweep all ${props.rows.length}`, cancelText: 'Cancel' },
  )
  if (!ok) return
  await runSweep([...props.rows])
}

// Sequential, failure-tolerant sweep loop (the SplitCollectionModal pattern):
// one book at a time so a failure can't fan out, with a summary at the end.
async function runSweep(targets: MusicCandidate[]) {
  sweeping.value = true
  let swept = 0
  const sweptIds: number[] = []
  const failures: string[] = []
  try {
    for (const [index, row] of targets.entries()) {
      progressText.value = `Sweeping "${row.title || row.id}" (${index + 1}/${targets.length})…`
      try {
        await apiService.rejectNotAudiobook(row.id)
        swept++
        sweptIds.push(row.id)
      } catch (err) {
        failures.push(
          `${row.title || row.id}: ${err instanceof Error ? err.message : 'sweep failed'}`,
        )
      }
    }
  } finally {
    sweeping.value = false
    progressText.value = ''
  }

  if (failures.length === 0) {
    toast.success(
      'Sweep complete',
      `Swept ${swept} book${swept === 1 ? '' : 's'} — files removed, releases blocklisted, re-searches started.`,
    )
  } else {
    toast.warning(
      `Sweep finished with issues — ${swept} of ${targets.length} swept`,
      failures.slice(0, 3).join(' · '),
    )
  }
  if (sweptIds.length) emit('swept', sweptIds)
}
</script>

<style scoped>
.music-review {
  margin-top: 0.75rem;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 8px;
  padding: 0.75rem 1rem;
  background: rgba(255, 255, 255, 0.02);
}

.music-review-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  margin-bottom: 0.6rem;
}

.music-review-title {
  color: #adb5bd;
  font-size: 0.85rem;
  line-height: 1.4;
}

.sweep-all-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.45rem 0.9rem;
  background-color: rgba(255, 107, 107, 0.1);
  color: #ff6b6b;
  border: 1px solid rgba(255, 107, 107, 0.3);
  border-radius: 6px;
  cursor: pointer;
  font-size: 0.85rem;
  white-space: nowrap;
  flex-shrink: 0;
}

.sweep-all-btn:hover:not(:disabled) {
  background-color: rgba(255, 107, 107, 0.2);
}

.sweep-all-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.music-row {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.4rem 0;
  border-top: 1px solid rgba(255, 255, 255, 0.04);
}

.music-row-title {
  color: #4dabf7;
  text-decoration: none;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 30%;
  flex-shrink: 0;
}

.music-row-title:hover {
  text-decoration: underline;
}

.music-row-evidence {
  color: #8a93a0;
  font-size: 0.82rem;
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.sweep-btn {
  padding: 0.3rem 0.8rem;
  background: transparent;
  color: #ff6b6b;
  border: 1px solid rgba(255, 107, 107, 0.3);
  border-radius: 5px;
  cursor: pointer;
  font-size: 0.8rem;
  flex-shrink: 0;
}

.sweep-btn:hover:not(:disabled) {
  background-color: rgba(255, 107, 107, 0.12);
}

.sweep-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.music-progress {
  margin-top: 0.5rem;
  color: #4dabf7;
  font-size: 0.85rem;
}
</style>
