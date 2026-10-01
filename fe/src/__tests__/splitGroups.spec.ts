import { describe, it, expect } from 'vitest'
import {
  describeStats,
  describeRange,
  verdictHeadline,
  replaceTargetsOf,
} from '@/utils/splitGroups'

describe('splitGroups', () => {
  it('describes a group by count, length, bitrate and size', () => {
    expect(
      describeStats({
        fileCount: 407,
        totalBytes: 1.8 * 1024 ** 3,
        totalDurationSeconds: 33.7 * 3600,
        bitrateKbps: 128,
        codec: 'mp3',
      }),
    ).toBe('407 files · 33.7 h · 128 kbps · 1.8 GB')
  })

  it('leaves out what is not on record', () => {
    expect(
      describeStats({ fileCount: 1, totalBytes: 0, totalDurationSeconds: 0, bitrateKbps: null }),
    ).toBe('1 file')
    expect(describeStats(null)).toBe('')
  })

  it('shows the first and last file of a group', () => {
    expect(describeRange(['a-001.mp3', 'a-002.mp3', 'a-407.mp3'])).toBe('a-001.mp3 … a-407.mp3')
    expect(describeRange(['only.m4b'])).toBe('only.m4b')
    expect(describeRange([])).toBe('')
  })

  it('has a headline for every verdict that compares two copies', () => {
    for (const verdict of [
      'better',
      'worse',
      'similar',
      'identical',
      'different',
      'unknown',
    ] as const) {
      expect(verdictHeadline(verdict)).not.toBe('')
    }
    expect(verdictHeadline('empty')).toBe('')
  })

  it('lists each replaced destination once, ignoring plain moves and deletes', () => {
    expect(
      replaceTargetsOf([
        { action: 'replace', targetId: 4670 },
        { action: 'replace', targetId: 4670 }, // a second part of the same book
        { action: 'move', targetId: 4673 },
        { action: 'delete', targetId: 4675 },
        { action: 'replace', targetId: null },
        { action: 'replace', targetId: 4677 },
      ]),
    ).toEqual([4670, 4677])
  })
})
