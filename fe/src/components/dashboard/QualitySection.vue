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
<!-- File-level quality breakdown: codec mix (donut) and bitrate buckets (bar). -->
<script setup lang="ts">
import { computed } from 'vue'
import DashboardChart from './DashboardChart.vue'
import type { QualityStats } from '@/types'

const props = defineProps<{ quality: QualityStats }>()

const hasCodecs = computed(() => props.quality.byCodec.length > 0)
const hasBitrates = computed(() => props.quality.byBitrate.length > 0)

const codecSeries = computed(() => props.quality.byCodec.map((c) => c.count))
const codecOptions = computed(() => ({
  chart: { type: 'donut' },
  labels: props.quality.byCodec.map((c) => c.codec),
  legend: { position: 'bottom' },
  plotOptions: { pie: { donut: { size: '62%' } } },
}))

const bitrateSeries = computed(() => [
  { name: 'Files', data: props.quality.byBitrate.map((b) => b.count) },
])
const bitrateOptions = computed(() => ({
  chart: { type: 'bar' },
  plotOptions: { bar: { borderRadius: 3, columnWidth: '55%' } },
  xaxis: { categories: props.quality.byBitrate.map((b) => b.label) },
  dataLabels: { enabled: true, style: { colors: ['#fff'] } },
}))
</script>

<template>
  <div class="quality-grid">
    <div class="quality-block">
      <h3>By codec</h3>
      <DashboardChart
        v-if="hasCodecs"
        type="donut"
        :series="codecSeries"
        :options="codecOptions"
        :height="300"
      />
      <p v-else class="empty">No file codec data yet.</p>
    </div>
    <div class="quality-block">
      <h3>By bitrate</h3>
      <DashboardChart
        v-if="hasBitrates"
        type="bar"
        :series="bitrateSeries"
        :options="bitrateOptions"
        :height="300"
      />
      <p v-else class="empty">No file bitrate data yet.</p>
    </div>
  </div>
</template>

<style scoped>
.quality-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 1.5rem;
}

.quality-block h3 {
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
