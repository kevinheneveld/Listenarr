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

// Pure helpers for the Split Collection modal: how a group (or a
// destination's existing copy) is described, and which destinations a run
// replaces.

import { formatBytes } from '@/utils/dashboardAggregates'

export interface SplitGroupStats {
  fileCount: number
  totalBytes: number
  totalDurationSeconds: number
  bitrateKbps?: number | null
  codec?: string | null
}

export type SplitVerdict =
  | 'empty'
  | 'identical'
  | 'better'
  | 'worse'
  | 'similar'
  | 'different'
  | 'unknown'

export interface SplitComparison {
  audiobookId: number
  targetId: number
  targetTitle?: string | null
  expectedRuntimeSeconds?: number | null
  incoming: SplitGroupStats
  existing: SplitGroupStats
  existingFileIds: number[]
  verdict: SplitVerdict
  reason: string
}

/** "407 files · 33.7 h · 128 kbps · 1.8 GB" — parts without data are left out. */
export function describeStats(stats: SplitGroupStats | null | undefined): string {
  if (!stats) return ''
  const parts = [`${stats.fileCount} file${stats.fileCount === 1 ? '' : 's'}`]
  if (stats.totalDurationSeconds > 0) {
    parts.push(`${(stats.totalDurationSeconds / 3600).toFixed(1)} h`)
  }
  if (stats.bitrateKbps && stats.bitrateKbps > 0) parts.push(`${stats.bitrateKbps} kbps`)
  if (stats.totalBytes > 0) parts.push(formatBytes(stats.totalBytes))
  return parts.join(' · ')
}

/** First and last file of a group ("…-001.mp3 … …-407.mp3"), or the only one. */
export function describeRange(fileNames: string[]): string {
  if (fileNames.length === 0) return ''
  const first = fileNames[0]!
  const last = fileNames[fileNames.length - 1]!
  return fileNames.length === 1 ? first : `${first} … ${last}`
}

export function verdictHeadline(verdict: SplitVerdict): string {
  switch (verdict) {
    case 'better':
      return 'This copy is better quality.'
    case 'worse':
      return 'The existing copy is better.'
    case 'similar':
      return 'About the same quality.'
    case 'identical':
      return 'Same files as the existing copy.'
    case 'different':
      return 'Probably not the same audio.'
    case 'unknown':
      return 'Quality cannot be compared.'
    default:
      return ''
  }
}

/**
 * The destinations whose existing files a run replaces, each once. Several
 * groups may land on one record (a book probed as separate parts); its old
 * files must be listed BEFORE any group moves in and deleted once, after all
 * of them have — otherwise the second group's "existing files" would be the
 * first group's.
 */
export function replaceTargetsOf(
  rows: ReadonlyArray<{ action: string; targetId: number | null }>,
): number[] {
  const seen = new Set<number>()
  for (const row of rows) {
    if (row.action === 'replace' && row.targetId != null) seen.add(row.targetId)
  }
  return [...seen]
}
