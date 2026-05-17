<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.
-->
<template>
  <div class="form-section">
    <h3><PhWrench /> Library Maintenance</h3>
    <div class="form-body">
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
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { PhWrench } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'

const toast = useToast()
const isRunning = ref(false)

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
}

.maintenance-row {
  display: flex;
  align-items: flex-start;
  gap: 1rem;
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

.action-button {
  flex: 0 0 auto;
  padding: 0.5rem 1rem;
  background-color: #3a3a3a;
  color: #fff;
  border: 1px solid #555;
  border-radius: 4px;
  cursor: pointer;
  font-size: 0.9rem;
}

.action-button:hover:not(:disabled) {
  background-color: #4a4a4a;
}

.action-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}
</style>
