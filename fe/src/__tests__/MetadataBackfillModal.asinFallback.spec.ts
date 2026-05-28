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
import { mount, flushPromises } from '@vue/test-utils'
import { vi, describe, it, expect, beforeEach } from 'vitest'

const apiMocks = vi.hoisted(() => ({
  getAudibleMetadata: vi.fn(),
  searchAudibleByTitleAndAuthor: vi.fn(),
  updateAudiobook: vi.fn(),
  // kevin/live's full modal kicks off an embedded-tag fetch in parallel
  // with the candidate search. Stub it to resolve cleanly so the modal's
  // open-time `void loadEmbeddedMetadata()` doesn't throw against an
  // unmocked apiService.
  getFileEmbeddedMetadata: vi.fn().mockResolvedValue({}),
}))

vi.mock('@/services/api', () => ({
  apiService: apiMocks,
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
    info: vi.fn(),
    warning: vi.fn(),
  }),
}))

import MetadataBackfillModal from '@/components/domain/audiobook/MetadataBackfillModal.vue'

const bookWithAsin = {
  id: 7,
  title: 'A Title',
  authors: ['An Author'],
  narrators: [],
  asin: 'B000UNUSABLE',
  files: [],
}

const usableFreshMetadata = {
  metadata: {
    asin: 'B0CSV7NJMB',
    title: 'A Title',
    authors: [{ name: 'An Author' }],
    narrators: [{ name: 'A Narrator' }],
  },
  source: 'Audible',
}

describe('MetadataBackfillModal — ASIN fallback (issue #10)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('falls back to title/author search when the saved ASIN returns no usable metadata', async () => {
    // Audible occasionally returns an empty payload or just an ASIN echo for
    // titles that are regionally restricted or delisted. Without the fallback
    // the user would see an empty Fresh column and a useless "Apply 0 changes".
    apiMocks.getAudibleMetadata.mockResolvedValue({ metadata: { asin: 'B000UNUSABLE' } })
    apiMocks.searchAudibleByTitleAndAuthor.mockResolvedValue({
      results: [
        { asin: 'B0CSV7NJMB', title: 'A Title', authors: [{ name: 'An Author' }] },
      ],
    })

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: bookWithAsin },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledWith('B000UNUSABLE', 'us')
    expect(apiMocks.searchAudibleByTitleAndAuthor).toHaveBeenCalled()
    // The modal surfaces the fallback as a visible notice so the user knows
    // why they're being shown candidates instead of a direct comparison.
    // The modal teleports to body, so the rendered text lives outside `wrapper`.
    expect(document.body.textContent).toContain('Audible returned no metadata for ASIN')

    wrapper.unmount()
  })

  it('uses the ASIN result directly when it has usable metadata', async () => {
    apiMocks.getAudibleMetadata.mockResolvedValue(usableFreshMetadata)

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: bookWithAsin },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledWith('B000UNUSABLE', 'us')
    expect(apiMocks.searchAudibleByTitleAndAuthor).not.toHaveBeenCalled()
    expect(document.body.textContent).not.toContain('Audible returned no metadata for ASIN')

    wrapper.unmount()
  })
})
