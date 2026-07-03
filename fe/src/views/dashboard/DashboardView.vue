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
  Library Health dashboard: what you own, which series are complete, and the
  health buckets that need a human — every count clicks through to the books
  behind it.
-->
<template>
  <div class="dashboard-view">
    <div v-if="loading" class="dash-loading"><PhSpinner class="ph-spin" /> Loading library…</div>

    <template v-else>
      <!-- Library at a glance -->
      <section class="dash-section">
        <h2>Library</h2>
        <div class="stat-row">
          <RouterLink
            class="stat-card owned"
            :to="{ path: '/audiobooks', query: { group: 'books' } }"
          >
            <span class="stat-value">{{ glance.owned }}</span>
            <span class="stat-label">owned</span>
            <small>{{ formatBytes(glance.totalBytes) }} on disk</small>
          </RouterLink>
          <RouterLink class="stat-card missing" to="/wanted">
            <span class="stat-value">{{ glance.missing }}</span>
            <span class="stat-label">missing</span>
            <small>monitored, no audio yet</small>
          </RouterLink>
          <RouterLink
            class="stat-card idle"
            :to="{ path: '/audiobooks', query: { group: 'books', filter: 'unmonitored' } }"
          >
            <span class="stat-value">{{ glance.idle }}</span>
            <span class="stat-label">idle</span>
            <small>no files, not monitored</small>
          </RouterLink>
          <div class="stat-card total">
            <span class="stat-value">{{ glance.total }}</span>
            <span class="stat-label">total records</span>
            <small>{{ seriesRows.length }} series tracked</small>
          </div>
        </div>
      </section>

      <!-- Health categories -->
      <section class="dash-section">
        <h2>Health</h2>
        <div class="health-row">
          <RouterLink
            class="health-chip flagged"
            :class="{ zero: verification.flagged === 0 }"
            :to="{ path: '/audiobooks', query: { group: 'books', filter: 'needs-review' } }"
          >
            <PhWarning />
            <strong>{{ verification.flagged }}</strong> needs review
          </RouterLink>
          <RouterLink
            class="health-chip neutral"
            :class="{ zero: verification.unverifiable === 0 }"
            :to="{ path: '/audiobooks', query: { group: 'books', filter: 'no-spoken-credits' } }"
          >
            <PhWaveform />
            <strong>{{ verification.unverifiable }}</strong> no spoken credits
          </RouterLink>
          <RouterLink
            class="health-chip ok"
            :class="{ zero: verification.verified === 0 }"
            :to="{ path: '/audiobooks', query: { group: 'books' } }"
          >
            <PhShieldCheck />
            <strong>{{ verification.verified }}</strong> verified
          </RouterLink>
          <RouterLink
            class="health-chip moves"
            :class="{ zero: (moveSummary?.queued ?? 0) + (moveSummary?.processing ?? 0) === 0 }"
            to="/settings"
          >
            <PhTruck />
            <strong>{{ (moveSummary?.queued ?? 0) + (moveSummary?.processing ?? 0) }}</strong>
            moves pending
          </RouterLink>
          <RouterLink
            v-if="(moveSummary?.failed ?? 0) > 0"
            class="health-chip failed"
            to="/settings"
          >
            <PhXCircle />
            <strong>{{ moveSummary?.failed }}</strong> moves failed
          </RouterLink>

          <button
            v-if="duplicates === null"
            type="button"
            class="health-chip scan"
            :disabled="scanningDuplicates"
            @click="scanDuplicates"
          >
            <PhSpinner v-if="scanningDuplicates" class="ph-spin" />
            <PhCopySimple v-else />
            {{ scanningDuplicates ? 'Scanning…' : 'Scan for duplicates' }}
          </button>
          <template v-else>
            <RouterLink
              class="health-chip duplicates"
              :class="{ zero: duplicates.duplicateGroups.length === 0 }"
              to="/settings"
            >
              <PhCopySimple />
              <strong>{{ duplicates.duplicateGroups.length }}</strong> duplicate records
            </RouterLink>
            <button
              type="button"
              class="health-chip duplicates"
              :class="{ zero: duplicates.duplicateCopyBooks.length === 0 }"
              @click="showCopyList = !showCopyList"
            >
              <PhCopySimple />
              <strong>{{ duplicates.duplicateCopyBooks.length }}</strong> duplicate copies
            </button>
          </template>
        </div>
        <div v-if="showCopyList && duplicates?.duplicateCopyBooks.length" class="copy-list">
          <RouterLink
            v-for="c in duplicates.duplicateCopyBooks"
            :key="c.id"
            :to="`/audiobooks/${c.id}`"
            class="copy-list-item"
          >
            {{ c.title || `Book ${c.id}` }}
            <small>{{ c.fileCount }} files · {{ c.clusterCount }} groups</small>
          </RouterLink>
        </div>
      </section>

      <!-- Series health -->
      <section class="dash-section">
        <h2>
          Series
          <small class="h2-sub"
            >{{ completeSeriesCount }} complete · {{ incompleteSeries.length }} with gaps</small
          >
        </h2>
        <div v-if="incompleteSeries.length === 0" class="dash-empty">
          Every tracked series is complete. 🎉
        </div>
        <div v-else class="series-list">
          <RouterLink
            v-for="s in visibleIncompleteSeries"
            :key="s.name"
            :to="`/collection/series/${encodeURIComponent(s.name)}`"
            class="series-row"
          >
            <span class="series-name">{{ s.name }}</span>
            <span class="series-bar">
              <span
                class="series-bar-fill"
                :style="{ width: `${Math.round((s.owned / s.total) * 100)}%` }"
              ></span>
            </span>
            <span class="series-counts">
              <strong>{{ s.owned }}</strong
              >/{{ s.total }} <small>({{ s.missing }} missing)</small>
            </span>
          </RouterLink>
          <button
            v-if="incompleteSeries.length > seriesLimit"
            type="button"
            class="show-more"
            @click="seriesLimit += 15"
          >
            Show more ({{ incompleteSeries.length - seriesLimit }} remaining)
          </button>
        </div>
      </section>

      <!-- Activity -->
      <section class="dash-section">
        <h2>Activity</h2>
        <div class="activity-strip">
          <div class="activity-current" :class="{ live: searchActivity.isSearching }">
            <PhMagnifyingGlass />
            <span>{{ searchActivity.current?.message || 'Search idle' }}</span>
          </div>
          <div v-if="moveSummary?.currentlyProcessing?.length" class="activity-current live">
            <PhTruck />
            <span>Moving: {{ moveSummary.currentlyProcessing[0].audiobookTitle }}</span>
          </div>
        </div>
        <div v-if="searchActivity.recent.length" class="activity-feed">
          <div v-for="(e, i) in searchActivity.recent.slice(0, 8)" :key="i" class="activity-line">
            <span class="activity-stage" :class="`stage-${e.stage}`">{{ e.stage }}</span>
            <RouterLink v-if="e.audiobookId" :to="`/audiobooks/${e.audiobookId}`">{{
              e.message
            }}</RouterLink>
            <span v-else>{{ e.message }}</span>
          </div>
        </div>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { RouterLink } from 'vue-router'
