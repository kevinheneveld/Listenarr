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
            <small>{{ recentlyImported7d }} imported this week</small>
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
          <button
            v-if="inconclusiveIds.length > 0"
            type="button"
            class="health-chip scan"
            :disabled="recheckPending"
            title="Queue a fresh verification pass (with head-probe + model escalation) for every book the agent couldn't confidently match. Manually verified/rejected books are untouched."
            @click="recheckInconclusive"
          >
            <PhSpinner v-if="recheckPending" class="ph-spin" />
            <PhArrowClockwise v-else />
            {{ recheckPending ? 'Queueing…' : `Re-check inconclusive (${inconclusiveIds.length})` }}
          </button>
          <RouterLink
            class="health-chip ok"
            :class="{ zero: verification.verified === 0 }"
            :to="{ path: '/audiobooks', query: { group: 'books', filter: 'verified' } }"
          >
            <PhShieldCheck />
            <strong>{{ verification.verified }}</strong> verified
          </RouterLink>
          <RouterLink
            class="health-chip moves"
            :class="{ zero: (moveSummary?.queued ?? 0) + (moveSummary?.processing ?? 0) === 0 }"
            to="/settings?section=maintenance"
          >
            <PhTruck />
            <strong>{{ (moveSummary?.queued ?? 0) + (moveSummary?.processing ?? 0) }}</strong>
            moves pending
          </RouterLink>
          <RouterLink
            v-if="(moveSummary?.failed ?? 0) > 0"
            class="health-chip failed"
            to="/settings?section=maintenance"
          >
            <PhXCircle />
            <strong>{{ moveSummary?.failed }}</strong> moves failed
          </RouterLink>
          <button
            v-if="(moveSummary?.failed ?? 0) > 0"
            type="button"
            class="health-chip"
            :disabled="dismissingFailedMoves"
            title="Mark all failed move jobs as dismissed — for failures a retry can't fix (source folder gone, record deleted). Retried jobs clear themselves."
            @click="dismissFailedMoves"
          >
            {{ dismissingFailedMoves ? 'Clearing…' : 'Clear failed' }}
          </button>

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
              to="/settings?section=duplicates"
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

          <button
            v-if="musicCandidates === null"
            type="button"
            class="health-chip scan"
            :disabled="scanningMusic"
            @click="scanMusic"
          >
            <PhSpinner v-if="scanningMusic" class="ph-spin" />
            <PhMusicNotes v-else />
            {{ scanningMusic ? 'Scanning…' : 'Scan for music / TTS' }}
          </button>
          <button
            v-else
            type="button"
            class="health-chip duplicates"
            :class="{ zero: musicCandidates.length === 0 }"
            @click="showMusicList = !showMusicList"
          >
            <PhMusicNotes />
            <strong>{{ musicCandidates.length }}</strong> likely music / TTS
          </button>
        </div>
        <MusicReviewList
          v-if="showMusicList && musicCandidates?.length"
          :rows="musicCandidates"
          @swept="onMusicSwept"
        />
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
            >{{ seriesBuckets.complete }} complete · {{ seriesBuckets.singleBook }} single-book ·
            {{ seriesBuckets.gaps }} with gaps</small
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
              <small
                v-if="s.catalogTotal == null"
                class="series-hint"
                title="No cached catalog for this series yet — counts reflect tracked records only. The background catalog sweep fills this in over time."
                >tracked only</small
              >
              <small
                v-if="(s.editions ?? 0) > 1"
                class="series-hint"
                title="This series has multiple recording runs (narrations/dramatizations) — open it to see per-edition coverage."
                >{{ s.editions }} editions</small
              >
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
          <!-- Background work: what the nice-priority CPU is actually doing and
               how much is left — days of invisible whisper grinding looked like
               a hang until this row existed. -->
          <div v-if="queueStatus?.verification?.processing" class="activity-current live">
            <PhWaveform />
            <span>
              Verifying:
              <RouterLink
                v-if="queueStatus.verification.processing.audiobookId"
                :to="`/audiobooks/${queueStatus.verification.processing.audiobookId}`"
                >{{ queueStatus.verification.processing.title || 'unknown book' }}</RouterLink
              >
              <template v-else>{{ queueStatus.verification.processing.title || '…' }}</template>
              · {{ queueStatus.verification.queuedBooks
              }}{{ queueStatus.verification.hasUnknownSizedJobs ? '+' : '' }} book{{
                queueStatus.verification.queuedBooks === 1 ? '' : 's'
              }}
              left
              <template v-if="verificationEta"> · {{ verificationEta }}</template>
              · {{ queueStatus.verification.completedBooksToday }} done today
            </span>
          </div>
          <div
            v-else-if="queueStatus && queueStatus.verification.queuedBooks > 0"
            class="activity-current"
          >
            <PhWaveform />
            <span>
              Verification queued: {{ queueStatus.verification.queuedBooks }} book{{
                queueStatus.verification.queuedBooks === 1 ? '' : 's'
              }}
              <template v-if="verificationEta"> · {{ verificationEta }}</template>
            </span>
          </div>
          <div
            v-if="queueStatus && queueStatus.seriesBackfill.totalMultiBook > 0"
            class="activity-current"
          >
            <PhBooks />
            <span>
              Series catalogs: {{ queueStatus.seriesBackfill.cached }}/{{
                queueStatus.seriesBackfill.totalMultiBook
              }}
              cached
            </span>
            <button
              v-if="queueStatus.seriesBackfill.cached < queueStatus.seriesBackfill.totalMultiBook"
              type="button"
              class="backfill-now-btn"
              :disabled="backfillRunning"
              title="Fetch up to 100 missing series catalogs now instead of waiting for the background cycles"
              @click="runBackfillNow"
            >
              {{ backfillRunning ? 'Backfilling…' : 'Backfill now' }}
            </button>
          </div>
          <div
            v-if="
              queueStatus &&
              !queueStatus.verification.processing &&
              queueStatus.verification.queuedBooks === 0 &&
              !moveSummary?.currentlyProcessing?.length &&
              !searchActivity.isSearching
            "
            class="activity-current"
          >
            <PhCheckCircle />
            <span>All quiet — no background work running</span>
          </div>
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

      <!-- Quality distribution -->
      <section class="dash-section" v-if="stats">
        <h2>Quality</h2>
        <div class="stats-panels">
          <div class="stats-panel">
            <h3>By codec</h3>
            <div v-for="c in stats.quality.byCodec" :key="c.codec" class="stat-row">
              <span class="stat-row-label">{{ c.codec }}</span>
              <span class="stat-row-count">{{ c.count }}</span>
            </div>
            <div v-if="stats.quality.byCodec.length === 0" class="dash-empty">
              No files tracked.
            </div>
          </div>
          <div class="stats-panel">
            <h3>By bitrate</h3>
            <div v-for="b in stats.quality.byBitrate" :key="b.label" class="stat-row">
              <span class="stat-row-label">{{ b.label }}</span>
              <span class="stat-row-count">{{ b.count }}</span>
            </div>
          </div>
        </div>
      </section>

      <!-- Metadata gaps -->
      <section class="dash-section" v-if="stats">
        <h2>Metadata gaps</h2>
        <div class="stats-panel">
          <button
            v-for="g in metadataGapRows"
            :key="g.field"
            type="button"
            class="stat-row stat-row-click"
            :disabled="g.count === 0"
            :title="
              g.count === 0
                ? 'No gaps'
                : `Show the ${g.count} book(s) missing ${g.label.toLowerCase()}`
            "
            @click="openMissing(g.field, g.label, g.count)"
          >
            <span class="stat-row-label">{{ g.label }}</span>
            <span class="stat-row-count" :class="{ zero: g.count === 0 }">{{ g.count }}</span>
          </button>
        </div>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import {
  PhSpinner,
  PhWarning,
  PhWaveform,
  PhShieldCheck,
  PhTruck,
  PhXCircle,
  PhCopySimple,
  PhMagnifyingGlass,
  PhArrowClockwise,
  PhBooks,
  PhCheckCircle,
  PhMusicNotes,
} from '@phosphor-icons/vue'
import { useLibraryStore } from '@/stores/library'
import { useSearchActivityStore } from '@/stores/searchActivity'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { useToast } from '@/services/toastService'
import MusicReviewList from '@/components/dashboard/MusicReviewList.vue'
import {
  libraryGlance,
  seriesHealth,
  bucketSeriesRows,
  type SeriesHealthRow,
  verificationCounts,
  formatBytes,
  formatEta,
} from '@/utils/dashboardAggregates'
import type {
  LibraryDuplicatesResponse,
  MoveQueueSummary,
  MusicCandidate,
  SeriesHealthApiRow,
  VerificationQueueStatus,
  DashboardStatsResponse,
} from '@/types'

