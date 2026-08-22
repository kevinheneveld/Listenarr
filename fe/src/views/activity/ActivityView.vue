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
<template>
  <div class="activity-view">
    <div class="page-header">
      <h1>
        <PhActivity />
        Activity
      </h1>
      <div class="activity-actions">
        <div class="filter-input-wrapper">
          <PhMagnifyingGlass class="filter-icon" />
          <input
            v-model="filterText"
            type="text"
            class="filter-input"
            placeholder="Filter activity..."
          />
          <button v-if="filterText" class="filter-clear" @click="filterText = ''">
            <PhX />
          </button>
        </div>
        <button class="btn btn-secondary" @click="refreshQueue" :disabled="loading">
          <component :is="loading ? PhSpinner : PhArrowClockwise" />
          Refresh
        </button>
      </div>
    </div>

    <!-- Summary chips: counts per category; click to filter the list -->
    <div class="activity-summary">
      <button
        v-for="chip in summaryChips"
        :key="chip.key"
        type="button"
        :class="['summary-chip', `chip-${chip.key}`, { active: activeCategory === chip.category }]"
        :title="
          chip.windowed
            ? `${chip.label} in the last ${windowHours}h — click to show only these`
            : `${chip.label} — click to show only these`
        "
        @click="selectCategory(chip.category)"
      >
        <span class="chip-count">{{ chip.count }}</span>
        <span class="chip-label"
          >{{ chip.label
          }}<span v-if="chip.windowed" class="chip-window"> · {{ windowHours }}h</span></span
        >
      </button>
    </div>

    <!-- Failure-reason breakdown, shown when drilled into Failed or Stalled -->
    <div
      v-if="
        (activeCategory === 'Failed' || activeCategory === 'Stalled') && failureReasons.length > 0
      "
      class="failure-reasons"
    >
      <span class="failure-reasons-label">Reasons:</span>
      <span v-for="r in failureReasons" :key="r.reason" class="failure-reason-pill">
        {{ r.reason }} <strong>{{ r.count }}</strong>
      </span>
    </div>

    <div v-if="queueHealthClients.length > 0" class="queue-health-banner">
      <PhWarningCircle />
      <div class="queue-health-copy">
        <strong>{{ queueHealthTitle }}</strong>
        <span>{{ queueHealthMessage }}</span>
      </div>
    </div>

    <!-- Queue Grid -->
    <div
      v-if="filteredQueue.length > 0"
      ref="scrollContainer"
      :class="['queue-grid-container', { 'is-static': !useVirtualActivityList }]"
      @scroll="updateVisibleRange"
    >
      <div class="queue-header">
        <div class="col-title">Title</div>
        <div class="col-quality">Quality</div>
        <div class="col-when">When</div>
        <div class="col-progress">Progress</div>
        <div class="col-eta">ETA</div>
        <div class="col-status">Status</div>
        <div class="col-actions"></div>
      </div>
      <div
        :class="['queue-body-spacer', { 'is-static': !useVirtualActivityList }]"
        :style="useVirtualActivityList ? { height: `${totalHeight}px` } : undefined"
      >
        <div
          :class="['queue-body', { 'is-static': !useVirtualActivityList }]"
          :style="useVirtualActivityList ? { transform: `translateY(${topPadding}px)` } : undefined"
        >
          <div
            v-for="item in visibleQueueItems"
            :key="item.id"
            v-memo="[
              item.id,
              item.status,
              item.progress,
              item.eta,
              item.downloadSpeed,
              item.downloadClient,
            ]"
            class="queue-row"
          >
            <div class="col-title">
              <div class="title-cell">
                <div class="title-main">
                  <div class="title-row">
                    <RouterLink
                      v-if="item.audiobookId"
                      :to="`/audiobooks/${item.audiobookId}`"
                      class="title-link"
                      >{{ getDisplayTitle(item) }}</RouterLink
                    >
                    <span v-else class="title-text">{{ getDisplayTitle(item) }}</span>
                    <span
                      v-if="item.downloadClient"
                      class="client-chip"
                      :title="clientTooltip(item)"
                    >
                      <component :is="clientIcon(item)" class="client-icon" />
                      {{ item.downloadClient }}
                    </span>
                  </div>
                  <span v-if="item.reason" class="title-reason" :title="item.reason">{{
                    item.reason
                  }}</span>
                </div>
              </div>
            </div>
            <div class="col-quality">
              <span v-if="item.quality && item.quality !== '*'" class="quality-tag">{{
                item.quality
              }}</span>
              <span v-else class="muted">-</span>
            </div>
            <div class="col-when">
              <span class="when-text" :title="formatWhenTitle(item)">{{
                formatWhen(item.whenIso)
              }}</span>
              <button
                v-if="item.attemptCount > 1"
                type="button"
                class="attempts-badge"
                :title="`${item.attemptCount} attempts — click to see what happened`"
                @click="openAttempts(item)"
              >
                ×{{ item.attemptCount }}
              </button>
            </div>
            <div class="col-progress">
              <div class="progress-cell">
                <ProgressBar
                  :value="item.progress"
                  :downloaded="item.downloaded"
                  :total="item.size"
                  :showPercentage="item.downloadClientType === 'move'"
                  variant="activity"
                  height="small"
                  :animating="item.status === 'downloading' || item.status === 'moving'"
                />
              </div>
            </div>

            <div class="col-eta">
              <span v-if="item.eta" class="eta-text">{{ formatEta(item.eta) }}</span>
              <span v-else class="muted">-</span>
            </div>
            <div class="col-status">
              <span :class="['status-badge', item.status]" :title="statusTooltip(item)">
                {{ formatStatus(item.status) }}
              </span>
            </div>
            <div class="col-actions">
              <button
                v-if="item.canRemove"
                class="btn-icon btn-danger-icon"
                @click="removeFromQueue(item)"
                title="Remove from Queue"
              >
                <PhX />
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Empty State -->
    <EmptyState
      v-else-if="filteredQueue.length === 0 && !loading"
      :title="filterText ? 'No Matching Downloads' : 'No Active Downloads'"
      :message="
        filterText
          ? 'No downloads match your filter.'
          : 'Downloads will appear here when you send items to your download clients.'
      "
    >
      <template #icon>
        <PhQueue :size="48" />
      </template>
    </EmptyState>

    <!-- Loading State -->
    <LoadingState v-if="loading && queue.length === 0" message="Loading queue..." />

    <!-- Remove Confirmation Modal -->
    <div v-if="showRemoveModal" class="modal-overlay" @click="showRemoveModal = false">
      <div class="modal-content" @click.stop>
        <div class="modal-header">
          <h3>
            <PhWarningCircle />
            Remove from Queue
          </h3>
          <button class="modal-close" @click="showRemoveModal = false">
            <PhX />
          </button>
        </div>
        <div class="modal-body">
          <p v-if="clientHasQueueEntry === null">Are you sure you want to remove this download?</p>
          <p v-else-if="clientHasQueueEntry === true">
            Are you sure you want to remove this download?
          </p>
          <p v-else>
            This download was not found in the selected download client's queue. Do you want to
            remove it from Listenarr only (this will delete the record from Listenarr's downloads)?
          </p>
          <div class="remove-item-info">
            <strong>{{ itemToRemove ? getDisplayTitle(itemToRemove) : '' }}</strong>
            <div class="item-details">
              <span v-if="itemToRemove?.downloadClient">
                <PhDesktop />
                {{ itemToRemove.downloadClient }}
              </span>
              <span>
                <PhChartBar />
                {{ itemToRemove?.progress.toFixed(1) }}% complete
              </span>
            </div>
          </div>
          <p class="warning-text" v-if="clientHasQueueEntry !== false">
            <PhInfo />
            This will remove the download from your download client. Files may or may not be deleted
            depending on your client settings.
          </p>
          <p class="warning-text" v-else>
            <PhInfo />
            The download was not found in the remote client's queue. Removing it here will only
            delete the Listenarr record (no action will be taken against the remote client).
          </p>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" @click="showRemoveModal = false">Cancel</button>
          <button class="btn btn-danger" @click="confirmRemove" :disabled="removing">
            <component :is="removing ? PhSpinner : PhTrash" />
            {{
              removing
                ? 'Removing...'
                : clientHasQueueEntry === false
                  ? 'Remove from Listenarr'
                  : 'Remove'
            }}
          </button>
        </div>
      </div>
    </div>

    <!-- Attempt-history modal: what each of the ×N grab attempts was and why it failed -->
    <div v-if="showAttemptsModal" class="modal-overlay" @click="closeAttempts">
      <div class="modal-content attempts-modal" @click.stop>
        <div class="modal-header">
          <h3>
            <PhClockCounterClockwise />
            Attempt History
          </h3>
          <button class="modal-close" @click="closeAttempts">
            <PhX />
          </button>
        </div>
        <div class="modal-body">
          <p class="attempts-subtitle">
            {{ attemptsItem ? getDisplayTitle(attemptsItem) : '' }} —
            <strong>{{ attemptsItem?.attempts.length ?? 0 }}</strong> attempts, newest first
          </p>
          <ol class="attempts-list">
            <li
              v-for="(attempt, idx) in attemptsItem?.attempts ?? []"
              :key="idx"
              class="attempt-row"
            >
              <div class="attempt-head">
                <span :class="['status-badge', attemptBadgeClass(attempt)]">{{
                  formatStatus(attempt.status.toLowerCase())
                }}</span>
                <span class="attempt-when" :title="new Date(attempt.at).toLocaleString()">{{
                  formatWhen(attempt.at)
                }}</span>
                <span v-if="attempt.downloadClientName" class="attempt-client">{{
                  attempt.downloadClientName
                }}</span>
              </div>
              <p v-if="attempt.reason" class="attempt-reason">{{ attempt.reason }}</p>
              <p v-else class="attempt-reason muted">No error recorded for this attempt.</p>
            </li>
          </ol>
        </div>
        <div class="modal-footer">
          <button class="btn btn-secondary" @click="closeAttempts">Close</button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, nextTick } from 'vue'
