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
import type { Audiobook } from '@/types'

/**
 * Pure aggregation helpers for the Library Health dashboard. "Owned" means the
 * record actually has audio on disk (fileCount > 0) — a tracked-but-fileless
 * record is "missing", not owned, mirroring the series-pill semantics on
 * collection pages.
 */

export interface LibraryGlance {
  total: number
  owned: number
  /** Monitored records with no files yet — the automatic-search targets. */
  missing: number
  /** Unmonitored records with no files — tracked but nobody is looking. */
  idle: number
  totalBytes: number
}

export function libraryGlance(items: Audiobook[]): LibraryGlance {
  const glance: LibraryGlance = {
    total: items.length,
    owned: 0,
    missing: 0,
    idle: 0,
    totalBytes: 0,
  }
  for (const b of items) {
    const hasFiles = (b.fileCount ?? 0) > 0
    if (hasFiles) {
      glance.owned++
      glance.totalBytes += b.fileSize ?? 0
    } else if (b.monitored) {
      glance.missing++
    } else {
      glance.idle++
    }
  }
  return glance
}

export interface SeriesHealthRow {
  name: string
  owned: number
  missing: number
  total: number
  complete: boolean
  /** Cached Audible catalog size; null/undefined = tracked-only knowledge. */
  catalogTotal?: number | null
}

export interface SeriesBuckets {
  /** Genuinely finished multi-book series. */
  complete: number
  /** "Series" of one book — trivially complete, split out of the headline. */
  singleBook: number
  gaps: number
}

/**
 * Headline buckets. A series only counts as "complete" when it has more than
 * one book (catalog size where known, tracked size otherwise) — one-book
 * series are honest but uninteresting and get their own bucket.
 */
export function bucketSeriesRows(rows: SeriesHealthRow[]): SeriesBuckets {
  const buckets: SeriesBuckets = { complete: 0, singleBook: 0, gaps: 0 }
  for (const r of rows) {
    if (!r.complete) {
      buckets.gaps++
      continue
    }
    const effectiveTotal = r.catalogTotal ?? r.total
    if (effectiveTotal <= 1) buckets.singleBook++
    else buckets.complete++
  }
  return buckets
}

/**
 * Per-series owned/missing among TRACKED records. A series is "complete" when
 * every tracked book has files. Catalog books never added at all are out of
 * scope here — that lives on the series page ("ready to add").
 * Memberships are preferred over the legacy single `series` field; a book in
 * several series counts toward each.
 */
export function seriesHealth(items: Audiobook[]): SeriesHealthRow[] {
  const bySeries = new Map<string, { owned: number; missing: number }>()

  for (const b of items) {
    const names = new Set<string>()
    if (b.seriesMemberships?.length) {
      for (const m of b.seriesMemberships) {
        const n = m.seriesName?.trim()
        if (n) names.add(n)
      }
    } else if (b.series?.trim()) {
      names.add(b.series.trim())
    }
    if (names.size === 0) continue

    const hasFiles = (b.fileCount ?? 0) > 0
    for (const n of names) {
      const row = bySeries.get(n) ?? { owned: 0, missing: 0 }
      if (hasFiles) row.owned++
      else row.missing++
      bySeries.set(n, row)
    }
  }

  return Array.from(bySeries.entries())
    .map(([name, r]) => ({
      name,
      owned: r.owned,
      missing: r.missing,
      total: r.owned + r.missing,
      complete: r.missing === 0,
    }))
    .sort((a, b) => b.missing - a.missing || b.total - a.total || a.name.localeCompare(b.name))
}

export interface VerificationCounts {
  verified: number
  flagged: number
  unverifiable: number
  unverified: number
}

export function verificationCounts(items: Audiobook[]): VerificationCounts {
  const counts: VerificationCounts = { verified: 0, flagged: 0, unverifiable: 0, unverified: 0 }
  for (const b of items) {
    switch (b.verificationStatus) {
      case 'agentVerified':
      case 'manuallyVerified':
        counts.verified++
        break
      case 'agentFlagged':
      case 'rejected':
        counts.flagged++
        break
      case 'agentUnverifiable':
        counts.unverifiable++
        break
      default:
        counts.unverified++
    }
  }
  return counts
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value >= 100 ? Math.round(value) : value.toFixed(1)} ${units[unit]}`
}
