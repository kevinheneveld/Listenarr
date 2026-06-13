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
<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { storeToRefs } from 'pinia'
import { RouterLink } from 'vue-router'
import { PhCircleNotch, PhMagnifyingGlass, PhCheckCircle, PhCircleDashed } from '@phosphor-icons/vue'
import { useSearchActivityStore } from '@/stores/searchActivity'

const store = useSearchActivityStore()
const { current, recent, isSearching } = storeToRefs(store)

onMounted(() => store.start())

const currentLabel = computed(() => {
  const c = current.value
  if (!c) return 'Automatic search is idle'
  if (c.stage === 'idle') return c.message || 'Automatic search is idle'
  return c.message
})

function formatTime(ts: string): string {
  const d = new Date(ts)
  if (Number.isNaN(d.getTime())) return ''
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}
</script>

<template>
  <div class="search-activity-section">
    <div class="current-line" :class="{ active: isSearching }">
      <span class="current-icon">
        <PhCircleNotch v-if="isSearching" class="spin" :size="18" weight="bold" />
        <PhMagnifyingGlass v-else :size="18" />
      </span>
      <span class="current-text">{{ currentLabel }}</span>
    </div>

    <p class="section-help">
      The automatic search runs every 6 hours over your monitored-but-missing books, pacing
      between books so it doesn't flood your indexers. Recent results appear below.
    </p>

    <ul v-if="recent.length > 0" class="recent-list">
      <li v-for="(evt, i) in recent" :key="`${evt.timestamp}-${i}`" class="recent-item">
        <span class="recent-icon" :class="evt.stage">
          <PhCheckCircle v-if="evt.stage === 'grabbed'" :size="16" weight="fill" />
          <PhCircleDashed v-else :size="16" />
        </span>
        <component
          :is="evt.audiobookId ? RouterLink : 'span'"
          :to="evt.audiobookId ? `/audiobooks/${evt.audiobookId}` : undefined"
          class="recent-message"
          >{{ evt.message }}</component
        >
        <span class="recent-time">{{ formatTime(evt.timestamp) }}</span>
      </li>
    </ul>
    <p v-else class="recent-empty">No recent search results yet.</p>
  </div>
</template>

<style scoped>
.search-activity-section {
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
}

.current-line {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.7rem 0.9rem;
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid rgba(255, 255, 255, 0.06);
  color: #c8cfd9;
  font-weight: 500;
}

.current-line.active {
  color: var(--brand, #5aa9e6);
  background: rgba(var(--brand-rgb, 90, 169, 230), 0.1);
  border-color: rgba(var(--brand-rgb, 90, 169, 230), 0.35);
}

.current-icon {
  display: inline-flex;
  flex: 0 0 auto;
}

.current-text {
  overflow: hidden;
  text-overflow: ellipsis;
}

.section-help {
  margin: 0;
  font-size: 0.8rem;
  color: #8b94a3;
  line-height: 1.4;
}

.recent-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
}

.recent-item {
  display: flex;
  align-items: center;
  gap: 0.55rem;
  padding: 0.45rem 0.25rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.05);
  font-size: 0.85rem;
}

.recent-item:last-child {
  border-bottom: none;
}

.recent-icon {
  display: inline-flex;
  flex: 0 0 auto;
}

.recent-icon.grabbed {
  color: #4caf7d;
}

.recent-icon.no_results {
  color: #7a8290;
}

.recent-message {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: #c8cfd9;
  text-decoration: none;
}

a.recent-message:hover {
  color: var(--brand, #5aa9e6);
  text-decoration: underline;
}

.recent-time {
  flex: 0 0 auto;
  color: #6f7785;
  font-variant-numeric: tabular-nums;
  font-size: 0.78rem;
}

.recent-empty {
  margin: 0;
  color: #8b94a3;
  font-size: 0.85rem;
}

.spin {
  animation: sas-rotate 0.9s linear infinite;
}

@keyframes sas-rotate {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}

@media (prefers-reduced-motion: reduce) {
  .spin {
    animation: none;
  }
}
</style>