import {
  PhActivity,
  PhSpinner,
  PhArrowClockwise,
  PhDesktop,
  PhX,
  PhQueue,
  PhWarningCircle,
  PhInfo,
  PhChartBar,
  PhTrash,
  PhMagnifyingGlass,
  PhClockCounterClockwise,
  PhMagnet,
  PhCloudArrowDown,
  PhGlobe,
  PhHardDrives,
} from '@phosphor-icons/vue'
import type { Component } from 'vue'
import { useToast } from '@/services/toastService'
import { errorTracking } from '@/services/errorTracking'
import { apiService } from '@/services/api'
import { signalRService } from '@/services/signalr'
import { useLibraryStore } from '@/stores/library'
import { useMoveJobsStore } from '@/stores/moveJobs'
import { EmptyState, LoadingState, ProgressBar } from '@/components/base'
import type {
  QueueClientStatus,
  QueueItem,
  QueueUpdatePayload,
  ActivityResponse,
  ActivityItem,
  ActivityAttempt,
  ActivityCategory,
} from '@/types'
import { normalizeQueueSnapshot } from '@/utils/queueSnapshot'

const libraryStore = useLibraryStore()
const moveJobsStore = useMoveJobsStore()

const filterText = ref('')
const queue = ref<QueueItem[]>([])
const queueClientStatuses = ref<QueueClientStatus[]>([])
const activity = ref<ActivityResponse | null>(null)
// null = default view (in-progress + blocked); otherwise drill into a single category.
const activeCategory = ref<ActivityCategory | null>(null)
const loading = ref(false)
const showRemoveModal = ref(false)
const clientHasQueueEntry = ref<boolean | null>(null)
const itemToRemove = ref<ActivityRow | null>(null)
const removing = ref(false)
const showAttemptsModal = ref(false)
const attemptsItem = ref<ActivityRow | null>(null)
let unsubscribeQueue: (() => void) | null = null
let queueRefreshInterval: ReturnType<typeof setInterval> | null = null

const applyQueueSnapshot = (payload: QueueUpdatePayload | null | undefined) => {
  const snapshot = normalizeQueueSnapshot(payload)
  queue.value = snapshot.items
  queueClientStatuses.value = snapshot.clients
}

