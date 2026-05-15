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
<!-- Authors and narrators side by side. Each top-N bar is stacked: books the
     user actually has vs. tracked-but-missing — so a big "400 books" number
     doesn't hide that only a handful are owned. -->
<script setup lang="ts">
import { computed } from 'vue'
import DashboardChart from './DashboardChart.vue'
import { formatNumber } from './format'
import type { AuthorStats, NarratorStats } from '@/types'

const props = defineProps<{
  authors: AuthorStats
  narrators: NarratorStats
}>()

// Shared stacked-bar options: green "have" + amber "missing".
function stackedOptions(categories: string[]) {
  return {
    chart: { type: 'bar', stacked: true },
    plotOptions: { bar: { horizontal: true, borderRadius: 3, barHeight: '70%' } },
    xaxis: { categories },
    colors: ['#51cf66', '#ffa500'],
    legend: { position: 'top' as const },
    tooltip: { y: { formatter: (v: number) => `${v} books` } },
  }
}

const authorCategories = computed(() => props.authors.topAuthors.map((a) => a.author))
const authorSeries = computed(() => [
  { name: 'Have', data: props.authors.topAuthors.map((a) => a.ownedBooks) },
  {
    name: 'Missing',
    data: props.authors.topAuthors.map((a) => a.totalBooks - a.ownedBooks),
  },
])

const narratorCategories = computed(() => props.narrators.topNarrators.map((n) => n.narrator))
const narratorSeries = computed(() => [
  { name: 'Have', data: props.narrators.topNarrators.map((n) => n.ownedBooks) },
  {
    name: 'Missing',
    data: props.narrators.topNarrators.map((n) => n.totalBooks - n.ownedBooks),
  },
])
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
        :options="stackedOptions(authorCategories)"
        :height="Math.max(220, authors.topAuthors.length * 28)"
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
        :options="stackedOptions(narratorCategories)"
        :height="Math.max(220, narrators.topNarrators.length * 28)"
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
