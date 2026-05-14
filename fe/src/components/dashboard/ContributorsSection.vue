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
<!-- Authors and narrators side by side: total counts plus top-N by book count. -->
<script setup lang="ts">
import { computed } from 'vue'
import DashboardChart from './DashboardChart.vue'
import { formatNumber } from './format'
import type { AuthorStats, NarratorStats } from '@/types'

const props = defineProps<{
  authors: AuthorStats
  narrators: NarratorStats
}>()

function topChartOptions(categories: string[], color: string) {
  return {
    chart: { type: 'bar' },
    plotOptions: { bar: { horizontal: true, borderRadius: 3, barHeight: '70%' } },
    xaxis: { categories },
    colors: [color],
    dataLabels: { enabled: true, style: { colors: ['#1a1a1a'] } },
  }
}

const authorSeries = computed(() => [
  { name: 'Books', data: props.authors.topAuthors.map((a) => a.count) },
])
const authorOptions = computed(() =>
  topChartOptions(
    props.authors.topAuthors.map((a) => a.author),
    '#2196f3',
  ),
)

const narratorSeries = computed(() => [
  { name: 'Books', data: props.narrators.topNarrators.map((n) => n.count) },
])
const narratorOptions = computed(() =>
  topChartOptions(
    props.narrators.topNarrators.map((n) => n.narrator),
    '#b197fc',
  ),
)
</script>

<template>
  <div class="contributors-grid">
    <div class="contributor-block">
      <h3>
        Authors
        <span class="total">{{ formatNumber(authors.totalAuthors) }} total</span>
      </h3>
      <DashboardChart
        v-if="authors.topAuthors.length"
        type="bar"
        :series="authorSeries"
        :options="authorOptions"
        :height="Math.max(200, authors.topAuthors.length * 26)"
      />
      <p v-else class="empty">No author data yet.</p>
    </div>

    <div class="contributor-block">
      <h3>
        Narrators
        <span class="total">{{ formatNumber(narrators.totalNarrators) }} total</span>
      </h3>
      <DashboardChart
        v-if="narrators.topNarrators.length"
        type="bar"
        :series="narratorSeries"
        :options="narratorOptions"
        :height="Math.max(200, narrators.topNarrators.length * 26)"
      />
      <p v-else class="empty">No narrator data yet.</p>
    </div>
  </div>
</template>

<style scoped>
.contributors-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 1.5rem;
}

.contributor-block h3 {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  margin: 0 0 0.5rem;
  color: #ccc;
  font-size: 0.95rem;
  font-weight: 500;
}

.contributor-block h3 .total {
  color: #777;
  font-size: 0.8rem;
  font-weight: 400;
}

.empty {
  color: #777;
  font-size: 0.85rem;
  font-style: italic;
}
</style>