const formatSnapshotAge = (seconds?: number): string => {
  if (!seconds || seconds <= 0) return 'just now'
  if (seconds < 60) return `${seconds}s old`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m old`
  return `${Math.floor(seconds / 3600)}h old`
}

const formatSnapshotReason = (reason?: string): string => {
  if (!reason) return 'client issue'
  if (reason === 'timeout') return 'a timeout'
  if (reason === 'canceled') return 'a canceled request'
  return 'a client error'
}

const queueHealthClients = computed(() => {
  if (queueClientStatuses.value.length > 0) {
    return queueClientStatuses.value
      .filter((client) => client.isStaleSnapshot || client.isUnavailable)
      .map((client) => ({
        id: client.clientId || client.clientName,
        name: client.clientName || 'Download client',
        ageSeconds: client.snapshotAgeSeconds,
        failureReason: client.snapshotFailureReason,
        snapshotState: client.snapshotState,
        isUnavailable: client.isUnavailable,
      }))
  }

  const map = new Map<
    string,
    {
      id: string
      name: string
      ageSeconds?: number
      failureReason?: string
      snapshotState?: string
      isUnavailable?: boolean
    }
  >()

  queue.value.forEach((item) => {
    if (!item.isStaleSnapshot) return

    const key = item.downloadClientId || item.downloadClient || item.id
    if (!map.has(key)) {
      map.set(key, {
        id: key,
        name: item.downloadClient || 'Download client',
        ageSeconds: item.snapshotAgeSeconds,
        failureReason: item.snapshotFailureReason,
        snapshotState: item.snapshotState,
        isUnavailable: false,
      })
    }
  })

  return Array.from(map.values())
})

const queueHealthTitle = computed(() => {
  return queueHealthClients.value.some((client) => client.isUnavailable)
    ? 'Some queue data is unavailable'
    : 'Using cached queue data'
})

const queueHealthMessage = computed(() => {
  return queueHealthClients.value
    .map((client) => {
      const parts = [client.name]
      if (client.isUnavailable) {
        parts.push('unavailable')
      } else if (client.snapshotState === 'cached') {
        parts.push('using cached data')
      }
      if (client.ageSeconds != null) parts.push(formatSnapshotAge(client.ageSeconds))
      if (client.failureReason) parts.push(`after ${formatSnapshotReason(client.failureReason)}`)
      return parts.join(' ')
    })
    .join(', ')
})

// Virtual scrolling setup
const scrollContainer = ref<HTMLElement | null>(null)
const ROW_HEIGHT = 37
const BUFFER_ROWS = 5
const MOBILE_ACTIVITY_BREAKPOINT = 768

const visibleRange = ref({ start: 0, end: 30 })

const isMobileActivityLayout = ref(false)
const useVirtualActivityList = computed(() => !isMobileActivityLayout.value)

const updateActivityLayoutMode = () => {
  if (typeof window === 'undefined') return

  if (typeof window.matchMedia === 'function') {
    isMobileActivityLayout.value = window.matchMedia(
      `(max-width: ${MOBILE_ACTIVITY_BREAKPOINT}px)`,
    ).matches
    return
  }

  isMobileActivityLayout.value = window.innerWidth <= MOBILE_ACTIVITY_BREAKPOINT
}

const visibleQueueItems = computed(() => {
  if (!useVirtualActivityList.value) {
    return filteredQueue.value
  }

  return filteredQueue.value.slice(visibleRange.value.start, visibleRange.value.end)
})

const updateVisibleRange = () => {
  if (!useVirtualActivityList.value) {
    visibleRange.value = { start: 0, end: filteredQueue.value.length }
    return
  }

  if (!scrollContainer.value) return

  const scrollTop = scrollContainer.value.scrollTop
  const viewportHeight = scrollContainer.value.clientHeight

  const firstVisibleIndex = Math.floor(scrollTop / ROW_HEIGHT)
  const visibleItemCount = Math.ceil(viewportHeight / ROW_HEIGHT)

  const startIndex = Math.max(0, firstVisibleIndex - BUFFER_ROWS)
  const endIndex = Math.min(
    firstVisibleIndex + visibleItemCount + BUFFER_ROWS,
    filteredQueue.value.length,
  )

  visibleRange.value = { start: startIndex, end: endIndex }
}

const totalHeight = computed(() => {
  return filteredQueue.value.length * ROW_HEIGHT
})

const topPadding = computed(() => {
  return visibleRange.value.start * ROW_HEIGHT
})

const syncActivityLayout = async () => {
  await nextTick()
  updateVisibleRange()
}

const handleViewportResize = () => {
  updateActivityLayoutMode()
  void syncActivityLayout()
}

// A rendered Activity row: a collapsed-per-book item from the activity endpoint, with live
// download progress overlaid from the SignalR queue snapshot. Crucially, the row's *status*
// always comes from the authoritative DB record (the activity endpoint) — never from the live
// queue — so a finished-but-blocked torrent that is still seeding no longer flips the row
// between "downloading" and "import blocked".
interface ActivityRow {
  id: string
  audiobookId?: number
  title: string
  quality: string
  language?: string
  status: string // badge class: downloading/queued/importblocked/imported/failed/stalled/...
  category: ActivityCategory
  progress: number
  size: number
  downloaded: number
  downloadSpeed: number
  eta?: number
  reason?: string
  attemptCount: number
  whenIso: string
  completedIso?: string
  downloadClient: string
  downloadClientId: string
  downloadClientType: string
  attempts: ActivityAttempt[]
  canRemove: boolean
  isStaleSnapshot?: boolean
}

// Maps a download's authoritative category + status to the existing status-badge CSS classes.
const badgeClassFor = (item: ActivityItem): string => {
  switch (item.category) {
    case 'Blocked':
      return 'importblocked'
    case 'Imported':
      return 'imported'
    case 'Failed':
      return 'failed'
    case 'Stalled':
      return 'stalled'
    case 'InProgress':
    default:
      // Preserve the specific in-flight state for the badge (downloading/queued/processing/...).
      return (item.status || 'downloading').toLowerCase()
  }
}

// Live progress overlay keyed by download id, taken from the SignalR queue snapshot.
const liveProgressById = computed(() => {
  const map = new Map<string, QueueItem>()
  for (const q of queue.value) map.set(q.id, q)
  return map
})

const toActivityRow = (item: ActivityItem): ActivityRow => {
  const live = item.category === 'InProgress' ? liveProgressById.value.get(item.id) : undefined
  const isDdl = (item.downloadClientId || '').toString().toUpperCase() === 'DDL'
  return {
    id: item.id,
    audiobookId: item.audiobookId,
    title: item.title,
    quality: '',
    language: item.series,
    status: badgeClassFor(item),
    category: item.category,
    progress: live?.progress ?? item.progress,
    size: live?.size ?? item.totalSize,
    downloaded: live?.downloaded ?? item.downloadedSize,
    downloadSpeed: live?.downloadSpeed ?? 0,
    eta: live?.eta,
    reason: item.reason,
    attemptCount: item.attemptCount,
    whenIso: item.activityAt,
    completedIso: item.completedAt,
    downloadClient: item.downloadClientName ?? item.downloadClientId ?? 'Unknown Client',
    downloadClientId: item.downloadClientId,
    downloadClientType: item.downloadClientType ?? (isDdl ? 'DDL' : 'external'),
    attempts: item.attempts ?? [],
    canRemove: item.category === 'InProgress' || item.category === 'Blocked',
  }
}

// #717's move jobs are live client-side state (SignalR), not part of the
// server activity feed — adapt each active job into an ActivityRow and merge
// it ahead of the feed rows below.
const moveJobRows = computed<ActivityRow[]>(() =>
  moveJobsStore.trackedJobs
    .filter(
      (job) =>
        job.status !== 'Completed' &&
        job.status !== 'Failed' &&
        job.status !== 'NeedsAttention' &&
        job.status !== 'Superseded',
    )
    .map((job) => ({
      id: `move:${job.jobId}`,
      audiobookId: job.audiobookId,
      title: 'Library move',
      quality: '',
      status: job.status === 'Queued' || job.status === 'RetryScheduled' ? 'queued' : 'downloading',
      category: 'InProgress' as ActivityCategory,
      progress: job.progress,
      size: 0,
      downloaded: 0,
      downloadSpeed: 0,
      reason: job.error ?? (job.phase ? `Library move · ${job.phase}` : undefined),
      attemptCount: 0,
      whenIso: '',
      downloadClient: job.phase ? `Library move · ${job.phase}` : 'Library move',
      downloadClientId: 'LISTENARR_MOVE',
      downloadClientType: 'move',
      attempts: [],
      canRemove: false,
    })),
)

// Active move jobs render ahead of the server feed's rows.
const allActivityItems = computed<ActivityRow[]>(() => [
  ...moveJobRows.value,
  ...(activity.value?.items ?? []).map(toActivityRow),
])

// Summary chip definitions, in display order. `category: null` is the default (in-progress + blocked) view.
interface SummaryChip {
  key: string
  label: string
  category: ActivityCategory | null
  count: number
  windowed: boolean
}

const summaryChips = computed<SummaryChip[]>(() => {
  const s = activity.value?.summary
  return [
    {
      key: 'inprogress',
      label: 'In progress',
      category: 'InProgress',
      count: (s?.inProgress ?? 0) + moveJobRows.value.length,
      windowed: false,
    },
    {
      key: 'blocked',
      label: 'Blocked',
      category: 'Blocked',
      count: s?.blocked ?? 0,
      windowed: false,
    },
    {
      key: 'imported',
      label: 'Imported',
      category: 'Imported',
      count: s?.imported ?? 0,
      windowed: true,
    },
    { key: 'failed', label: 'Failed', category: 'Failed', count: s?.failed ?? 0, windowed: true },
    {
      key: 'stalled',
      label: 'Stalled',
      category: 'Stalled',
      count: s?.stalled ?? 0,
      windowed: true,
    },
  ]
})

const windowHours = computed(() => activity.value?.summary.windowHours ?? 24)
const failureReasons = computed(() => activity.value?.summary.failureReasons ?? [])

// Build a lookup map from audiobook ID -> title for resolving friendly names
const audiobookTitleMap = computed(() => {
  const map = new Map<number, string>()
  for (const ab of libraryStore.audiobooks) {
    if (ab.id && ab.title) map.set(ab.id, ab.title)
  }
  return map
})

const getDisplayTitle = (item: { audiobookId?: number; title: string }): string => {
  if (item.audiobookId) {
    const abTitle = audiobookTitleMap.value.get(item.audiobookId)
    if (abTitle) return abTitle
  }
  return item.title
}

// Filter by text search across title, client, status
const filteredQueue = computed(() => {
  const text = filterText.value.trim().toLowerCase()
  if (!text) return allActivityItems.value

  return allActivityItems.value.filter((item) => {
    const displayTitle = getDisplayTitle(item)
    return (
      (displayTitle && displayTitle.toLowerCase().includes(text)) ||
      (item.title && item.title.toLowerCase().includes(text)) ||
      (item.downloadClient && item.downloadClient.toLowerCase().includes(text)) ||
      (item.status && item.status.toLowerCase().includes(text)) ||
      (item.reason && item.reason.toLowerCase().includes(text))
    )
  })
})

// Load the activity summary + collapsed list for the currently selected category.
const loadActivity = async () => {
  try {
    activity.value = await apiService.getActivity({
      category: activeCategory.value ?? undefined,
    })
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'ActivityView',
      operation: 'loadActivity',
    })
  }
}

const selectCategory = (category: ActivityCategory | null) => {
  // Clicking the active chip returns to the default view.
  activeCategory.value = activeCategory.value === category ? null : category
  void refreshQueue()
}

const refreshQueue = async () => {
  // Intentionally does NOT call apiService.getQueue(): that endpoint runs the full, expensive
  // DownloadQueueService pipeline (path translation + metadata enrichment + orphan reconciliation
  // over thousands of records, ~10-30s) on every call. Live progress for in-progress rows already
  // arrives via the SignalR `QueueUpdate` subscription (see onMounted), and getActivity() carries
  // DB progress. Polling getQueue here every 30s was the source of the CPU spike while the
  // Activity tab is open.
  loading.value = true
  try {
    await loadActivity()
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'ActivityView',
      operation: 'refreshQueue',
    })
  } finally {
    loading.value = false
  }
}

const removeFromQueue = async (item: ActivityRow) => {
  itemToRemove.value = item

  if (
    (item.downloadClientId || '').toString().toUpperCase() === 'DDL' ||
    (item.downloadClientType || '').toString().toUpperCase() === 'DDL'
  ) {
    clientHasQueueEntry.value = true
    showRemoveModal.value = true
    return
  }

  const found = queue.value.some((q) => q.id === item.id)
  clientHasQueueEntry.value = found
  showRemoveModal.value = true
}

const confirmRemove = async () => {
  if (!itemToRemove.value) return

  removing.value = true
  try {
    if (
      (itemToRemove.value.downloadClientId || '').toString().toUpperCase() === 'DDL' ||
      (itemToRemove.value.downloadClientType || '').toString().toUpperCase() === 'DDL'
    ) {
      await apiService.cancelDownload(itemToRemove.value.id)
      await refreshQueue()
    } else {
      if (clientHasQueueEntry.value === false) {
        await apiService.cancelDownload(itemToRemove.value.id)
        await refreshQueue()
      } else {
        await apiService.removeFromQueue(itemToRemove.value.id, itemToRemove.value.downloadClientId)
        await refreshQueue()
      }
    }

    showRemoveModal.value = false
    itemToRemove.value = null
    clientHasQueueEntry.value = null
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'ActivityView',
      operation: 'removeFromQueue',
    })
    const toast = useToast()
    toast.error('Remove failed', (err as Error).message)
  } finally {
    removing.value = false
  }
}

const formatStatus = (status: string): string => {
  const labels: Record<string, string> = {
    downloading: 'Downloading',
    queued: 'Queued',
    paused: 'Paused',
    completed: 'Completed',
    failed: 'Failed',
    processing: 'Processing',
    moving: 'Moving',
    importpending: 'Importing',
    importblocked: 'Import Blocked',
    imported: 'Imported',
    stalled: 'Stalled',
    // Raw DownloadStatus names that map to the Imported category (used in the attempt-history modal).
    moved: 'Imported',
    ready: 'Imported',
  }
  return labels[status] ?? status.charAt(0).toUpperCase() + status.slice(1)
}

// Maps a download-client implementation type to an icon. Torrent clients get a magnet, usenet
// clients a cloud-download, direct downloads a globe; anything unknown falls back to a drive.
const clientIcon = (item: ActivityRow): Component => {
  const type = (item.downloadClientType || '').toLowerCase()
  if (type === 'ddl') return PhGlobe
  if (['qbittorrent', 'transmission', 'deluge', 'rtorrent', 'utorrent'].includes(type))
    return PhMagnet
  if (['sabnzbd', 'nzbget'].includes(type)) return PhCloudArrowDown
  return PhHardDrives
}

// Human label for the client implementation, used in the client chip's tooltip.
const clientKindLabel = (type: string): string => {
  const t = (type || '').toLowerCase()
  if (t === 'ddl') return 'direct download'
  if (['qbittorrent', 'transmission', 'deluge', 'rtorrent', 'utorrent'].includes(t))
    return 'torrent client'
  if (['sabnzbd', 'nzbget'].includes(t)) return 'usenet client'
  return 'download client'
}

const clientTooltip = (item: ActivityRow): string => {
  const where = item.downloadClient || 'an unknown client'
  return `Downloading via ${where} (${clientKindLabel(item.downloadClientType)})`
}

// Explains what a status means — and, crucially, why a row can read ~100% yet still show
// Downloading/Processing: the progress bar tracks downloaded bytes, the badge tracks pipeline stage.
const statusTooltip = (item: ActivityRow): string => {
  const nearDone = item.progress >= 99.5
  switch (item.status) {
    case 'downloading':
      return nearDone
        ? 'Bytes are fully downloaded, but the client is still active (e.g. seeding) or Listenarr has not yet detected completion.'
        : 'Actively downloading from the client.'
    case 'processing':
      return nearDone
        ? 'Download finished; Listenarr is now importing and moving the files into your library.'
        : 'Listenarr is processing the completed download (importing / moving files).'
    case 'queued':
      return 'Waiting in the download client queue to start.'
    case 'paused':
      return 'Download is paused in the client.'
    case 'importpending':
      return 'Waiting on import completion or manual interaction before the files land in the library.'
    case 'importblocked':
      return 'Finished downloading, but the import needs attention. Retryable from the item.'
    case 'imported':
      return 'The files made it into your library.'
    case 'completed':
      return 'Download completed.'
    case 'stalled':
      return 'Made no download progress and was reaped by the stall timer.'
    case 'failed':
      return 'The download failed. See the reason for details.'
    default:
      return formatStatus(item.status)
  }
}

// Reuses the row badge mapping for a single historical attempt (category drives the colour/class).
const attemptBadgeClass = (attempt: ActivityAttempt): string => {
  switch (attempt.category) {
    case 'Blocked':
      return 'importblocked'
    case 'Imported':
      return 'imported'
    case 'Failed':
      return 'failed'
    case 'Stalled':
      return 'stalled'
    case 'InProgress':
    default:
      return (attempt.status || 'downloading').toLowerCase()
  }
}

const openAttempts = (item: ActivityRow) => {
  attemptsItem.value = item
  showAttemptsModal.value = true
}

const closeAttempts = () => {
  showAttemptsModal.value = false
  attemptsItem.value = null
}

// Relative "when" label for the activity timestamp (e.g. "5m ago", "3h ago", "Jun 5").
const formatWhen = (iso?: string): string => {
  if (!iso) return '-'
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return '-'
  const diffMs = Date.now() - then
  const sec = Math.floor(diffMs / 1000)
  if (sec < 45) return 'just now'
  const min = Math.floor(sec / 60)
  if (min < 60) return `${min}m ago`
  const hr = Math.floor(min / 60)
  if (hr < 24) return `${hr}h ago`
  const day = Math.floor(hr / 24)
  if (day < 7) return `${day}d ago`
  return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })
}

const formatWhenTitle = (row: ActivityRow): string => {
  const parts: string[] = []
  if (row.whenIso) parts.push(`Added ${new Date(row.whenIso).toLocaleString()}`)
  if (row.completedIso) parts.push(`Finished ${new Date(row.completedIso).toLocaleString()}`)
  if (row.attemptCount > 1) parts.push(`${row.attemptCount} attempts`)
  return parts.join(' · ')
}

const formatEta = (seconds: number): string => {
  if (seconds < 60) return `${seconds}s`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`
  return `${Math.floor(seconds / 86400)}d`
}

