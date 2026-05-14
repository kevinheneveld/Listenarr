/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { apiService } from '@/services/api'
import { errorTracking } from '@/services/errorTracking'
import type { ActivityGranularity, LibraryStats } from '@/types'

// Default activity-window size per granularity.
const DEFAULT_PERIODS: Record<ActivityGranularity, number> = {
  Day: 30,
  Week: 12,
  Month: 12,
}

export const useDashboardStore = defineStore('dashboard', () => {
  const stats = ref<LibraryStats | null>(null)
  const loading = ref(false)
  const error = ref<string | null>(null)
  const activityGranularity = ref<ActivityGranularity>('Month')
  const activityPeriods = ref<number>(DEFAULT_PERIODS.Month)
  let inFlightFetch: Promise<void> | null = null

  async function fetchStats() {
    if (inFlightFetch) {
      return inFlightFetch
    }

    loading.value = true
    error.value = null
    inFlightFetch = (async () => {
      try {
        stats.value = await apiService.getDashboardStats(
          activityGranularity.value,
          activityPeriods.value,
        )
      } catch (err) {
        error.value = err instanceof Error ? err.message : 'Failed to load dashboard stats'
        errorTracking.captureException(err as Error, {
          component: 'DashboardStore',
          operation: 'fetchStats',
        })
      } finally {
        loading.value = false
        inFlightFetch = null
      }
    })()

    return inFlightFetch
  }

  // Change the activity granularity, reset its window to a sensible default,
  // and refetch.
  async function setActivityGranularity(granularity: ActivityGranularity) {
    if (granularity === activityGranularity.value) return
    activityGranularity.value = granularity
    activityPeriods.value = DEFAULT_PERIODS[granularity]
    await fetchStats()
  }

  return {
    stats,
    loading,
    error,
    activityGranularity,
    activityPeriods,
    fetchStats,
    setActivityGranularity,
  }
})
