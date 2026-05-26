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
  Polls GET /library/move/summary on a 5s interval while mounted (paused
  automatically when the browser tab is hidden — Page Visibility API).
  Displays counts + the most recent completed/failed entries so the user
  can see at-a-glance whether the move queue is draining, stuck, or
  failing.

  Renders nothing when the queue is completely empty AND no history exists
  (total === 0), so a brand-new instance doesn't show an empty banner.

  Embedded in both OrganizeLibraryModal (top of body) and the Settings
  Library Maintenance section. No SignalR plumbing — polling is cheap
  (single table-count query backend-side, ~tens of bytes over the wire)
  and avoids the connect / reconnect / fallback complexity of subscribing
  to MoveJobUpdate from inside a transient modal.
-->
<template>
  <div v-if="visible" class="move-queue-banner" :class="bannerClass">
    <div class="banner-row">
      <span class="banner-label">Move queue:</span>
      <span class="banner-counts">
        <strong>{{ summary!.completed }}</strong> of <strong>{{ totalActive }}</strong> done
        <span v-if="summary!.processing > 0"
          ><span class="dot">·</span><span class="counts-processing"
            >{{ summary!.processing }} processing</span
          ></span
        >
        <span v-if="summary!.queued > 0"
          ><span class="dot">·</span><span class="counts-queued">{{ summary!.queued }} queued</span></span
        >
        <span v-if="summary!.failed > 0"
          ><span class="dot">·</span><span class="counts-failed">{{ summary!.failed }} failed</span></span
        >
        <span v-if="summary!.other > 0"
          ><span class="dot">·</span><span class="counts-other"
            >{{ summary!.other }} other / cancelled</span
          ></span
        >
      </span>
      <span v-if="lastFetched" class="banner-updated" :title="lastFetchedAbsolute">
        updated {{ lastFetchedRelative }}
      </span>
    </div>

    <div v-if="latestCompleted" class="banner-row banner-detail">
      <span class="banner-detail-label">Last completed:</span>
      <span class="banner-detail-text" :title="latestCompleted.requestedPath || ''">
        ab #{{ latestCompleted.audiobookId }} →
        {{ shortPath(latestCompleted.requestedPath) || '(unknown target)' }}
      </span>
      <span v-if="latestCompleted.updatedAt" class="banner-detail-time">
        {{ formatRelative(latestCompleted.updatedAt) }}
      </span>
    </div>

    <div v-if="latestFailed" class="banner-row banner-detail banner-detail-failed">
      <span class="banner-detail-label">Last failure:</span>
      <span class="banner-detail-text" :title="latestFailed.error || ''">
        ab #{{ latestFailed.audiobookId }}: {{ (latestFailed.error || 'unknown error').slice(0, 100)
        }}{{ (latestFailed.error || '').length > 100 ? '…' : '' }}
      </span>
      <span v-if="latestFailed.updatedAt" class="banner-detail-time">
        {{ formatRelative(latestFailed.updatedAt) }}
      </span>
    </div>

    <div v-if="loadError" class="banner-row banner-detail banner-detail-failed">
      <span class="banner-detail-label">Couldn't fetch summary:</span>
      <span class="banner-detail-text">{{ loadError }}</span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onBeforeUnmount, watch } from 'vue'
import { apiService } from '@/services/api'
import { errorTracking } from '@/services/errorTracking'
import type { MoveQueueSummary, MoveQueueJobSummary } from '@/types'

const props = withDefaults(
  defineProps<{
    /** Poll interval in milliseconds. Default 5000. Set to 0 to disable polling (single fetch on mount). */
    intervalMs?: number
    /** How many recent completed/failed jobs to ask the API for (passed through to recentLimit). Default 3. */
    recentLimit?: number
  }>(),
  { intervalMs: 5000, recentLimit: 3 },
)

const summary = ref<MoveQueueSummary | null>(null)
const loadError = ref<string | null>(null)
const lastFetched = ref<Date | null>(null)

// Refresh "updated X ago" on a 1s tick — purely cosmetic, lets the timestamp
// in the corner age in real time instead of jumping every poll interval.
const now = ref<number>(Date.now())
let nowTimer: number | null = null

let pollTimer: number | null = null
let visibilityHandler: (() => void) | null = null

// The banner is meant to be ambient — render nothing when there's literally
// no history (totally fresh install) so it doesn't clutter pages where it'd
// otherwise just say "0 of 0 done".
const visible = computed(() => {
  if (loadError.value) return true
  if (!summary.value) return false
  return summary.value.total > 0
})

// Most operations are "X of Y done" where Y is the active set (queued +
// processing + completed + failed). Cancelled jobs are excluded from Y so a
// stale cancel-all doesn't make the denominator misleading.
const totalActive = computed(() => {
  if (!summary.value) return 0
  return (
    summary.value.completed + summary.value.queued + summary.value.processing + summary.value.failed
  )
})

