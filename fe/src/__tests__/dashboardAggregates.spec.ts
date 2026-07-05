import { describe, it, expect } from 'vitest'
import {
  libraryGlance,
  seriesHealth,
  bucketSeriesRows,
  verificationCounts,
  formatBytes,
} from '@/utils/dashboardAggregates'
import type { Audiobook } from '@/types'

function book(partial: Partial<Audiobook>): Audiobook {
  return { id: 0, title: 'T', ...partial } as Audiobook
}

describe('libraryGlance', () => {
  it('splits owned / missing / idle and sums bytes over owned only', () => {
    const glance = libraryGlance([
      book({ id: 1, fileCount: 3, fileSize: 100, monitored: true }),
      book({ id: 2, fileCount: 0, monitored: true, fileSize: 999 }),
      book({ id: 3, fileCount: 0, monitored: false }),
    ])
    expect(glance).toEqual({ total: 3, owned: 1, missing: 1, idle: 1, totalBytes: 100 })
  })
})

describe('seriesHealth', () => {
  it('aggregates by membership, marks complete series, sorts by missing', () => {
    const rows = seriesHealth([
      book({ id: 1, fileCount: 1, seriesMemberships: [{ seriesName: 'Done' }] }),
      book({ id: 2, fileCount: 1, seriesMemberships: [{ seriesName: 'Gappy' }] }),
      book({ id: 3, fileCount: 0, monitored: true, seriesMemberships: [{ seriesName: 'Gappy' }] }),
      book({ id: 4, fileCount: 0, monitored: true, series: 'Legacy Field' }),
    ])
    expect(rows[0]).toMatchObject({ name: 'Gappy', owned: 1, missing: 1, complete: false })
    expect(rows.find((r) => r.name === 'Done')).toMatchObject({ complete: true, total: 1 })
    expect(rows.find((r) => r.name === 'Legacy Field')).toMatchObject({ missing: 1 })
  })

  it('counts a multi-series book toward each series', () => {
    const rows = seriesHealth([
      book({
        id: 1,
        fileCount: 1,
        seriesMemberships: [{ seriesName: 'A' }, { seriesName: 'B' }],
      }),
    ])
    expect(rows).toHaveLength(2)
  })
})

describe('verificationCounts', () => {
  it('buckets statuses; no-spoken-credits is not flagged', () => {
    const counts = verificationCounts([
      book({ id: 1, verificationStatus: 'agentVerified' }),
      book({ id: 2, verificationStatus: 'agentFlagged' }),
      book({ id: 3, verificationStatus: 'agentUnverifiable' }),
      book({ id: 4 }),
    ])
    expect(counts).toEqual({ verified: 1, flagged: 1, unverifiable: 1, unverified: 1 })
  })
})

describe('formatBytes', () => {
  it('formats human-readable sizes', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(1536)).toBe('1.5 KB')
    expect(formatBytes(3.2 * 1024 ** 4)).toBe('3.2 TB')
  })
})

describe('bucketSeriesRows', () => {
  const row = (over: Partial<Parameters<typeof bucketSeriesRows>[0][number]>) => ({
    name: 'S',
    owned: 1,
    missing: 0,
    total: 1,
    complete: true,
    ...over,
  })

  it('splits single-book series out of the complete headline', () => {
    const buckets = bucketSeriesRows([
      row({ total: 1, complete: true }), // tracked-only single
      row({ total: 3, owned: 3, complete: true }), // real complete
      row({ total: 3, owned: 1, missing: 2, complete: false }), // gaps
    ])
    expect(buckets).toEqual({ complete: 1, singleBook: 1, gaps: 1 })
  })

  it('uses catalog total over tracked total when known', () => {
    const buckets = bucketSeriesRows([
      // one tracked book, and the catalog agrees the series has 1 book: single
      row({ total: 1, catalogTotal: 1, complete: true }),
      // one tracked book, catalog says 4 and endpoint marked incomplete: gaps
      row({ total: 4, owned: 1, missing: 3, catalogTotal: 4, complete: false }),
      // catalog-confirmed full trilogy: complete
      row({ total: 3, owned: 3, catalogTotal: 3, complete: true }),
    ])
    expect(buckets).toEqual({ complete: 1, singleBook: 1, gaps: 1 })
  })
})