// Subscribe to SignalR for real-time updates
onMounted(async () => {
  moveJobsStore.start()
  updateActivityLayoutMode()
  if (typeof window !== 'undefined') {
    window.addEventListener('resize', handleViewportResize, { passive: true })
  }

  unsubscribeQueue = signalRService.onQueueUpdate((updatedQueue) => {
    applyQueueSnapshot(updatedQueue)
  })

  await refreshQueue()
  await syncActivityLayout()

  queueRefreshInterval = setInterval(async () => {
    try {
      await refreshQueue()
    } catch (err) {
      errorTracking.captureException(err as Error, {
        component: 'ActivityView',
        operation: 'queueRefreshInterval',
      })
    }
  }, 30000)
})

onUnmounted(() => {
  if (typeof window !== 'undefined') {
    window.removeEventListener('resize', handleViewportResize)
  }

  if (unsubscribeQueue) {
    unsubscribeQueue()
  }

  if (queueRefreshInterval) {
    clearInterval(queueRefreshInterval)
    queueRefreshInterval = null
  }
})
</script>

<style scoped>
.activity-view {
  padding: 1em;
}

.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.75rem;
  flex-wrap: wrap;
  gap: 0.75rem;
}

.page-header h1 {
  margin: 0;
  color: white;
  font-size: 2rem;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-weight: 500;
}

