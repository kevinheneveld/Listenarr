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
import { computed, onMounted } from 'vue'
import { storeToRefs } from 'pinia'
import {
  PhChartLineUp,
  PhArrowClockwise,
  PhSpinner,
  PhWarning,
  PhGauge,
  PhListChecks,
  PhStack,
  PhMicrophoneStage,
  PhWaveform,
  PhChartPieSlice,
  PhPulse,
} from '@phosphor-icons/vue'
import { LoadingState } from '@/components/base'
import { useDashboardStore } from '@/stores/dashboard'
import DashboardSection from '@/components/dashboard/DashboardSection.vue'
import OverviewSection from '@/components/dashboard/OverviewSection.vue'
import MetadataCompletenessSection from '@/components/dashboard/MetadataCompletenessSection.vue'
import SeriesSection from '@/components/dashboard/SeriesSection.vue'
import ContributorsSection from '@/components/dashboard/ContributorsSection.vue'
import QualitySection from '@/components/dashboard/QualitySection.vue'
import DistributionsSection from '@/components/dashboard/DistributionsSection.vue'
import ActivitySection from '@/components/dashboard/ActivitySection.vue'
import type { ActivityGranularity } from '@/types'

const dashboardStore = useDashboardStore()
const { stats, loading, error } = storeToRefs(dashboardStore)

const generatedAt = computed(() =>
  stats.value ? new Date(stats.value.generatedAt).toLocaleString() : '',
)

function refresh() {
  dashboardStore.fetchStats()
}

function onChangeGranularity(granularity: ActivityGranularity) {
  dashboardStore.setActivityGranularity(granularity)
}

onMounted(() => {
  dashboardStore.fetchStats()
})
</script>

<template>
  <div class="dashboard-view">
    <div class="page-header-with-actions">
      <div class="dashboard-header">
        <h1>
          <PhChartLineUp />
          Dashboard
        </h1>
        <p>Library metrics, metadata health, and recent activity</p>
      </div>
      <div class="page-actions">
        <button class="btn btn-primary refresh-button" :disabled="loading" @click="refresh">
          <component :is="loading ? PhSpinner : PhArrowClockwise" :class="{ 'ph-spin': loading }" />
          {{ loading ? 'Refreshing...' : 'Refresh' }}
        </button>
      </div>
    </div>

    <div v-if="error" class="error-message">
      <PhWarning />
      {{ error }}
    </div>

    <LoadingState v-if="loading && !stats" message="Computing library metrics..." />

    <div v-else-if="stats" class="dashboard-sections">
      <DashboardSection title="Overview" :icon="PhGauge">
        <OverviewSection :overview="stats.overview" />
      </DashboardSection>

      <DashboardSection title="Metadata completeness" :icon="PhListChecks">
        <MetadataCompletenessSection :completeness="stats.metadataCompleteness" />
      </DashboardSection>

      <DashboardSection title="Series" :icon="PhStack">
        <SeriesSection :series="stats.series" />
      </DashboardSection>

      <DashboardSection title="Authors & narrators" :icon="PhMicrophoneStage">
        <ContributorsSection :authors="stats.authors" :narrators="stats.narrators" />
      </DashboardSection>

      <DashboardSection title="Quality" :icon="PhWaveform">
        <QualitySection :quality="stats.quality" />
      </DashboardSection>

      <DashboardSection title="Distributions" :icon="PhChartPieSlice">
        <DistributionsSection
          :top-genres="stats.topGenres"
          :duration-distribution="stats.durationDistribution"
          :languages="stats.languages"
        />
      </DashboardSection>

      <DashboardSection title="Activity" :icon="PhPulse">
        <ActivitySection :activity="stats.activity" @change-granularity="onChangeGranularity" />
      </DashboardSection>

      <p v-if="generatedAt" class="generated-at">Generated {{ generatedAt }}</p>
    </div>
  </div>
</template>

<style scoped>
.dashboard-view {
  padding: 1rem;
  margin: 0 auto;
}

.page-header-with-actions {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  gap: 2rem;
  margin-bottom: 2rem;
}

.dashboard-header h1 {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin: 0 0 0.5rem 0;
  color: #fff;
  font-size: 2rem;
  font-weight: 500;
}

.dashboard-header h1 :deep(svg) {
  color: var(--brand-focus);
}

.dashboard-header p {
  color: #999;
  font-size: 1rem;
  margin: 0;
}

.refresh-button {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.6rem 1.2rem;
  background: var(--brand-focus);
  color: #fff;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
  font-size: 0.95rem;
  transition: all 0.2s;
}

.refresh-button:hover:not(:disabled) {
  background: var(--brand-700);
  transform: translateY(-1px);
}

.refresh-button:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.error-message {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 1rem;
  background: rgba(231, 76, 60, 0.1);
  border: 1px solid rgba(231, 76, 60, 0.3);
  border-radius: 6px;
  color: #e74c3c;
  margin-bottom: 1.5rem;
}

.dashboard-sections {
  display: flex;
  flex-direction: column;
  gap: 1.5rem;
}

.generated-at {
  color: #666;
  font-size: 0.8rem;
  text-align: right;
  margin: 0;
}

.ph-spin {
  animation: spin 1s linear infinite;
}

@media (max-width: 768px) {
  .page-header-with-actions {
    flex-direction: column;
    align-items: stretch;
    gap: 1rem;
  }

  .refresh-button {
    width: 100%;
    justify-content: center;
  }
}
</style>