const router = useRouter()
const libraryStore = useLibraryStore()
const searchActivity = useSearchActivityStore()
const toast = useToast()

const loading = ref(true)
const moveSummary = ref<MoveQueueSummary | null>(null)
const dismissingFailedMoves = ref(false)

async function dismissFailedMoves() {
  if (dismissingFailedMoves.value) return
  dismissingFailedMoves.value = true
  try {
    const result = await apiService.dismissFailedMoveJobs()
    toast.success('Failed moves cleared', `${result.dismissed} job(s) dismissed.`)
    moveSummary.value = await apiService.getMoveQueueSummary(3)
  } catch (err) {
    toast.error('Could not clear failed moves', err instanceof Error ? err.message : 'Unknown error')
  } finally {
    dismissingFailedMoves.value = false
  }
}
const duplicates = ref<LibraryDuplicatesResponse | null>(null)
const scanningDuplicates = ref(false)
const showCopyList = ref(false)
const seriesLimit = ref(15)

const glance = computed(() => libraryGlance(libraryStore.audiobooks))

// Quality + metadata-gap stats (secondary panel; failure hides it).
const stats = ref<DashboardStatsResponse | null>(null)

const recentlyImported7d = computed(() => {
  const cutoff = Date.now() - 7 * 24 * 3600 * 1000
  return libraryStore.audiobooks.filter((b) => {
    const t = b.importedAt ? Date.parse(b.importedAt) : NaN
    return Number.isFinite(t) && t >= cutoff
  }).length
})

