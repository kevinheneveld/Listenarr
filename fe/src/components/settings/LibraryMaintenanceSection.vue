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

    <OrganizeLibraryModal :visible="showOrganizeModal" @close="showOrganizeModal = false" />
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { PhWrench, PhFolderOpen, PhImages } from '@phosphor-icons/vue'
import OrganizeLibraryModal from '@/components/domain/organize/OrganizeLibraryModal.vue'
import MoveQueueStatusBanner from '@/components/domain/organize/MoveQueueStatusBanner.vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'

const showOrganizeModal = ref(false)

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
</style>