.page-header h1 svg {
  width: 32px;
  height: 32px;
}

.activity-actions {
  display: flex;
  gap: 0.75rem;
  align-items: center;
}

/* Summary chips */
.activity-summary {
  display: flex;
  flex-wrap: wrap;
  gap: 0.6rem;
  margin-bottom: 0.75rem;
}

.summary-chip {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.45rem 0.85rem;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 8px;
  background: #252525;
  color: #adb5bd;
  cursor: pointer;
  transition:
    border-color 0.15s,
    background-color 0.15s,
    color 0.15s;
}

.summary-chip:hover {
  background: #2c2c2c;
  border-color: rgba(255, 255, 255, 0.16);
}

.summary-chip.active {
  border-color: #4dabf7;
  background: rgba(77, 171, 247, 0.12);
  color: #fff;
}

.chip-count {
  font-size: 1.05rem;
  font-weight: 700;
  color: #fff;
}

.chip-label {
  font-size: 0.8rem;
  white-space: nowrap;
}

.chip-window {
  color: #868e96;
  font-size: 0.72rem;
}

/* Per-category count accents */
.chip-inprogress .chip-count {
  color: #4dabf7;
}
.chip-blocked .chip-count {
  color: #fab005;
}
.chip-imported .chip-count {
  color: #20c997;
}
.chip-failed .chip-count {
  color: #fa5252;
}
.chip-stalled .chip-count {
  color: #fd7e14;
}

