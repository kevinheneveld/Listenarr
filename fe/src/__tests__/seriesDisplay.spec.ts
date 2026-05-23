import { describe, it, expect } from 'vitest'
import {
  getPrimarySeries,
  formatSeriesDisplay,
  formatAllSeriesTooltip,
  getSeriesSortKey,
  parseSeriesPositionForSort,
} from '@/utils/seriesDisplay'

describe('seriesDisplay', () => {
  describe('getPrimarySeries', () => {
    it('returns null when no series info', () => {
      expect(getPrimarySeries({})).toBeNull()
      expect(getPrimarySeries(null)).toBeNull()
    })

    it('prefers seriesMemberships over legacy series field', () => {
      const r = getPrimarySeries({
        series: 'Legacy',
        seriesNumber: '99',
        seriesMemberships: [{ seriesName: 'Real', seriesNumber: '1' }],
      })
      expect(r?.name).toBe('Real')
      expect(r?.number).toBe('1')
    })

    it('picks isPrimary entry over sortOrder', () => {
      const r = getPrimarySeries({
        seriesMemberships: [
          { seriesName: 'Spinoff', sortOrder: 0 },
          { seriesName: 'Main', isPrimary: true, sortOrder: 5 },
        ],
      })
      expect(r?.name).toBe('Main')
    })

    it('falls back to sortOrder when no isPrimary flag', () => {
      const r = getPrimarySeries({
        seriesMemberships: [
          { seriesName: 'B', sortOrder: 2 },
          { seriesName: 'A', sortOrder: 1 },
        ],
      })
      expect(r?.name).toBe('A')
    })

    it('reports extraCount for multi-series books', () => {
      const r = getPrimarySeries({
        seriesMemberships: [
          { seriesName: 'Main', isPrimary: true },
          { seriesName: 'Spinoff' },
          { seriesName: 'Anthology' },
        ],
      })
      expect(r?.extraCount).toBe(2)
    })

    it('falls back to legacy series fields', () => {
      const r = getPrimarySeries({ series: 'Wheel of Time', seriesNumber: '3' })
      expect(r).toEqual({
        name: 'Wheel of Time',
        number: '3',
        extraCount: 0,
        allMemberships: [],
      })
    })

    it('ignores empty membership names', () => {
      const r = getPrimarySeries({
        seriesMemberships: [{ seriesName: '   ' }, { seriesName: 'Real' }],
      })
      expect(r?.name).toBe('Real')
      expect(r?.extraCount).toBe(0)
    })
  })

  describe('formatSeriesDisplay', () => {
    it('renders name #number when both present', () => {
      expect(
        formatSeriesDisplay({ name: 'Foundation', number: '2', extraCount: 0, allMemberships: [] }),
      ).toBe('Foundation #2')
    })

    it('renders only name when number missing', () => {
      expect(
        formatSeriesDisplay({ name: 'Foundation', extraCount: 0, allMemberships: [] }),
      ).toBe('Foundation')
    })

    it('supports omnibus range numbers verbatim', () => {
      expect(
        formatSeriesDisplay({
          name: 'Mistborn',
          number: '1-3',
          extraCount: 0,
          allMemberships: [],
        }),
      ).toBe('Mistborn #1-3')
    })

    it('returns empty string for null', () => {
      expect(formatSeriesDisplay(null)).toBe('')
    })
  })

  describe('formatAllSeriesTooltip', () => {
    it('returns empty string when only one membership', () => {
      expect(
        formatAllSeriesTooltip({
          name: 'Foo',
          extraCount: 0,
          allMemberships: [{ seriesName: 'Foo' }],
        }),
      ).toBe('')
    })

    it('joins all memberships when multiple', () => {
      const tip = formatAllSeriesTooltip({
        name: 'Main',
        extraCount: 1,
        allMemberships: [
          { seriesName: 'Main', seriesNumber: '1' },
          { seriesName: 'Spinoff' },
        ],
      })
      expect(tip).toBe('Main #1\nSpinoff')
    })
  })

  describe('parseSeriesPositionForSort', () => {
    it('parses integers', () => {
      expect(parseSeriesPositionForSort('3')).toBe(3)
    })
    it('parses decimals (novellas)', () => {
      expect(parseSeriesPositionForSort('1.5')).toBe(1.5)
    })
    it('parses leading number from ranges (omnibus)', () => {
      expect(parseSeriesPositionForSort('1-3')).toBe(1)
    })
    it('parses embedded numbers', () => {
      expect(parseSeriesPositionForSort('Book 2')).toBe(2)
    })
    it('returns null for non-numeric', () => {
      expect(parseSeriesPositionForSort('Prequel')).toBeNull()
      expect(parseSeriesPositionForSort(undefined)).toBeNull()
    })
  })

  describe('getSeriesSortKey', () => {
    it('orders by series name first, then position numerically', () => {
      const a = getSeriesSortKey({ series: 'Foundation', seriesNumber: '2' })
      const b = getSeriesSortKey({ series: 'Foundation', seriesNumber: '10' })
      // 2 must sort before 10 within the same series
      expect(a < b).toBe(true)
    })

    it('places 1.5 between 1 and 2', () => {
      const one = getSeriesSortKey({ series: 'X', seriesNumber: '1' })
      const half = getSeriesSortKey({ series: 'X', seriesNumber: '1.5' })
      const two = getSeriesSortKey({ series: 'X', seriesNumber: '2' })
      expect(one < half).toBe(true)
      expect(half < two).toBe(true)
    })

    it('places standalones after all series ascending', () => {
      const standalone = getSeriesSortKey({})
      const named = getSeriesSortKey({ series: 'ZZZ Last Series', seriesNumber: '99' })
      expect(named < standalone).toBe(true)
    })

    it('places unnumbered series entries after numbered ones within the same series', () => {
      const numbered = getSeriesSortKey({ series: 'Foo', seriesNumber: '5' })
      const unnumbered = getSeriesSortKey({ series: 'Foo' })
      expect(numbered < unnumbered).toBe(true)
    })
  })
})
