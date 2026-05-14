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
<!--
  Thin wrapper around vue3-apexcharts that applies the app's dark theme and
  brand palette in one place, so section components only supply their data and
  the handful of options that actually differ.
-->
<script setup lang="ts">
import { computed } from 'vue'
import VueApexCharts from 'vue3-apexcharts'

type ApexOptions = Record<string, unknown>

const props = withDefaults(
  defineProps<{
    type: 'bar' | 'line' | 'area' | 'donut' | 'radialBar'
    series: unknown
    options?: ApexOptions
    height?: number | string
  }>(),
  {
    options: () => ({}),
    height: 280,
  },
)

// App palette — mirrors the brand/status CSS variables in styles/base/base.css.
const PALETTE = [
  '#2196f3',
  '#51cf66',
  '#ffa500',
  '#ff6b6b',
  '#74c0fc',
  '#b197fc',
  '#ffd43b',
  '#63e6be',
  '#ff8787',
  '#a9e34b',
]

const baseOptions: ApexOptions = {
  chart: {
    background: 'transparent',
    toolbar: { show: false },
    foreColor: '#999',
    fontFamily: 'inherit',
    animations: { speed: 300 },
  },
  theme: { mode: 'dark' },
  colors: PALETTE,
  grid: { borderColor: '#333', strokeDashArray: 3 },
  tooltip: { theme: 'dark' },
  dataLabels: { enabled: false },
  legend: { labels: { colors: '#ccc' } },
  stroke: { curve: 'smooth', width: 2 },
  noData: { text: 'No data', style: { color: '#666' } },
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

// Shallow-recursive merge so callers can override nested option groups
// (e.g. chart.toolbar) without restating the whole base config.
function mergeOptions(base: ApexOptions, override: ApexOptions): ApexOptions {
  const result: Record<string, unknown> = { ...base }
  for (const [key, value] of Object.entries(override)) {
    const existing = result[key]
    result[key] =
      isPlainObject(existing) && isPlainObject(value)
        ? mergeOptions(existing, value)
        : value
  }
  return result
}

const mergedOptions = computed(() => mergeOptions(baseOptions, props.options))
</script>

<template>
  <VueApexCharts
    :type="props.type"
    :series="props.series"
    :options="mergedOptions"
    :height="props.height"
  />
</template>
