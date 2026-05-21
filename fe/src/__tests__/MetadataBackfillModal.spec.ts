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
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import MetadataBackfillModal from '@/components/domain/audiobook/MetadataBackfillModal.vue'
import { apiService } from '@/services/api'

const BOOK_WITHOUT_ASIN = {
  id: 7,
  title: 'The Eye of the World',
  authors: ['Robert Jordan'],
  narrators: [] as string[],
  files: [{ id: 71, path: '/library/Robert Jordan/The Eye of the World/eye.m4b' }],
}

const KATE_KRAMER_CANDIDATE = {
  asin: 'B0036NHZ10',
  title: 'The Eye of the World',
  authors: [{ name: 'Robert Jordan' }],
  narrators: [{ name: 'Kate Reading' }, { name: 'Michael Kramer' }],
  imageUrl: '',
  releaseDate: '2006-01-01',
}

const ROSAMUND_CANDIDATE = {
  asin: 'B09JT4PH62',
  title: 'The Eye of the World',
  authors: [{ name: 'Robert Jordan' }],
  narrators: [{ name: 'Rosamund Pike' }],
  imageUrl: '',
  releaseDate: '2021-06-01',
}

// The modal wraps its UI in <Teleport to="body">, which routes content outside
// any test wrapper. Stub it to a passthrough so `wrapper.find(...)` works.
const mountOptions = {
  global: {
    stubs: {
      Teleport: { template: '<div><slot /></div>' },
      // FilePreviewModal is rendered (visible=false) but pulls in audio APIs we
      // don't need in these tests — stub it to a no-op.
      FilePreviewModal: { template: '<div />' },
    },
  },
}

describe('MetadataBackfillModal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(apiService.searchAudibleByTitleAndAuthor).mockResolvedValue({
      totalResults: 2,
      results: [KATE_KRAMER_CANDIDATE, ROSAMUND_CANDIDATE],
    })
    vi.mocked(apiService.getFileEmbeddedMetadata).mockResolvedValue({
      fileId: 71,
      audiobookId: 7,
    })
  })

  it('sorts candidates whose narrator matches the embedded file tags to the top', async () => {
    // The DB narrators field is empty, so the file's embedded composer tag is the
    // only source. This isolates the embedded-tag branch of the ranking logic.
    vi.mocked(apiService.getFileEmbeddedMetadata).mockResolvedValue({
      fileId: 71,
      audiobookId: 7,
      narrator: 'Rosamund Pike',
    })

    const wrapper = mount(MetadataBackfillModal, {
      ...mountOptions,
      props: { visible: false, audiobook: BOOK_WITHOUT_ASIN as unknown as never },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()
    await flushPromises()

    const rows = wrapper.findAll('.candidate-item')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('Rosamund Pike')
    expect(rows[0].classes()).toContain('candidate-item--narrator-match')
    expect(rows[0].text()).toContain('Narrator matches')
    expect(rows[1].text()).toContain('Kate Reading')
    expect(rows[1].classes()).not.toContain('candidate-item--narrator-match')
  })

  it('uses the audiobook DB narrators field as a ranking source when embedded tag is missing', async () => {
    // No `narrator` on the embedded payload — only the audiobook's existing
    // `narrators` DB field should drive the sort. This exercises the second
    // narrator source independently.
    const book = { ...BOOK_WITHOUT_ASIN, narrators: ['Rosamund Pike'] }

    const wrapper = mount(MetadataBackfillModal, {
      ...mountOptions,
      props: { visible: false, audiobook: book as unknown as never },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()
    await flushPromises()

    const rows = wrapper.findAll('.candidate-item')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('Rosamund Pike')
    expect(rows[0].classes()).toContain('candidate-item--narrator-match')
  })

  it('preserves search order when no narrator info is available from either source', async () => {
    // Empty DB narrators AND no embedded narrator — ranking is a no-op, list
    // stays in search-result order and no candidate is badged as matching.
    const wrapper = mount(MetadataBackfillModal, {
      ...mountOptions,
      props: { visible: false, audiobook: BOOK_WITHOUT_ASIN as unknown as never },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()
    await flushPromises()

    const rows = wrapper.findAll('.candidate-item')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('Kate Reading')
    expect(rows[1].text()).toContain('Rosamund Pike')
    expect(rows[0].classes()).not.toContain('candidate-item--narrator-match')
    expect(rows[1].classes()).not.toContain('candidate-item--narrator-match')
  })

  it('renders the narrator hint above the candidate picker when narrator info is known', async () => {
    vi.mocked(apiService.getFileEmbeddedMetadata).mockResolvedValue({
      fileId: 71,
      audiobookId: 7,
      narrator: 'Rosamund Pike',
    })

    const wrapper = mount(MetadataBackfillModal, {
      ...mountOptions,
      props: { visible: false, audiobook: BOOK_WITHOUT_ASIN as unknown as never },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()
    await flushPromises()

    const hint = wrapper.find('.narrator-hint')
    expect(hint.exists()).toBe(true)
    expect(hint.text()).toContain('Rosamund Pike')
    expect(hint.text()).toContain('from file tags')
  })
})
