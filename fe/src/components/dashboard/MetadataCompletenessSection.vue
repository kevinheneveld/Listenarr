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
<!-- Overall completeness gauge plus a per-field "missing" bar chart. Each bar
     is a candidate set for a future backfill action (drill-down hook). -->
<script setup lang="ts">
import { computed } from 'vue'
import DashboardChart from './DashboardChart.vue'
import type { MetadataCompletenessStats } from '@/types'

const props = defineProps<{ completeness: MetadataCompletenessStats }>()

const c = computed(() => props.completeness)

// Ordered worst-first so the most-missing field is most prominent.
const missingFields = computed(() => {
  const fields: { label: string; count: number }[] = [
    { label: 'Cover art', count: c.value.missingCoverArt },
    { label: 'ASIN', count: c.value.missingAsin },
    { label: 'ISBN', count: c.value.missingIsbn },
    { label: 'Genres', count: c.value.missingGenres },
    { label: 'Narrators', count: c.value.missingNarrators },
    { label: 'Description', count: c.value.missingDescription },
    { label: 'Publisher', count: c.value.missingPublisher },
    { label: 'Language', count: c.value.missingLanguage },
    { label: 'Publish date', count: c.value.missingPublishDate },
    { label: 'Runtime', count: c.value.missingRuntime },
    { label: 'Series position', count: c.value.missingSeriesPosition },
  ]
  return fields.sort((a, b) => b.count - a.count)
})

const gaugeSeries = computed(() => [c.value.overallCompletenessPercent])
const gaugeOptions = {
  chart: { type: 'radialBar' },
  plotOptions: {
    radialBar: {
      hollow: { size: '60%' },
      track: { background: '#333' },
      dataLabels: {
        name: { show: true, color: '#999', fontSize: '0.85rem', offsetY: 22 },
        value: {
          show: true,
          color: '#fff',
          fontSize: '2rem',
          fontWeight: 600,
          offsetY: -12,
          formatter: (val: number) => `${val}%`,
        },
      },
    },
  },
  labels: ['Overall completeness'],
  stroke: { lineCap: 'round' },
}

const barSeries = computed(() => [
  { name: 'Books missing this field', data: missingFields.value.map((f) => f.count) },
])
const barOptions = computed(() => ({
  chart: { type: 'bar' },
  plotOptions: { bar: { horizontal: true, borderRadius: 3, barHeight: '65%' } },
  xaxis: { categories: missingFields.value.map((f) => f.label) },
  colors: ['#ffa500'],
  dataLabels: {
    enabled: true,
    style: { colors: ['#1a1a1a'] },
  },
}))
</script>

<template>
  <div class="completeness-layout">
    <div class="gauge">
      <DashboardChart type="radialBar" :series="gaugeSeries" :options="gaugeOptions" :height="260" />
      <p class="gauge-caption">
        Averaged across 10 core metadata fields for {{ c.totalBooks.toLocaleString() }} books.
      </p>
    </div>
    <div class="missing-chart">
      <h3>Books missing each field</h3>
      <DashboardChart type="bar" :series="barSeries" :options="barOptions" :height="360" />
    </div>
  </div>
</template>

<style scoped>
.completeness-layout {
  display: grid;
  grid-template-columns: minmax(260px, 1fr) minmax(0, 2fr);
  gap: 1.5rem;
  align-items: start;
}

.gauge {
  text-align: center;
}

.gauge-caption {
  color: #777;
  font-size: 0.8rem;
  margin: 0.5rem 0 0;
}

.missing-chart h3 {
  margin: 0 0 0.5rem;
  color: #ccc;
  font-size: 0.95rem;
  font-weight: 500;
}

@media (max-width: 900px) {
  .completeness-layout {
    grid-template-columns: 1fr;
  }
}
</style>
