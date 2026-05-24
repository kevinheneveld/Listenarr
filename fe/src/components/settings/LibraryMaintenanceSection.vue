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
  <section class="settings-section">
    <h3 class="section-title">Library Maintenance</h3>

    <div class="maintenance-action">
      <div class="action-text">
        <div class="action-label">Find duplicate audiobooks</div>
        <p class="action-help">
          Find audiobook rows that share the same ASIN. Pick which row to keep per group;
          the rest are removed (files on disk are not touched).
        </p>
        <p v-if="lastResultMessage" class="action-result">{{ lastResultMessage }}</p>
      </div>
      <button type="button" class="action-btn" @click="showModal = true">
        Review duplicates…
      </button>
    </div>

    <DuplicatesReviewModal
      :visible="showModal"
      @close="showModal = false"
      @merged="onMerged"
    />
  </section>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import DuplicatesReviewModal from '@/components/domain/maintenance/DuplicatesReviewModal.vue'
import type { MergeDuplicatesResult } from '@/types'

const showModal = ref(false)
const lastResultMessage = ref<string | null>(null)

function onMerged(result: MergeDuplicatesResult) {
  const parts: string[] = []
  parts.push(`Merged ${result.groupsProcessed} group${result.groupsProcessed === 1 ? '' : 's'}`)
  parts.push(`removed ${result.rowsDeleted} row${result.rowsDeleted === 1 ? '' : 's'}`)
  if (result.downloadsReassigned > 0) {
    parts.push(`reassigned ${result.downloadsReassigned} downloads`)
  }
  if (result.historyReassigned > 0) {
    parts.push(`reassigned ${result.historyReassigned} history entries`)
  }
  lastResultMessage.value = parts.join(', ') + '.'
}
</script>

<style scoped>
.settings-section {
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  padding: 14px 16px;
  margin-bottom: 16px;
}
.section-title {
  font-size: 14px;
  margin: 0 0 12px;
  color: #fff;
}
.maintenance-action {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
  flex-wrap: wrap;
}
.action-text {
  flex: 1;
  min-width: 240px;
}
.action-label {
  font-size: 13px;
  color: #ddd;
  margin-bottom: 2px;
}
.action-help {
  font-size: 12px;
  color: #999;
  margin: 0;
  line-height: 1.4;
}
.action-result {
  font-size: 12px;
  color: #6fc080;
  margin: 6px 0 0;
}
.action-btn {
  padding: 6px 12px;
  border-radius: 4px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.04);
  color: #ddd;
  font-size: 13px;
  cursor: pointer;
  white-space: nowrap;
}
.action-btn:hover {
  background: rgba(255, 255, 255, 0.08);
}
</style>