/* Failure-reason breakdown */
.failure-reasons {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.4rem;
  margin-bottom: 0.75rem;
  font-size: 0.8rem;
  color: #868e96;
}

.failure-reasons-label {
  font-weight: 600;
}

.failure-reason-pill {
  padding: 0.2rem 0.55rem;
  border-radius: 6px;
  background: rgba(250, 82, 82, 0.1);
  color: #e0867f;
}

.failure-reason-pill strong {
  color: #fff;
}

.filter-input-wrapper {
  position: relative;
  display: flex;
  align-items: center;
}

.filter-icon {
  position: absolute;
  left: 0.75rem;
  color: #868e96;
  width: 16px;
  height: 16px;
  pointer-events: none;
}

.filter-input {
  background: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  color: #fff;
  padding: 0.5rem 2rem 0.5rem 2.25rem;
  font-size: 0.875rem;
  width: 220px;
  height: var(--control-height, 40px);
  box-sizing: border-box;
  transition:
    border-color 0.2s,
    box-shadow 0.2s;
}

.filter-input::placeholder {
  color: #868e96;
}

.filter-input:focus {
  outline: none;
  border-color: #4dabf7;
  box-shadow: 0 0 0 2px rgba(77, 171, 247, 0.15);
}

.filter-clear {
  position: absolute;
  right: 0.5rem;
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.25rem;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: color 0.2s;
}

.filter-clear:hover {
  color: #fa5252;
  background: rgba(250, 82, 82, 0.15);
}

.filter-clear svg {
  width: 14px;
  height: 14px;
}

