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
import { PhStack, PhCheckCircle, PhWarningCircle, PhQuestion, PhBookmark } from '@phosphor-icons/vue'
import MetricKpiCard from './MetricKpiCard.vue'
import DashboardChart from './DashboardChart.vue'
import { formatNumber } from './format'
import type { SeriesStats } from '@/types'

const props = defineProps<{ series: SeriesStats }>()

const s = computed(() => props.series)

const donutSeries = computed(() => [
  s.value.completeSeries,
  s.value.incompleteSeries,
  s.value.unknownCompletenessSeries,
])
const donutOptions = {
  chart: { type: 'donut' },
  labels: ['Complete', 'Incomplete', 'Completeness unknown'],
  colors: ['#51cf66', '#ffa500', '#666'],
  legend: { position: 'bottom' },
  plotOptions: { pie: { donut: { size: '62%' } } },
}

const hasSeries = computed(() => s.value.totalSeries > 0)

// Audible labels many standalone books as 1-member "series"; those are folded
// into the standalone count rather than counted as real series.
const standaloneSublabel = computed(() =>
  s.value.singleBookSeriesFolded > 0
    ? `incl. ${s.value.singleBookSeriesFolded.toLocaleString()} single-book Audible series`
    : `${s.value.booksInSeries.toLocaleString()} in a series`,
)
</script>

<template>
  <div class="kpi-grid">
    <MetricKpiCard :icon="PhStack" label="Total series" :value="formatNumber(s.totalSeries)" />
    <MetricKpiCard
      :icon="PhCheckCircle"
      label="Complete series"
      :value="formatNumber(s.completeSeries)"
      tone="success"
    />
    <MetricKpiCard
      :icon="PhWarningCircle"
      label="Incomplete series"
      :value="formatNumber(s.incompleteSeries)"
      :sublabel="`${formatNumber(s.missingBooksAcrossSeries)} books missing`"
      :tone="s.incompleteSeries > 0 ? 'warning' : 'default'"
    />
    <MetricKpiCard
      :icon="PhQuestion"
      label="Completeness unknown"
      :value="formatNumber(s.unknownCompletenessSeries)"
      sublabel="No cached catalog"
    />
    <MetricKpiCard
      :icon="PhBookmark"
      label="Standalone books"
      :value="formatNumber(s.standaloneBooks)"
      :sublabel="standaloneSublabel"
    />
  </div>

  <div v-if="hasSeries" class="donut-wrap">
    <DashboardChart type="donut" :series="donutSeries" :options="donutOptions" :height="300" />
    <p class="caption">
      Completeness compares owned books against the cached Audible series catalog. Series with no
      cached catalog are counted as "unknown" rather than guessed.
    </p>
  </div>
</template>

<style scoped>
.kpi-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 1rem;
}

.donut-wrap {
  max-width: 480px;
  margin: 0 auto;
  text-align: center;
}

.caption {
  color: #777;
  font-size: 0.8rem;
  margin: 0.25rem 0 0;
}
</style>