import {
  PhSpinner,
  PhWarning,
  PhWaveform,
  PhShieldCheck,
  PhTruck,
  PhXCircle,
  PhCopySimple,
  PhMagnifyingGlass,
} from '@phosphor-icons/vue'
import { useLibraryStore } from '@/stores/library'
import { useSearchActivityStore } from '@/stores/searchActivity'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import {
  libraryGlance,
  seriesHealth,
  verificationCounts,
  formatBytes,
} from '@/utils/dashboardAggregates'
import type { LibraryDuplicatesResponse, MoveQueueSummary } from '@/types'

const libraryStore = useLibraryStore()
const searchActivity = useSearchActivityStore()
const toast = useToast()

const loading = ref(true)
const moveSummary = ref<MoveQueueSummary | null>(null)
const duplicates = ref<LibraryDuplicatesResponse | null>(null)
const scanningDuplicates = ref(false)
const showCopyList = ref(false)
const seriesLimit = ref(15)

const glance = computed(() => libraryGlance(libraryStore.audiobooks))
const seriesRows = computed(() => seriesHealth(libraryStore.audiobooks))
const completeSeriesCount = computed(() => seriesRows.value.filter((s) => s.complete).length)
const incompleteSeries = computed(() => seriesRows.value.filter((s) => !s.complete))
const visibleIncompleteSeries = computed(() => incompleteSeries.value.slice(0, seriesLimit.value))
const verification = computed(() => verificationCounts(libraryStore.audiobooks))

async function scanDuplicates() {
  scanningDuplicates.value = true
  try {
    duplicates.value = await apiService.getLibraryDuplicates()
  } catch (err) {
    toast.error(
      'Duplicate scan failed',
      err instanceof Error ? err.message : 'Could not scan the library.',
    )
  } finally {
    scanningDuplicates.value = false
  }
}

onMounted(async () => {
  try {
    if (libraryStore.audiobooks.length === 0) {
      await libraryStore.fetchLibrary()
    }
  } finally {
    loading.value = false
  }
  // Secondary panels load after first paint; failures degrade to hidden panels.
  searchActivity.start()
  void apiService
    .getMoveQueueSummary(3)
    .then((s) => (moveSummary.value = s))
    .catch(() => {})
})
</script>

<style scoped>
.dashboard-view {
  padding: 1.25rem 1.5rem 3rem;
  max-width: 1100px;
}

.dash-loading {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #adb5bd;
  padding: 2rem 0;
}