const metadataGapRows = computed(() => {
  const c = stats.value?.completeness
  if (!c) return []
  return [
    { field: 'CoverArt' as const, label: 'Cover art', count: c.missingCoverArt },
    { field: 'Description' as const, label: 'Description', count: c.missingDescription },
    { field: 'Narrators' as const, label: 'Narrators', count: c.missingNarrators },
    { field: 'SeriesPosition' as const, label: 'Series position', count: c.missingSeriesPosition },
  ]
})

async function openMissing(
  field: 'CoverArt' | 'Description' | 'Narrators' | 'SeriesPosition',
  label: string,
  count: number,
) {
  if (count === 0) return
  try {
    const resp = await apiService.getMissingFieldIds(field)
    libraryStore.setIdSetFilter(resp.ids, `Missing ${label.toLowerCase()}`)
    void router.push('/audiobooks')
  } catch {
    toast.error('Could not load list', `Failed to fetch the books missing ${label.toLowerCase()}.`)
  }
}

// Catalog-aware series health from the server; null until loaded (or on
// failure), in which case the client-side tracked-only aggregation stands in.
const serverSeries = ref<SeriesHealthApiRow[] | null>(null)
const seriesRows = computed<SeriesHealthRow[]>(() => {
  if (serverSeries.value) {
    return serverSeries.value.map((r) => {
      const total = r.catalogTotal ?? r.owned + r.missingTracked
      return {
        name: r.name,
        owned: r.owned,
        missing: Math.max(0, total - r.owned),
        total,
        complete: r.complete,
        catalogTotal: r.catalogTotal,
        editions: r.editions ?? undefined,
      }
    })
  }
  return seriesHealth(libraryStore.audiobooks)
})
const seriesBuckets = computed(() => bucketSeriesRows(seriesRows.value))
const incompleteSeries = computed(() => seriesRows.value.filter((s) => !s.complete))
const visibleIncompleteSeries = computed(() => incompleteSeries.value.slice(0, seriesLimit.value))
const verification = computed(() => verificationCounts(libraryStore.audiobooks))

// Books worth a fresh verification pass: agent-flagged, agent-unverifiable,
// and never-verified books that actually have audio. Manual verdicts are
// sticky server-side, so they're excluded here AND skipped by the backend.
const inconclusiveIds = computed(() =>
  libraryStore.audiobooks
    .filter(
      (b) =>
        b.verificationStatus === 'agentFlagged' ||
        b.verificationStatus === 'agentUnverifiable' ||
        ((b.verificationStatus === 'unverified' || !b.verificationStatus) &&
          (b.fileCount ?? 0) > 0),
    )
    .map((b) => b.id),
)
const recheckPending = ref(false)

async function recheckInconclusive() {
  const ids = inconclusiveIds.value
  if (ids.length === 0 || recheckPending.value) return
  recheckPending.value = true
  try {
    // Single POST: the batch endpoint takes the full id list (a few hundred
    // ints is trivial payload); the queue processes serially at idle priority.
    await apiService.startLibraryVerification(ids)
    toast.info(
      'Re-check queued',
      `${ids.length} book${ids.length === 1 ? '' : 's'} queued — the activity strip and badges update as verdicts land.`,
    )
  } catch (err) {
    toast.error('Could not queue re-check', err instanceof Error ? err.message : 'Unknown error')
  } finally {
    recheckPending.value = false
  }
}

