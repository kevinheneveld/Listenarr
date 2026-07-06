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
  <div class="settings-section">
    <h3><PhWrench /> Library Maintenance</h3>

    <!-- Live move-queue status: in-flight moves, queue depth, recent failures. -->
    <MoveQueueStatusBanner />

    <div class="maintenance-action">
      <div class="maintenance-action-text">
        <strong>Organize library</strong>
        <small>
          Preview every audiobook against the folder your naming pattern computes for it, then queue
          background moves for the ones you confirm. Read-only until you apply.
        </small>
      </div>
      <button type="button" class="action-button" @click="showOrganizeModal = true">
        <PhFolderOpen />
        Organize library…
      </button>
    </div>

    <div class="maintenance-action">
      <div class="maintenance-action-text">
        <strong>Cache external cover art</strong>
        <small>
          Downloads any audiobook cover still pointing at an external URL (e.g. an Amazon CDN link
          from before this feature shipped) into local library storage. Safe to re-run — records
          already cached locally are skipped.
        </small>
      </div>
      <button type="button" class="action-button" :disabled="sweepRunning" @click="runCoverSweep">
        <PhImages />
        {{ sweepRunning ? 'Running…' : 'Run sweep' }}
      </button>
    </div>

    <div class="maintenance-action">
      <div class="maintenance-action-text">
        <strong>Backfill metadata from files</strong>
        <small>
          Queue a force-refresh scan for every audiobook: blank library fields (cover, ASIN, ISBN,
          series, narrator…) are filled from the files' embedded tags. Existing values are never
          overwritten.
        </small>
      </div>
      <button type="button" class="action-button" :disabled="backfillRunning" @click="runBackfill">
        <PhArrowsClockwise />
        {{ backfillRunning ? 'Queueing…' : 'Backfill metadata' }}
      </button>
    </div>

    <details class="advanced-recovery">
      <summary><PhFirstAid /> Advanced recovery</summary>
      <p class="advanced-recovery-intro">
        Operator repair tools. Each runs a <strong>preview first</strong> and reports what it would
        change; you confirm before anything is written. Recovery scans carry a safety belt that
        prevents file-tracking deletions even if a recovered folder turns out empty.
      </p>

      <div v-for="op in recoveryOps" :key="op.id" class="maintenance-action">
        <div class="maintenance-action-text">
          <strong>{{ op.title }}</strong>
          <small>{{ op.description }}</small>
        </div>
        <button
          type="button"
          class="action-button"
          :disabled="recoveryBusy !== null"
          @click="runRecovery(op)"
        >
          {{ recoveryBusy === op.id ? 'Running…' : 'Preview…' }}
        </button>
      </div>

      <div class="maintenance-action">
        <div class="maintenance-action-text">
          <strong>Cancel stale move jobs</strong>
          <small>
            Cancel queued/processing move jobs with no activity for 5+ minutes — jobs wedged by a
            crashed worker. A cancelled job can be re-queued from the Organize modal.
          </small>
        </div>
        <button
          type="button"
          class="action-button"
          :disabled="recoveryBusy !== null"
          @click="runCancelStale"
        >
          {{ recoveryBusy === 'cancel-stale' ? 'Running…' : 'Cancel stale' }}
        </button>
      </div>
    </details>

    <OrganizeLibraryModal :visible="showOrganizeModal" @close="showOrganizeModal = false" />
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import {
  PhWrench,
  PhFolderOpen,
  PhImages,
  PhArrowsClockwise,
  PhFirstAid,
} from '@phosphor-icons/vue'
import { showConfirm } from '@/composables/useConfirm'
import OrganizeLibraryModal from '@/components/domain/organize/OrganizeLibraryModal.vue'
import MoveQueueStatusBanner from '@/components/domain/organize/MoveQueueStatusBanner.vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'

const showOrganizeModal = ref(false)

// --- Advanced recovery (dry-run-first operator tools) --------------------------------
type RecoveryAction =
  | 'recover-broken-moves'
  | 'recover-rootbase-audiobooks'
  | 'recover-orphaned-tracking'
  | 'cleanup-phantom-rows'

interface RecoveryOp {
  id: RecoveryAction | 'orphan-tmp'
  title: string
  description: string
}

const recoveryOps: RecoveryOp[] = [
  {
    id: 'recover-broken-moves',
    title: 'Recover broken moves',
    description:
      'Books left empty by a failed move: restore their folder from move history (only when it still exists on disk with content) and re-scan.',
  },
  {
    id: 'recover-rootbase-audiobooks',
    title: 'Recover root-based books',
    description:
      'Books whose folder is set to a bare library root: compute or find their real folder and re-scan.',
  },
  {
    id: 'recover-orphaned-tracking',
    title: 'Recover orphaned tracking',
    description:
      'Books whose folder exists with audio on disk but which track zero files: re-scan to re-register the files.',
  },
  {
    id: 'cleanup-phantom-rows',
    title: 'Clean up phantom rows',
    description:
      'Duplicate zero-file rows shadowing a book that owns files: their history moves to the real record and the phantom is deleted.',
  },
  {
    id: 'orphan-tmp',
    title: 'Clean up orphan move staging',
    description:
      'Leftover .tmp staging folders from interrupted moves: preview their size, then delete them to reclaim disk space.',
  },
]