.dash-section {
  margin-bottom: 1.75rem;
}

.dash-section h2 {
  color: #fff;
  font-size: 1.05rem;
  font-weight: 600;
  margin: 0 0 0.75rem 0;
  display: flex;
  align-items: baseline;
  gap: 0.6rem;
}

.h2-sub {
  color: #868e96;
  font-size: 0.8rem;
  font-weight: 400;
}

/* Glance cards */
.stat-row {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 0.75rem;
}

.stat-card {
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
  padding: 0.9rem 1rem;
  border-radius: 8px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.02);
  text-decoration: none;
  color: #fff;
  transition: border-color 0.15s;
}

.stat-card:hover {
  border-color: var(--brand-500);
}

.stat-value {
  font-size: 1.7rem;
  font-weight: 700;
  line-height: 1.1;
}

.stat-label {
  font-size: 0.85rem;
  color: #adb5bd;
}

.stat-card small {
  color: #868e96;
  font-size: 0.75rem;
}

.stat-card.owned .stat-value {
  color: #2ecc71;
}

.stat-card.missing .stat-value {
  color: #f39c12;
}

.stat-card.idle .stat-value {
  color: #868e96;
}

/* Health chips */
.health-row {
  display: flex;
  flex-wrap: wrap;
  gap: 0.6rem;
}

.health-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.5rem 0.85rem;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.03);
  color: #d8dee6;
  font-size: 0.9rem;
  text-decoration: none;
  cursor: pointer;
}

.health-chip strong {
  font-size: 1rem;
}

.health-chip.zero {
  opacity: 0.55;
}

.health-chip.flagged {
  border-color: rgba(243, 156, 18, 0.4);
  color: #f39c12;
}

.health-chip.failed {
  border-color: rgba(231, 76, 60, 0.4);
  color: #e74c3c;
}

.health-chip.ok {
  border-color: rgba(46, 204, 113, 0.35);
  color: #2ecc71;
}

.health-chip.neutral,
.health-chip.duplicates,
.health-chip.moves,
.health-chip.scan {
  color: #adb5bd;
}

.health-chip.scan:disabled {
  opacity: 0.6;
  cursor: wait;
}

.copy-list {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  margin-top: 0.7rem;
}

.copy-list-item {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.4rem 0.7rem;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  color: #d8dee6;
  text-decoration: none;
  font-size: 0.9rem;
}

.copy-list-item:hover {
  border-color: var(--brand-500);
}

.copy-list-item small {
  color: #868e96;
}

/* Series */
.series-list {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.series-row {
  display: grid;
  grid-template-columns: minmax(140px, 1fr) 2fr auto;
  align-items: center;
  gap: 0.9rem;
  padding: 0.45rem 0.7rem;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.05);
  color: #d8dee6;
  text-decoration: none;
  font-size: 0.9rem;
}

.series-row:hover {
  border-color: var(--brand-500);
}

.series-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.series-bar {
  height: 6px;
  border-radius: 3px;
  background: rgba(255, 255, 255, 0.07);
  overflow: hidden;
}

.series-bar-fill {
  display: block;
  height: 100%;
  background: linear-gradient(90deg, #2ecc71, #27ae60);
  border-radius: 3px;
}

.series-counts {
  color: #adb5bd;
  white-space: nowrap;
}

.series-counts small {
  color: #f39c12;
}

.show-more {
  align-self: flex-start;
  margin-top: 0.4rem;
  background: none;
  border: 1px solid rgba(255, 255, 255, 0.12);
  color: #adb5bd;
  border-radius: 6px;
  padding: 0.35rem 0.8rem;
  cursor: pointer;
  font-size: 0.85rem;
}

.dash-empty {
  color: #868e96;
  font-size: 0.9rem;
}

/* Activity */
.activity-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 0.6rem;
  margin-bottom: 0.6rem;
}

.activity-current {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.45rem 0.8rem;
  border-radius: 6px;
  border: 1px solid rgba(255, 255, 255, 0.08);
  color: #868e96;
  font-size: 0.88rem;
}

.activity-current.live {
  color: #4dabf7;
  border-color: rgba(77, 171, 247, 0.35);
}

.activity-feed {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.activity-line {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  font-size: 0.85rem;
  color: #adb5bd;
}

.activity-line a {
  color: #d8dee6;
  text-decoration: none;
}

.activity-line a:hover {
  color: var(--brand-500);
}

.activity-stage {
  min-width: 84px;
  text-align: center;
  padding: 0.1rem 0.4rem;
  border-radius: 4px;
  font-size: 0.72rem;
  background: rgba(255, 255, 255, 0.05);
}

.activity-stage.stage-grabbed {
  color: #2ecc71;
}

.activity-stage.stage-no_results {
  color: #868e96;
}

.activity-stage.stage-searching {
  color: #4dabf7;
}
</style>
