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
<!-- Books-added time-series at adjustable granularity, plus import success KPIs.
     The granularity toggle is exposed via the #toggle slot target so the parent
     section can place it in the section header's actions area. -->
<script setup lang="ts">
import { computed } from 'vue'
import { PhCheckCircle, PhXCircle } from '@phosphor-icons/vue'
import MetricKpiCard from './MetricKpiCard.vue'
import DashboardChart from './DashboardChart.vue'
import { formatNumber } from './format'
import type { ActivityStats, ActivityGranularity } from '@/types'

const props = defineProps<{ activity: ActivityStats }>()
const emit = defineEmits<{ (e: 'change-granularity', value: ActivityGranularity): void }>()

const granularities: ActivityGranularity[] = ['Day', 'Week', 'Month']

// Two overlaid series so you can see the queue → arrival lag between when a
// book was requested (added to the library) and when its file actually landed.
const series = computed(() => [
  { name: 'Requested', data: props.activity.booksAddedByPeriod.map((b) => b.count) },
  { name: 'Imported', data: props.activity.booksImportedByPeriod.map((b) => b.count) },
])
const options = computed(() => ({
  chart: { type: 'area' },
  colors: ['#74c0fc', '#51cf66'],
  xaxis: {
    // Both series share the same period bucketing; either label list works.
    categories: props.activity.booksAddedByPeriod.map((b) => b.label),
    labels: { rotate: -45, hideOverlappingLabels: true },
  },
  fill: {
    type: 'gradient',
    gradient: { shadeIntensity: 0.4, opacityFrom: 0.45, opacityTo: 0.05 },
  },
  markers: { size: 3 },
  legend: { position: 'top' as const },
  tooltip: { shared: true, y: { formatter: (v: number) => `${v} books` } },
}))
</script>

<template>
  <div class="activity-toggle">
    <button
      v-for="g in granularities"
      :key="g"
      type="button"
      class="toggle-btn"
      :class="{ active: activity.granularity === g }"
      @click="emit('change-granularity', g)"
    >
      {{ g }}
    </button>
  </div>

  <DashboardChart type="area" :series="series" :options="options" :height="300" />

  <div class="kpi-grid">
    <MetricKpiCard
      :icon="PhCheckCircle"
      label="Import success rate"
      :value="`${activity.importSuccessRate}%`"
      :sublabel="`${formatNumber(activity.totalImports)} imported`"
      :tone="activity.importSuccessRate >= 90 ? 'success' : 'warning'"
    />
    <MetricKpiCard
      :icon="PhXCircle"
      label="Failed imports"
      :value="formatNumber(activity.failedImports)"
      :tone="activity.failedImports > 0 ? 'danger' : 'success'"
    />
  </div>
</template>

<style scoped>
.activity-toggle {
  display: inline-flex;
  gap: 0;
  border: 1px solid #333;
  border-radius: 6px;
  overflow: hidden;
  align-self: flex-start;
}

.toggle-btn {
  padding: 0.4rem 0.9rem;
  background: #232323;
  color: #999;
  border: none;
  border-right: 1px solid #333;
  cursor: pointer;
  font-size: 0.85rem;
  transition:
    background 0.15s,
    color 0.15s;
}

.toggle-btn:last-child {
  border-right: none;
}

.toggle-btn:hover {
  background: #2a2a2a;
  color: #ccc;
}

.toggle-btn.active {
  background: var(--brand-focus);
  color: #fff;
}

.kpi-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 1rem;
}
</style>
