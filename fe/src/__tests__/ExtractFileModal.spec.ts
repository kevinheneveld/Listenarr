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
import ExtractFileModal from '@/components/domain/organize/ExtractFileModal.vue'
import { apiService } from '@/services/api'

const EMBEDDED_TAGS = {
  fileId: 71,
  audiobookId: 7,
  currentPath: '/library/H.G. Wells/The First Men in the Moon/Eye of the World.m4b',
  title: 'The Eye of the World: Book One of The Wheel of Time',
  author: 'Robert Jordan',
  narrator: 'Rosamund Pike',
  year: 2021,
}

const AUDIBLE_CANDIDATE = {
  asin: 'B002UZJBA8',
  title: 'The Eye of the World',
  authors: [{ name: 'Robert Jordan' }],
  narrators: [{ name: 'Rosamund Pike' }],
  imageUrl: 'https://example.com/cover.jpg',
  releaseDate: '2021-06-01',
}

const FULL_AUDIBLE_METADATA = {
  title: 'The Eye of the World',
  authors: ['Robert Jordan'],
  narrators: ['Rosamund Pike'],
  publishYear: '2021',
  asin: 'B002UZJBA8',
  imageUrl: 'https://example.com/cover.jpg',
}

describe('ExtractFileModal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(apiService.getFileEmbeddedMetadata).mockResolvedValue(EMBEDDED_TAGS)
    vi.mocked(apiService.searchAudibleByTitleAndAuthor).mockResolvedValue({
      totalResults: 1,
      results: [AUDIBLE_CANDIDATE],
    })
    vi.mocked(apiService.getAudibleMetadata).mockResolvedValue(FULL_AUDIBLE_METADATA)
  })

  it('seeds title/author from embedded tags and auto-runs an Audible search', async () => {
    const wrapper = mount(ExtractFileModal, {
      props: { visible: true, audiobookId: 7, fileId: 71 },
    })
    await flushPromises()
    await flushPromises()

    expect(apiService.getFileEmbeddedMetadata).toHaveBeenCalledWith(7, 71)
    // Subtitle is stripped from the embedded title for the search seed
    expect(apiService.searchAudibleByTitleAndAuthor).toHaveBeenCalledWith(
      'The Eye of the World',
      'Robert Jordan',
    )
    expect(wrapper.text()).toContain('The Eye of the World')
    expect(wrapper.text()).toContain('Robert Jordan')
  })

  it('on candidate pick, fetches full metadata and shows the side-by-side confirm view', async () => {
    const wrapper = mount(ExtractFileModal, {
      props: { visible: true, audiobookId: 7, fileId: 71 },
    })
    await flushPromises()
    await flushPromises()

    await wrapper.find('.extract-candidate').trigger('click')
    await flushPromises()

    expect(apiService.getAudibleMetadata).toHaveBeenCalledWith('B002UZJBA8')
    expect(wrapper.text()).toContain("File's embedded tags")
    expect(wrapper.text()).toContain('Destination (Audible)')
    expect(wrapper.text()).toContain('ASIN')
    expect(wrapper.find('.btn.btn-primary').text()).toContain('Move file')
  })

  it('on Move file, posts the chosen metadata with strategy none and emits done on success', async () => {
    vi.mocked(apiService.extractFileToNewAudiobook).mockResolvedValue({
      success: true,
      appliedStrategy: 'none',
      destinationAudiobookId: 99,
      destinationAudiobookTitle: 'The Eye of the World',
      newFilePath: '/library/Robert Jordan/The Eye of the World/The Eye of the World.m4b',
      sourceAudiobookId: 7,
      sourceAudiobookEmpty: false,
    })

    const wrapper = mount(ExtractFileModal, {
      props: { visible: true, audiobookId: 7, fileId: 71 },
    })
    await flushPromises()
    await flushPromises()
    await wrapper.find('.extract-candidate').trigger('click')
    await flushPromises()

    await wrapper.find('.btn.btn-primary').trigger('click')
    await flushPromises()

    expect(apiService.extractFileToNewAudiobook).toHaveBeenCalledWith(
      7,
      71,
      expect.objectContaining({
        metadata: expect.objectContaining({ asin: 'B002UZJBA8' }),
        duplicateStrategy: 'none',
      }),
    )
    expect(wrapper.emitted('done')).toBeTruthy()
  })

  it('when backend reports a duplicate conflict, shows the strategy chooser with the recommended option', async () => {
    vi.mocked(apiService.extractFileToNewAudiobook).mockResolvedValueOnce({
      success: false,
      appliedStrategy: 'none',
      error: 'An audiobook with this ASIN already exists.',
      sourceAudiobookId: 7,
      sourceAudiobookEmpty: false,
      conflict: {
        existingAudiobookId: 42,
        existingTitle: 'The Eye of the World',
        existingFileCount: 0,
        recommendedStrategy: 'merge',
        recommendationReason: 'The existing audiobook has no files yet.',
      },
    })

    const wrapper = mount(ExtractFileModal, {
      props: { visible: true, audiobookId: 7, fileId: 71 },
    })
    await flushPromises()
    await flushPromises()
    await wrapper.find('.extract-candidate').trigger('click')
    await flushPromises()

    await wrapper.find('.btn.btn-primary').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('already in your library')
    const options = wrapper.findAll('.extract-conflict-option')
    expect(options).toHaveLength(2)
    expect(options[0].classes()).toContain('recommended')
    expect(wrapper.emitted('done')).toBeFalsy()
  })

  it('resolving the conflict by clicking Merge re-issues the request with strategy=merge', async () => {
    vi.mocked(apiService.extractFileToNewAudiobook)
      .mockResolvedValueOnce({
        success: false,
        appliedStrategy: 'none',
        sourceAudiobookId: 7,
        sourceAudiobookEmpty: false,
        conflict: {
          existingAudiobookId: 42,
          existingFileCount: 0,
          recommendedStrategy: 'merge',
        },
      })
      .mockResolvedValueOnce({
        success: true,
        appliedStrategy: 'merge',
        destinationAudiobookId: 42,
        sourceAudiobookId: 7,
        sourceAudiobookEmpty: true,
      })

    const wrapper = mount(ExtractFileModal, {
      props: { visible: true, audiobookId: 7, fileId: 71 },
    })
    await flushPromises()
    await flushPromises()
    await wrapper.find('.extract-candidate').trigger('click')
    await flushPromises()
    await wrapper.find('.btn.btn-primary').trigger('click')
    await flushPromises()

    // Click the first conflict option (recommended Merge)
    await wrapper.findAll('.extract-conflict-option')[0].trigger('click')
    await flushPromises()

    expect(apiService.extractFileToNewAudiobook).toHaveBeenCalledTimes(2)
    expect(apiService.extractFileToNewAudiobook).toHaveBeenLastCalledWith(
      7,
      71,
      expect.objectContaining({ duplicateStrategy: 'merge' }),
    )
    expect(wrapper.emitted('done')).toBeTruthy()
  })
})