const recoveryBusy = ref<string | null>(null)
const backfillRunning = ref(false)

function summarizeRecovery(result: Record<string, unknown>): string {
  return Object.entries(result)
    .filter(([k, v]) => typeof v === 'number' && k !== 'dryRun')
    .map(([k, v]) => `${k}: ${v}`)
    .join(', ')
}

async function runRecovery(op: RecoveryOp) {
  if (recoveryBusy.value) return
  recoveryBusy.value = op.id
  try {
    const call = (dryRun: boolean) =>
      op.id === 'orphan-tmp'
        ? apiService.cleanupOrphanMoveTmp(dryRun)
        : apiService.runLibraryRecovery(op.id as RecoveryAction, dryRun)

    const preview = await call(true)
    const summary = summarizeRecovery(preview)
    const wouldAct =
      Number(preview.recovered ?? 0) + Number(preview.merges ?? 0) + Number(preview.found ?? 0) > 0
    if (!wouldAct) {
      toast.success(op.title, `Nothing to do. ${summary}`)
      return
    }

    const ok = await showConfirm(
      `Preview: ${summary}.

Apply these changes now?`,
      op.title,
      { danger: true, confirmText: 'Apply', cancelText: 'Cancel' },
    )
    if (!ok) return

    const applied = await call(false)
    toast.success(`${op.title} applied`, summarizeRecovery(applied))
  } catch (err) {
    toast.error(op.title, err instanceof Error ? err.message : 'Operation failed')
  } finally {
    recoveryBusy.value = null
  }
}

async function runCancelStale() {
  if (recoveryBusy.value) return
  recoveryBusy.value = 'cancel-stale'
  try {
    const result = await apiService.cancelStaleMoveJobs(5)
    toast.success('Cancel stale move jobs', `Cancelled ${result.cancelled} job(s).`)
  } catch (err) {
    toast.error('Cancel stale move jobs', err instanceof Error ? err.message : 'Operation failed')
  } finally {
    recoveryBusy.value = null
  }
}

async function runBackfill() {
  if (backfillRunning.value) return
  const ok = await showConfirm(
    'Queue a metadata-backfill scan for every audiobook? Blank fields are filled from file tags; existing values are never overwritten. This runs in the background.',
    'Backfill metadata from files',
    { confirmText: 'Queue backfill', cancelText: 'Cancel' },
  )
  if (!ok) return
  backfillRunning.value = true
  try {
    const result = await apiService.backfillLibraryMetadata()
    toast.success('Backfill queued', result.message)
  } catch (err) {
    toast.error('Backfill metadata', err instanceof Error ? err.message : 'Operation failed')
  } finally {
    backfillRunning.value = false
  }
}

// Cover-art sweep: one-shot, backend-idempotent. Button disables while running.
const sweepRunning = ref(false)
const toast = useToast()

async function runCoverSweep() {
  if (sweepRunning.value) return
  sweepRunning.value = true
  try {
    const result = await apiService.cacheExternalCovers()
    if (result.queued === 0) {
      toast.success(
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
    sweepRunning.value = false
  }
}
</script>

<style scoped>
.settings-section {
  margin-top: 2rem;
}

.settings-section h3 {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
  font-size: 1.15rem;
  font-weight: 500;
  margin: 0 0 1rem;
}

.maintenance-action {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  flex-wrap: wrap;
  padding: 1rem 1.25rem;
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 8px;
  margin-top: 1rem;
}

.maintenance-action-text {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.maintenance-action-text strong {
  color: #fff;
}

.maintenance-action-text small {
  color: #8a93a0;
  max-width: 46rem;
  line-height: 1.5;
}

.action-button {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.6rem 1.1rem;
  background: rgba(var(--brand-rgb), 0.1);
  border: 1px solid var(--brand-500);
  border-radius: 6px;
  color: var(--brand-500);
  font-size: 0.95rem;
  cursor: pointer;
  transition: all 0.2s;
  white-space: nowrap;
}

.action-button:hover {
  background: rgba(var(--brand-rgb), 0.2);
}
.advanced-recovery {
  margin-top: 1rem;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 6px;
  padding: 0.75rem 1rem;
}

.advanced-recovery summary {
  cursor: pointer;
  color: #ccc;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.advanced-recovery-intro {
  color: #868e96;
  font-size: 0.85rem;
  margin: 0.75rem 0;
  line-height: 1.5;
}
</style>
