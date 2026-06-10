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
  <div class="form-section">
    <h3><PhWrench /> Library Maintenance</h3>
    <div class="form-body">
      <!-- Move-queue status banner: visible whenever there's any move history.
           Polls /library/move/summary every 5s. Lets the user see at-a-glance
           whether the queue is draining, stuck, or failing, without having to
           open the Organize modal. -->
      <MoveQueueStatusBanner />

      <div class="maintenance-row">
        <div class="maintenance-copy">
          <div class="maintenance-title">Cache external cover art</div>
          <div class="maintenance-help">
            Downloads any audiobook cover still pointing at an external URL
            (e.g. an Amazon CDN link from before this feature shipped) into
            local library storage. Safe to re-run — records already cached
            locally are skipped.
          </div>
        </div>
        <button
          type="button"
          class="action-button"
          :disabled="isRunning"
          @click="runSweep"
        >
          {{ isRunning ? 'Running…' : 'Run sweep' }}
        </button>
      </div>

      <div class="maintenance-row">
        <div class="maintenance-copy">
          <div class="maintenance-title">Find duplicate audiobooks</div>
          <div class="maintenance-help">
            Find audiobook rows that share the same ASIN and resolve each group:
            keep one, discard the rest (deletes their files and folders from disk),
            or just clear the ASIN on a row that got it stamped by mistake. A
            confirmation panel shows exactly what'll change before anything is
            applied.
          </div>
          <div v-if="lastDuplicatesMessage" class="maintenance-result">
            {{ lastDuplicatesMessage }}
          </div>
        </div>
        <button
          type="button"
          class="action-button"
          @click="showDuplicatesModal = true"
        >
          Review duplicates…
        </button>
      </div>

      <div class="maintenance-row">
        <div class="maintenance-copy">
          <div class="maintenance-title">Verify library audio</div>
          <div class="maintenance-help">
            Transcribe the opening and closing of each book with local
            speech-to-text and compare the spoken credits ("…by Author, narrated
            by Narrator") against the stored metadata, flagging entries whose
            audio doesn't match. Flag-only — nothing is deleted or re-searched.
            Safe to re-run: already-verified and manually-marked books are skipped.
          </div>
          <div v-if="verificationMessage" class="maintenance-result">
            {{ verificationMessage }}
          </div>
        </div>
        <button
          type="button"
          class="action-button"
          :disabled="isVerifying"
          @click="runVerification"
        >
          {{ isVerifying ? verifyProgressLabel : 'Verify library' }}
        </button>
      </div>

      <div class="maintenance-row">
        <div class="maintenance-copy">
          <div class="maintenance-title">Organize library folders</div>
          <div class="maintenance-help">
            Walk every audiobook and move it under its canonical
            <code>{Author}/{Title}</code> path using the configured Folder Naming Pattern.
            Preview-first: rows that already match, that need moving, that collide with
            another row at the same target, and that have unmoveable metadata are shown
            separately. Apply queues per-book moves through the existing background queue.
          </div>
          <div v-if="lastOrganizeMessage" class="maintenance-result">
            {{ lastOrganizeMessage }}
          </div>
        </div>
        <button
          type="button"
          class="action-button"
          @click="showOrganizeModal = true"
        >
          Organize library…
        </button>
      </div>
    </div>

    <DuplicatesReviewModal
      :visible="showDuplicatesModal"
      @close="showDuplicatesModal = false"
      @merged="onDuplicatesMerged"
    />

    <OrganizeLibraryModal
      :visible="showOrganizeModal"
      @close="showOrganizeModal = false"
      @organized="onOrganized"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onUnmounted, ref } from 'vue'
import { PhWrench } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { useToast } from '@/services/toastService'
import DuplicatesReviewModal from '@/components/domain/maintenance/DuplicatesReviewModal.vue'
import OrganizeLibraryModal from '@/components/domain/maintenance/OrganizeLibraryModal.vue'
import MoveQueueStatusBanner from '@/components/domain/maintenance/MoveQueueStatusBanner.vue'
import type { MergeDuplicatesResult, OrganizeLibraryApplyResult } from '@/types'

const toast = useToast()
const isRunning = ref(false)
const showDuplicatesModal = ref(false)
const lastDuplicatesMessage = ref<string | null>(null)
const showOrganizeModal = ref(false)
const lastOrganizeMessage = ref<string | null>(null)

// Audio verification (ADR-0001): trigger the batch walk and surface progress
const isVerifying = ref(false)
const verificationMessage = ref<string | null>(null)
const verifyProcessed = ref(0)
const verifyTotal = ref(0)
let verifyJobId: string | null = null

const verifyProgressLabel = computed(() =>
  verifyTotal.value > 0 ? `Verifying ${verifyProcessed.value}/${verifyTotal.value}…` : 'Verifying…',
)

const unsubVerifyProgress = signalRService.onVerificationProgress((payload) => {
  if (verifyJobId && payload.jobId !== verifyJobId) return
  isVerifying.value = true
  verifyProcessed.value = payload.processed
  verifyTotal.value = payload.total
})

const unsubVerifyComplete = signalRService.onVerificationComplete((payload) => {
  if (verifyJobId && payload.jobId !== verifyJobId) return
  isVerifying.value = false
  verifyJobId = null
  if (payload.error) {
    verificationMessage.value = null
    toast.error('Library verification failed', payload.error)
    return
  }
  const summary =
    `Checked ${payload.processed} book${payload.processed === 1 ? '' : 's'}: ` +
    `${payload.verified} verified, ${payload.flagged} flagged for review` +
    (payload.skipped > 0 ? `, ${payload.skipped} skipped` : '') +
    (payload.failed > 0 ? `, ${payload.failed} failed` : '') +
    '.'
  verificationMessage.value = summary
  if (payload.flagged > 0) {
    toast.warning('Library verification complete', `${summary} Use the "Needs review" filter in the library to triage flagged books.`)
  } else {
    toast.success('Library verification complete', summary)
  }
})