/* Queue health banner */
.queue-health-banner {
  display: flex;
  align-items: flex-start;
  gap: 0.75rem;
  padding: 0.9rem 1rem;
  margin-bottom: 1rem;
  border: 1px solid rgba(217, 119, 6, 0.28);
  background: rgba(245, 158, 11, 0.12);
  color: var(--color-text, #1f2937);
  border-radius: 0.85rem;
}

.queue-health-copy {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
}

.queue-health-copy strong {
  font-size: 0.95rem;
}

.queue-health-copy span {
  font-size: 0.88rem;
  opacity: 0.86;
}

/* Grid container with virtual scrolling */
.queue-grid-container {
  height: calc(100vh - 200px);
  overflow-y: auto;
  position: relative;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  background: #1e1e1e;
}

.queue-grid-container.is-static {
  height: auto;
  overflow-y: visible;
}

/* Desktop grid columns shared by header and rows */
.queue-header,
.queue-row {
  display: grid;
  grid-template-columns:
    minmax(0, 3fr) minmax(0, 1fr) minmax(0, 1fr) minmax(0, 2fr) minmax(0, 1fr)
    minmax(0, 1fr) 40px;
  align-items: center;
}

.queue-header {
  position: sticky;
  top: 0;
  z-index: 2;
  background: #252525;
  padding: 0.5rem 0;
  border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.queue-header > div {
  padding: 0 0.75rem;
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  color: #868e96;
  white-space: nowrap;
}

.queue-body-spacer {
  position: relative;
  width: 100%;
}

.queue-body-spacer.is-static {
  position: static;
}

.queue-body {
  position: absolute;
  top: 0;
  left: 0;
  width: 100%;
  overflow: hidden;
}

.queue-body.is-static {
  position: static;
}

/* Grid rows */
.queue-row {
  transition: background-color 0.15s;
}

.queue-row:hover {
  background-color: rgba(255, 255, 255, 0.03);
}

.queue-row > div {
  padding: 0.5rem 0.75rem;
  font-size: 0.85rem;
  color: #adb5bd;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  display: flex;
  align-items: center;
  align-self: stretch;
}

/* Progress column needs visible overflow for its flex layout */
.queue-row > .col-progress {
  overflow: visible;
}

/* Actions column: center the button */
.queue-row > .col-actions {
  justify-content: center;
  overflow: visible;
}

/* Title cell */
.title-cell {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  min-width: 0;
}

.title-main {
  display: flex;
  flex-direction: column;
  min-width: 0;
  gap: 0.1rem;
}

.title-row {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  min-width: 0;
}

.title-reason {
  color: #868e96;
  font-size: 0.72rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.client-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.2rem;
  flex-shrink: 0;
  font-size: 0.68rem;
  color: #adb5bd;
  background: rgba(255, 255, 255, 0.05);
  border-radius: 4px;
  padding: 0.05rem 0.35rem;
  white-space: nowrap;
}

.client-icon {
  width: 12px;
  height: 12px;
  color: #868e96;
}

.title-text,
.title-link {
  color: white;
  font-weight: 500;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  /* Shrink within the flex title row so the client chip stays visible and the title ellipsizes. */
  min-width: 0;
  flex: 0 1 auto;
}

.title-link {
  text-decoration: none;
}

.title-link:hover {
  color: #4dabf7;
}

.quality-tag {
  flex-shrink: 0;
  font-size: 0.7rem;
  padding: 0.1rem 0.4rem;
  border-radius: 4px;
  background: rgba(81, 207, 102, 0.12);
  color: #51cf66;
  font-weight: 500;
}

/* When cell */
.when-text {
  color: #adb5bd;
  font-size: 0.8rem;
  white-space: nowrap;
}

.attempts-badge {
  margin-left: 0.4rem;
  padding: 0.05rem 0.3rem;
  border: none;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.06);
  color: #868e96;
  font-size: 0.65rem;
  font-weight: 600;
  cursor: pointer;
  transition:
    background-color 0.15s,
    color 0.15s;
}

.attempts-badge:hover {
  background: rgba(77, 171, 247, 0.18);
  color: #4dabf7;
}

/* Progress cell */
.progress-cell {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  width: 100%;
  min-width: 0;
}

.progress-cell :deep(.progress-wrapper) {
  flex: 1;
  min-width: 0;
}

.progress-label {
  font-size: 0.8rem;
  font-weight: 500;
  color: white;
  white-space: nowrap;
  min-width: 40px;
  text-align: right;
}

/* Size */
.size-text {
  font-size: 0.8rem;
  color: #868e96;
}

/* Speed */
.speed-text {
  color: #51cf66;
  font-size: 0.8rem;
}

/* ETA */
.eta-text {
  color: #ffd43b;
  font-size: 0.8rem;
}

.muted {
  color: #495057;
  font-size: 0.8rem;
}

/* Status badges */
.status-badge {
  padding: 0.2rem 0.5rem;
  border-radius: 4px;
  font-size: 0.7rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.3px;
  display: inline-block;
}

.status-badge.completed {
  background-color: rgba(81, 207, 102, 0.15);
  color: #51cf66;
}

.status-badge.downloading {
  background-color: rgba(77, 171, 247, 0.15);
  color: #4dabf7;
}

.status-badge.failed {
  background-color: rgba(250, 82, 82, 0.15);
  color: #fa5252;
}

.status-badge.paused {
  background-color: rgba(255, 212, 59, 0.15);
  color: #ffd43b;
}

.status-badge.queued {
  background-color: rgba(134, 142, 150, 0.15);
  color: #868e96;
}

.status-badge.processing,
.status-badge.moving {
  background-color: rgba(190, 75, 219, 0.15);
  color: #be4bdb;
}

.status-badge.importpending {
  background-color: rgba(255, 146, 43, 0.15);
  color: #ff922b;
}

/* Blocked = needs action (amber), distinct from a hard failure (red) */
.status-badge.importblocked {
  background-color: rgba(250, 176, 5, 0.18);
  color: #fab005;
}

.status-badge.imported {
  background-color: rgba(32, 201, 151, 0.15);
  color: #20c997;
}

/* Stalled = reaped for no progress; muted orange, between blocked and failed */
.status-badge.stalled {
  background-color: rgba(253, 126, 20, 0.15);
  color: #fd7e14;
}

.stale-badge {
  display: inline-block;
  margin-left: 0.35rem;
  padding: 0.1rem 0.35rem;
  border-radius: 4px;
  background: rgba(245, 158, 11, 0.16);
  color: #b45309;
  font-size: 0.65rem;
  font-weight: 600;
}

/* Actions */
.btn-danger-icon {
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.35rem;
  border-radius: 4px;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: all 0.2s;
}

.btn-danger-icon:hover {
  background: rgba(250, 82, 82, 0.15);
  color: #fa5252;
}

.btn-danger-icon svg {
  width: 16px;
  height: 16px;
}

/* Modal Styles */
.modal-overlay {
  position: fixed;
  top: 0;
  left: 0;
  right: 0;
  bottom: 0;
  background-color: rgba(0, 0, 0, 0.85);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
  animation: fadeIn 0.2s ease;
  backdrop-filter: blur(4px);
}

.modal-content {
  background-color: #2a2a2a;
  border-radius: 6px;
  width: 90%;
  max-width: 500px;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.5);
  animation: slideUp 0.3s ease;
  border: 1px solid rgba(255, 255, 255, 0.1);
}

@keyframes slideUp {
  from {
    transform: translateY(20px);
    opacity: 0;
  }
  to {
    transform: translateY(0);
    opacity: 1;
  }
}

.modal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 1.5rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.1);
}