const bannerClass = computed(() => {
  if (!summary.value) return ''
  if (summary.value.processing > 0 || summary.value.queued > 0) return 'is-active'
  if (summary.value.failed > 0 && summary.value.completed === 0) return 'is-failing'
  if (summary.value.completed > 0) return 'is-done'
  return ''
})

const latestCompleted = computed<MoveQueueJobSummary | null>(
  () => summary.value?.recentCompleted?.[0] ?? null,
)
const latestFailed = computed<MoveQueueJobSummary | null>(
  () => summary.value?.recentFailed?.[0] ?? null,
)

function shortPath(p: string | null): string {
  if (!p) return ''
  // Trim /audiobooks/ prefix to save horizontal space. Full path is in title attr.
  return p.replace(/^\/?audiobooks\//i, '')
}

function formatRelative(iso: string): string {
  try {
    const then = new Date(iso).getTime()
    const seconds = Math.floor((now.value - then) / 1000)
    if (seconds < 0) return 'just now'
    if (seconds < 5) return 'just now'
    if (seconds < 60) return `${seconds}s ago`
    const minutes = Math.floor(seconds / 60)
    if (minutes < 60) return `${minutes}m ago`
    const hours = Math.floor(minutes / 60)
    if (hours < 24) return `${hours}h ago`
    const days = Math.floor(hours / 24)
    return `${days}d ago`
  } catch {
    return ''
  }
}

const lastFetchedRelative = computed(() => {
  if (!lastFetched.value) return ''
  return formatRelative(lastFetched.value.toISOString())
})
const lastFetchedAbsolute = computed(() => {
  if (!lastFetched.value) return ''
  return lastFetched.value.toLocaleString()
})

async function fetchSummary() {
  try {
    summary.value = await apiService.getMoveQueueSummary(props.recentLimit)
    loadError.value = null
    lastFetched.value = new Date()
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : 'unknown error'
    errorTracking.captureException(err as Error, {
      component: 'MoveQueueStatusBanner',
      operation: 'fetchSummary',
    })
  }
}

function startPolling() {
  stopPolling()
  // Fire once immediately so the banner populates without waiting an interval.
  void fetchSummary()
  if (props.intervalMs > 0) {
    pollTimer = window.setInterval(() => {
      // Skip the network call when the tab is hidden — saves bandwidth and
      // avoids piling up polls during long backgrounded sessions. The next
      // visibility-change handler will fire a fresh fetch.
      if (document.hidden) return
      void fetchSummary()
    }, props.intervalMs)
  }
}

function stopPolling() {
  if (pollTimer != null) {
    window.clearInterval(pollTimer)
    pollTimer = null
  }
}

onMounted(() => {
  startPolling()
  nowTimer = window.setInterval(() => {
    now.value = Date.now()
  }, 1000)
  visibilityHandler = () => {
    if (!document.hidden) void fetchSummary()
  }
  document.addEventListener('visibilitychange', visibilityHandler)
})

onBeforeUnmount(() => {
  stopPolling()
  if (nowTimer != null) {
    window.clearInterval(nowTimer)
    nowTimer = null
  }
  if (visibilityHandler) {
    document.removeEventListener('visibilitychange', visibilityHandler)
    visibilityHandler = null
  }
})

// If the parent updates intervalMs at runtime (rare but supported), restart.
watch(
  () => props.intervalMs,
  () => startPolling(),
)
</script>

<style scoped>
.move-queue-banner {
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.025);
  border-radius: 6px;
  padding: 8px 12px;
  margin-bottom: 12px;
  font-size: 12px;
  color: #c0c0c0;
}
.move-queue-banner.is-active {
  border-left: 3px solid #5ea1ff;
  background: rgba(94, 161, 255, 0.05);
}
.move-queue-banner.is-failing {
  border-left: 3px solid #f06060;
  background: rgba(240, 96, 96, 0.06);
}
.move-queue-banner.is-done {
  border-left: 3px solid #6fc080;
  background: rgba(111, 192, 128, 0.04);
}
.banner-row {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 8px;
}
.banner-row + .banner-row {
  margin-top: 4px;
}
.banner-label {
  font-weight: 600;
  color: #e0e0e0;
}
.banner-counts {
  flex: 1 1 auto;
}
.banner-counts strong {
  color: #fff;
}
.dot {
  margin: 0 6px;
  color: #555;
}
.counts-processing {
  color: #5ea1ff;
}
.counts-queued {
  color: #c0c0c0;
}
.counts-failed {
  color: #f08080;
}
.counts-other {
  color: #888;
}
.banner-updated {
  font-size: 10px;
  color: #888;
  cursor: help;
}
.banner-detail {
  font-size: 11px;
  color: #aaa;
}
.banner-detail-label {
  color: #888;
}
.banner-detail-text {
  flex: 1 1 auto;
  font-family: monospace;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.banner-detail-time {
  font-size: 10px;
  color: #777;
}
.banner-detail-failed .banner-detail-text {
  color: #f0a0a0;
}
</style>