onUnmounted(() => {
  unsubVerifyProgress()
  unsubVerifyComplete()
})

async function runVerification() {
  if (isVerifying.value) return
  isVerifying.value = true
  verifyProcessed.value = 0
  verifyTotal.value = 0
  verificationMessage.value = null
  try {
    const result = await apiService.startLibraryVerification()
    verifyJobId = result.jobId
  } catch (err) {
    isVerifying.value = false
    const message = err instanceof Error ? err.message : 'Unknown error'
    toast.error('Could not start library verification', message)
  }
}

async function runSweep() {
  if (isRunning.value) return
  isRunning.value = true
  try {
    const result = await apiService.cacheExternalCovers()
    if (result.queued === 0) {
      toast.info(
        'Cover art sweep complete',
        `Nothing to do — all ${result.totalScanned} covers were already cached locally.`,
      )
    } else if (result.failed === 0) {
      toast.success(
        'Cover art sweep complete',
        `Cached ${result.succeeded} of ${result.queued} external covers (scanned ${result.totalScanned}).`,
      )
    } else {
      toast.warning(
        'Cover art sweep complete with failures',
        `Cached ${result.succeeded}/${result.queued}, ${result.failed} failed, ${result.alreadyLocal} already local. Check logs for failed records.`,
      )
    }
  } catch (err) {
    const message = err instanceof Error ? err.message : 'Unknown error'
    toast.error('Cover art sweep failed', message)
  } finally {
    isRunning.value = false
  }
}

function onDuplicatesMerged(result: MergeDuplicatesResult) {
  const parts: string[] = []
  parts.push(`Resolved ${result.groupsProcessed} group${result.groupsProcessed === 1 ? '' : 's'}`)
  if (result.rowsDeleted > 0) {
    parts.push(`discarded ${result.rowsDeleted} row${result.rowsDeleted === 1 ? '' : 's'}`)
  }
  if (result.diskFilesDeleted > 0) {
    parts.push(`deleted ${result.diskFilesDeleted} file${result.diskFilesDeleted === 1 ? '' : 's'} from disk`)
  }
  if (result.diskFoldersDeleted > 0) {
    parts.push(`removed ${result.diskFoldersDeleted} folder${result.diskFoldersDeleted === 1 ? '' : 's'}`)
  }
  if (result.asinsCleared > 0) {
    parts.push(`cleared ${result.asinsCleared} ASIN${result.asinsCleared === 1 ? '' : 's'}`)
  }
  if (result.downloadsReassigned > 0) {
    parts.push(`reassigned ${result.downloadsReassigned} downloads`)
  }
  if (result.historyReassigned > 0) {
    parts.push(`reassigned ${result.historyReassigned} history entries`)
  }
  lastDuplicatesMessage.value = parts.join(', ') + '.'

  const warningCount = result.warnings?.length ?? 0
  if (warningCount > 0) {
    toast.warning(
      `Duplicate cleanup complete with ${warningCount} warning${warningCount === 1 ? '' : 's'}`,
      `${lastDuplicatesMessage.value} Check server logs for details on the warnings.`,
    )
  } else {
    toast.success('Duplicate cleanup complete', lastDuplicatesMessage.value)
  }
}

function onOrganized(result: OrganizeLibraryApplyResult) {
  const parts: string[] = []
  parts.push(`Queued ${result.queued} move${result.queued === 1 ? '' : 's'}`)
  if (result.skipped > 0) parts.push(`${result.skipped} skipped`)
  if (result.failedToQueue > 0) parts.push(`${result.failedToQueue} failed to queue`)
  if (result.warnings.length > 0) {
    parts.push(`${result.warnings.length} warning${result.warnings.length === 1 ? '' : 's'}`)
  }
  lastOrganizeMessage.value = parts.join(', ') + '.'
}
</script>

<style scoped>
h3 {
  margin: 0 0 1.5rem 0;
  padding: 0;
  font-size: 1.1rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
}

.form-body {
  padding: 1.25rem;
  border-radius: 6px;
  border: 1px solid #333;
  box-shadow: 0 4px 14px rgba(0, 0, 0, 0.6);
  background-color: #232323;
  display: flex;
  flex-direction: column;
  gap: 1rem;
}

.maintenance-row {
  display: flex;
  align-items: flex-start;
  gap: 1rem;
}

.maintenance-row + .maintenance-row {
  padding-top: 1rem;
  border-top: 1px solid rgba(255, 255, 255, 0.06);
}

.maintenance-copy {
  flex: 1 1 auto;
  min-width: 0;
}

.maintenance-title {
  font-weight: 500;
  color: #fff;
}

.maintenance-help {
  margin-top: 0.35rem;
  font-size: 0.85rem;
  color: #adb5bd;
}

.maintenance-help code {
  background: rgba(255, 255, 255, 0.06);
  padding: 1px 4px;
  border-radius: 3px;
  font-size: 0.78rem;
}

.maintenance-result {
  margin-top: 0.35rem;
  font-size: 0.85rem;
  color: #6fc080;
}

.action-button {
  flex: 0 0 auto;
  padding: 0.5rem 1rem;
  background-color: #3a3a3a;
  color: #fff;
  border: 1px solid #555;
  border-radius: 4px;
  cursor: pointer;
  font-size: 0.9rem;
  white-space: nowrap;
}

.action-button:hover:not(:disabled) {
  background-color: #4a4a4a;
}

.action-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
</style>
