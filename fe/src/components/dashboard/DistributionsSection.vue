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
<!-- Genre, duration, and language distributions. -->
<script setup lang="ts">
import { computed } from 'vue'
import DashboardChart from './DashboardChart.vue'
import type { GenreCount, DurationBucket, LanguageCount } from '@/types'

const props = defineProps<{
  topGenres: GenreCount[]
  durationDistribution: DurationBucket[]
  languages: LanguageCount[]
}>()

const hasGenres = computed(() => props.topGenres.length > 0)
const hasLanguages = computed(() => props.languages.length > 0)
const hasDurations = computed(() => props.durationDistribution.some((d) => d.totalBooks > 0))

// Shared stacked have/missing colors — same palette the authors/narrators
// section uses, so the dashboard reads consistently.
const STACK_COLORS = ['#51cf66', '#ffa500']

const genreSeries = computed(() => [
  { name: 'Have', data: props.topGenres.map((g) => g.ownedBooks) },
  { name: 'Missing', data: props.topGenres.map((g) => g.totalBooks - g.ownedBooks) },
])
const genreOptions = computed(() => ({
  chart: { type: 'bar', stacked: true },
  plotOptions: { bar: { horizontal: true, borderRadius: 3, barHeight: '70%' } },
  xaxis: { categories: props.topGenres.map((g) => g.genre) },
  colors: STACK_COLORS,
  legend: { position: 'top' as const },
  tooltip: { y: { formatter: (v: number) => `${v} books` } },
}))

const durationSeries = computed(() => [
  { name: 'Have', data: props.durationDistribution.map((d) => d.ownedBooks) },
  {
    name: 'Missing',
    data: props.durationDistribution.map((d) => d.totalBooks - d.ownedBooks),
  },
])
const durationOptions = computed(() => ({
  chart: { type: 'bar', stacked: true },
  plotOptions: { bar: { borderRadius: 3, columnWidth: '55%' } },
  xaxis: { categories: props.durationDistribution.map((d) => d.label) },
  colors: STACK_COLORS,
  legend: { position: 'top' as const },
  tooltip: { y: { formatter: (v: number) => `${v} books` } },
}))

const languageSeries = computed(() => props.languages.map((l) => l.count))
const languageOptions = computed(() => ({
  chart: { type: 'donut' },
  labels: props.languages.map((l) => l.language),
  legend: { position: 'bottom' },
  plotOptions: { pie: { donut: { size: '62%' } } },
}))
</script>

<template>
  <div class="dist-grid">
    <div class="dist-block wide">
      <h3>Top genres</h3>
      <DashboardChart
        v-if="hasGenres"
        type="bar"
        :series="genreSeries"
        :options="genreOptions"
        :height="Math.max(220, topGenres.length * 26)"
      />
      <p v-else class="empty">No genre data yet.</p>
    </div>
    <div class="dist-block">
      <h3>Duration distribution</h3>
      <DashboardChart
        v-if="hasDurations"
        type="bar"
        :series="durationSeries"
        :options="durationOptions"
        :height="300"
      />
      <p v-else class="empty">No duration data yet.</p>
    </div>
    <div class="dist-block">
      <h3>Languages</h3>
      <DashboardChart
        v-if="hasLanguages"
        type="donut"
        :series="languageSeries"
        :options="languageOptions"
        :height="300"
      />
      <p v-else class="empty">No language data yet.</p>
    </div>
  </div>
</template>

<style scoped>
.dist-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 1.5rem;
}

.dist-block.wide {
  grid-column: 1 / -1;
}

.dist-block h3 {
  margin: 0 0 0.5rem;
  color: #ccc;
  font-size: 0.95rem;
  font-weight: 500;
}

.empty {
  color: #777;
  font-size: 0.85rem;
  font-style: italic;
}
</style>
