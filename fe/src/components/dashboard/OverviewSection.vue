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
<!-- Book-centric overview: leads with books actually owned vs. tracked-but-missing. -->
<script setup lang="ts">
import { computed } from 'vue'
import {
  PhBooks,
  PhBookOpen,
  PhWarningCircle,
  PhEye,
  PhEyeSlash,
  PhHardDrives,
  PhClock,
  PhTimer,
} from '@phosphor-icons/vue'
import MetricKpiCard from './MetricKpiCard.vue'
import { formatBytes, formatHours, formatNumber } from './format'
import type { LibraryOverviewStats } from '@/types'

const props = defineProps<{ overview: LibraryOverviewStats }>()

const o = computed(() => props.overview)
const ownedPercent = computed(() =>
  o.value.totalBooks > 0 ? Math.round((100 * o.value.ownedBooks) / o.value.totalBooks) : 0,
)
</script>

<template>
  <div class="kpi-grid">
    <MetricKpiCard :icon="PhBooks" label="Total books tracked" :value="formatNumber(o.totalBooks)" />
    <MetricKpiCard
      :icon="PhBookOpen"
      label="Books I have"
      :value="formatNumber(o.ownedBooks)"
      :sublabel="`${ownedPercent}% of tracked`"
      tone="success"
    />
    <MetricKpiCard
      :icon="PhWarningCircle"
      label="Books missing"
      :value="formatNumber(o.missingBooks)"
      sublabel="Tracked, no file yet"
      :tone="o.missingBooks > 0 ? 'warning' : 'success'"
      :to="{ path: '/audiobooks', query: { group: 'books', missing: 'files' } }"
    />
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
    <MetricKpiCard
      :icon="PhHardDrives"
      label="Library size"
      :value="formatBytes(o.totalSizeBytes)"
    />
    <MetricKpiCard
      :icon="PhClock"
      label="Owned duration"
      :value="formatHours(o.totalDurationHours)"
      sublabel="Across books I have"
    />
    <MetricKpiCard
      :icon="PhTimer"
      label="Average length"
      :value="formatHours(o.averageDurationHours)"
      sublabel="Per owned book"
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