.modal-header h3 {
  margin: 0;
  color: white;
  font-size: 1.25rem;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.625rem;
}

.modal-header h3 svg {
  color: #ffd43b;
  width: 24px;
  height: 24px;
}

.modal-close {
  background: none;
  border: none;
  color: #868e96;
  cursor: pointer;
  padding: 0.5rem;
  border-radius: 6px;
  transition: all 0.2s;
  font-size: 1.5rem;
  line-height: 1;
}

.modal-close:hover {
  background-color: rgba(255, 255, 255, 0.08);
  color: white;
}

.modal-close svg {
  width: 20px;
  height: 20px;
}

.modal-body {
  padding: 1.5rem;
}

.modal-body > p:first-child {
  color: #adb5bd;
  margin: 0 0 1rem 0;
  font-size: 1rem;
  line-height: 1.5;
}

.remove-item-info {
  background-color: rgba(0, 0, 0, 0.3);
  border-left: 4px solid #ffd43b;
  padding: 1rem;
  border-radius: 6px;
  margin-bottom: 1rem;
}

.remove-item-info strong {
  color: white;
  display: block;
  margin-bottom: 0.5rem;
  font-size: 1rem;
  font-weight: 500;
}

.item-details {
  display: flex;
  gap: 1rem;
  font-size: 0.875rem;
  color: #868e96;
  flex-wrap: wrap;
}

.item-details span {
  display: flex;
  align-items: center;
  gap: 0.35rem;
}

.item-details svg {
  width: 14px;
  height: 14px;
}

.warning-text {
  color: #ffd43b;
  font-size: 0.875rem;
  display: flex;
  align-items: flex-start;
  gap: 0.625rem;
  background-color: rgba(255, 212, 59, 0.1);
  padding: 0.875rem;
  border-radius: 6px;
  margin: 0;
  border: 1px solid rgba(255, 212, 59, 0.25);
  line-height: 1.5;
}

.warning-text svg {
  flex-shrink: 0;
  margin-top: 0.1rem;
  width: 18px;
  height: 18px;
}

.modal-footer {
  justify-content: flex-end;
  gap: 0.75rem;
}

.btn-danger {
  background: #fa5252;
  color: white;
  border: none;
  padding: 0.65rem 1.25rem;
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  transition: all 0.2s;
  font-size: 0.9rem;
  box-shadow: 0 2px 8px rgba(250, 82, 82, 0.3);
}

.btn-danger:hover:not(:disabled) {
  background: #e03131;
  transform: translateY(-1px);
  box-shadow: 0 4px 12px rgba(250, 82, 82, 0.4);
}

.btn-danger:active:not(:disabled) {
  transform: translateY(0);
}

.btn-danger:disabled {
  opacity: 0.5;
  cursor: not-allowed;
  transform: none;
}

.btn-danger svg {
  width: 16px;
  height: 16px;
}

/* Attempt-history modal */
.attempts-modal {
  max-width: 560px;
}

.attempts-subtitle {
  color: #adb5bd;
  font-size: 0.9rem;
  margin: 0 0 1rem 0;
}

.attempts-subtitle strong {
  color: #fff;
}

.attempts-list {
  list-style: none;
  margin: 0;
  padding: 0;
  max-height: 50vh;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.attempt-row {
  background: rgba(0, 0, 0, 0.25);
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  padding: 0.6rem 0.75rem;
}

.attempt-head {
  display: flex;
  align-items: center;
  gap: 0.6rem;
  flex-wrap: wrap;
}

.attempt-when {
  color: #adb5bd;
  font-size: 0.78rem;
}

.attempt-client {
  color: #868e96;
  font-size: 0.72rem;
  margin-left: auto;
}

.attempt-reason {
  margin: 0.4rem 0 0 0;
  color: #ced4da;
  font-size: 0.8rem;
  line-height: 1.4;
  word-break: break-word;
}

.attempt-reason.muted {
  color: #6c757d;
  font-style: italic;
}

/* Mobile responsive */
@media (max-width: 768px) {
  .page-header {
    flex-direction: column;
    align-items: flex-start;
    gap: 0.75rem;
    margin-bottom: 1rem;
  }

  .activity-actions {
    width: 100%;
    flex-wrap: wrap;
  }

  .filter-input-wrapper {
    flex: 1;
    min-width: 0;
  }

  .filter-input {
    width: 100%;
  }

  .queue-grid-container {
    height: auto;
    overflow-y: visible;
    border: none;
    background: transparent;
  }

  .queue-header {
    display: none;
  }

  .queue-body-spacer {
    height: auto !important;
    position: static !important;
  }

  .queue-body {
    position: static !important;
    transform: none !important;
  }

  /* Each row becomes a card */
  .queue-row {
    grid-template-columns: 1fr auto;
    grid-template-rows: auto auto auto;
    gap: 0.3rem 0.5rem;
    padding: 0.75rem;
    margin-bottom: 0.5rem;
    background: #2a2a2a;
    border-radius: 6px;
    border: 1px solid rgba(255, 255, 255, 0.06);
  }

  .queue-row:hover {
    background-color: #2f2f2f;
  }

  .queue-row > div {
    padding: 0;
    border: none;
    overflow: visible;
    white-space: normal;
  }

  /* Hide columns that don't fit mobile */
  .queue-row .col-quality,
  .queue-row .col-eta {
    display: none;
  }

  /* When: small, under the title row */
  .queue-row .col-when {
    grid-column: 1;
    grid-row: 3;
    font-size: 0.72rem;
    color: #868e96;
  }

  /* Row 1: Title (left) + Status badge (right) */
  .queue-row .col-title {
    grid-column: 1;
    grid-row: 1;
    min-width: 0;
  }

  .queue-row .col-status {
    grid-column: 2;
    grid-row: 1;
    display: flex;
    align-items: center;
    justify-content: flex-end;
    white-space: nowrap;
  }

  /* Row 2: Progress bar (left) + Actions (right) */
  .queue-row .col-progress {
    grid-column: 1;
    grid-row: 2;
  }

  .queue-row .col-actions {
    grid-column: 2;
    grid-row: 2;
    display: flex;
    align-items: center;
    justify-content: flex-end;
  }
}
</style>
