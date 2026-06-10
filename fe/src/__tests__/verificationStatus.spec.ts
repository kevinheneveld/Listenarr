/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { describe, it, expect } from 'vitest'
import {
  verificationLabel,
  verificationClass,
  needsReview,
  parseVerificationDetail,
} from '@/utils/verificationStatus'

describe('verificationStatus utils', () => {
  it('maps every status to a label and class', () => {
    expect(verificationLabel('agentVerified')).toBe('Audio verified')
    expect(verificationLabel('agentFlagged')).toBe('Needs review')
    expect(verificationLabel('manuallyVerified')).toBe('Verified (manual)')
    expect(verificationLabel('rejected')).toBe('Rejected')
    expect(verificationLabel('unverified')).toBe('Unverified')
    expect(verificationLabel(undefined)).toBe('Unverified')

    expect(verificationClass('agentVerified')).toBe('verification-ok')
    expect(verificationClass('manuallyVerified')).toBe('verification-ok')
    expect(verificationClass('agentFlagged')).toBe('verification-flagged')
    expect(verificationClass('rejected')).toBe('verification-rejected')
    expect(verificationClass(undefined)).toBe('verification-none')
  })

  it('needsReview is true only for agent-flagged books', () => {
    expect(needsReview({ verificationStatus: 'agentFlagged' })).toBe(true)
    expect(needsReview({ verificationStatus: 'agentVerified' })).toBe(false)
    expect(needsReview({ verificationStatus: 'rejected' })).toBe(false)
    expect(needsReview({ verificationStatus: undefined })).toBe(false)
  })

  it('parses the persisted detail JSON and tolerates garbage', () => {
    const detail = parseVerificationDetail({
      verificationDetailJson: JSON.stringify({
        outcome: 'mismatch',
        confidence: 0.9,
        method: 'deterministic:whisper-base.en',
        titleMatch: { score: 0.1, matchedText: null },
        authorMatch: { score: 0.05, matchedText: 'someone else' },
      }),
    })
    expect(detail?.outcome).toBe('mismatch')
    expect(detail?.authorMatch?.score).toBe(0.05)

    expect(parseVerificationDetail({ verificationDetailJson: null })).toBeNull()
    expect(parseVerificationDetail({ verificationDetailJson: 'not json{' })).toBeNull()
  })
})
