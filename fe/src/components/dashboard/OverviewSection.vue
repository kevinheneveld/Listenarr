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
import { computed } from 'vue'
import {
  PhBooks,
  PhEye,
  PhEyeSlash,
  PhFiles,
  PhWarningCircle,
  PhHardDrives,
  PhClock,
  PhTimer,
} from '@phosphor-icons/vue'
import MetricKpiCard from './MetricKpiCard.vue'
import { formatBytes, formatHours, formatNumber } from './format'
import type { LibraryOverviewStats } from '@/types'

const props = defineProps<{ overview: LibraryOverviewStats }>()

const o = computed(() => props.overview)
</script>

<template>
  <div class="kpi-grid">
    <MetricKpiCard :icon="PhBooks" label="Total books" :value="formatNumber(o.totalBooks)" />
    <MetricKpiCard
      :icon="PhEye"
      label="Monitored"
      :value="formatNumber(o.monitoredBooks)"
      :sublabel="`${formatNumber(o.unmonitoredBooks)} unmonitored`"
    />
    <MetricKpiCard
      :icon="PhEyeSlash"
      label="Unmonitored"
      :value="formatNumber(o.unmonitoredBooks)"
      :tone="o.unmonitoredBooks > 0 ? 'warning' : 'default'"
    />
    <MetricKpiCard :icon="PhFiles" label="Audio files" :value="formatNumber(o.totalFiles)" />
    <MetricKpiCard
      :icon="PhWarningCircle"
      label="Books without files"
      :value="formatNumber(o.booksWithoutFiles)"
      :sublabel="`${formatNumber(o.booksWithFiles)} have files`"
      :tone="o.booksWithoutFiles > 0 ? 'warning' : 'success'"
    />
    <MetricKpiCard
      :icon="PhHardDrives"
      label="Library size"
      :value="formatBytes(o.totalSizeBytes)"
    />
    <MetricKpiCard
      :icon="PhClock"
      label="Total duration"
      :value="formatHours(o.totalDurationHours)"
    />
    <MetricKpiCard
      :icon="PhTimer"
      label="Average length"
      :value="formatHours(o.averageDurationHours)"
    />
  </div>
</template>

<style scoped>
.kpi-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 1rem;
}
</style>