const musicCandidates = ref<MusicCandidate[] | null>(null)
const scanningMusic = ref(false)
const showMusicList = ref(false)

async function scanMusic() {
  scanningMusic.value = true
  try {
    musicCandidates.value = (await apiService.getMusicCandidates()).candidates
    showMusicList.value = true
  } catch (err) {
    toast.error(
      'Music scan failed',
      err instanceof Error ? err.message : 'Could not scan the library.',
    )
  } finally {
    scanningMusic.value = false
  }
}

function onMusicSwept(ids: number[]) {
  if (!musicCandidates.value) return
  const swept = new Set(ids)
  musicCandidates.value = musicCandidates.value.filter((c) => !swept.has(c.id))
  // Swept books changed verification/library state — refresh the glance data.
  void libraryStore.fetchLibrary()
}

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

// --- Background activity (verification queue + series backfill) -------------
const queueStatus = ref<VerificationQueueStatus | null>(null)
const backfillRunning = ref(false)
let queuePollTimer: ReturnType<typeof setInterval> | null = null
let unsubscribeVerification: (() => void) | null = null

const verificationEta = computed(() => formatEta(queueStatus.value?.verification?.etaSeconds))

async function refreshQueueStatus() {
  try {
    queueStatus.value = await apiService.getVerificationQueueStatus()
  } catch {
    /* endpoint unavailable — the panel simply hides */
  }
}

async function runBackfillNow() {
  backfillRunning.value = true
  try {
    const result = await apiService.runSeriesCatalogBackfill()
    toast.success('Series backfill', result.message)
    void refreshQueueStatus()
    // Newly cached catalogs change series-health rows too.
    void apiService
      .getSeriesHealth()
      .then((resp) => (serverSeries.value = resp.rows))
      .catch(() => {})
  } catch (err) {
    toast.error(
      'Backfill failed',
      err instanceof Error ? err.message : 'Could not run the series backfill.',
    )
  } finally {
    backfillRunning.value = false
  }
}

onUnmounted(() => {
  if (queuePollTimer) clearInterval(queuePollTimer)
  unsubscribeVerification?.()
})

onMounted(async () => {
  void apiService
    .getSeriesHealth()
    .then((resp) => {
      serverSeries.value = resp.rows
    })
    .catch(() => {
      /* endpoint unavailable — client-side tracked-only aggregation stands in */
    })
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
  void apiService
    .getDashboardStats()
    .then((r) => (stats.value = r))
    .catch(() => {})
  void refreshQueueStatus()
  queuePollTimer = setInterval(() => void refreshQueueStatus(), 30_000)
  unsubscribeVerification = signalRService.onVerificationComplete(() => void refreshQueueStatus())
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

.series-hint {
  margin-left: 0.4rem;
  padding: 0.05rem 0.35rem;
  border: 1px solid rgba(173, 181, 189, 0.3);
  border-radius: 999px;
  color: #868e96;
  font-size: 0.68rem;
  white-space: nowrap;
  cursor: help;
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
.backfill-now-btn {
  margin-left: auto;
  padding: 0.25rem 0.7rem;
  background-color: rgba(var(--brand-rgb), 0.1);
  border: 1px solid var(--brand-500);
  border-radius: 5px;
  color: var(--brand-500);
  font-size: 0.8rem;
  cursor: pointer;
  flex-shrink: 0;
}

.backfill-now-btn:disabled {
  opacity: 0.5;
  cursor: default;
}

.stats-panels {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 0.75rem;
}

.stats-panel {
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.07);
  border-radius: 8px;
  padding: 0.75rem 1rem;
}

.stats-panel h3 {
  margin: 0 0 0.5rem;
  font-size: 0.85rem;
  color: #8a93a0;
  font-weight: 500;
}

.stat-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  width: 100%;
  padding: 0.3rem 0;
  color: #d8dee6;
  font-size: 0.9rem;
  background: none;
  border: none;
  text-align: left;
}

.stat-row-click {
  cursor: pointer;
  border-radius: 4px;
}

.stat-row-click:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.04);
}

.stat-row-click:disabled {
  cursor: default;
}

.stat-row-count {
  font-weight: 600;
  color: #f0c674;
}

.stat-row-count.zero {
  color: #51cf66;
}
</style>
