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

  it('unwraps the {metadata, source, sourceUrl} envelope from getAudibleMetadata and renders the inner fields', async () => {
    // Regression: the controller returns an envelope, not raw AudibleBookMetadata. The
    // inner shape uses AudibleAuthor/AudibleNarrator objects (not string[]). Validate
    // that the modal merges the envelope + candidate into a populated destination view.
    vi.mocked(apiService.getAudibleMetadata).mockResolvedValueOnce({
      metadata: {
        asin: 'B002UZJBA8',
        title: 'The Eye of the World',
        authors: [{ name: 'Robert Jordan' }],
        narrators: [{ name: 'Rosamund Pike' }],
        releaseDate: '2021-06-01T07:00:00Z',
        imageUrl: 'https://example.com/inner-cover.jpg',
        description: 'A fantasy epic.',
        publisher: 'Audible Studios',
        language: 'English',
        lengthMinutes: 1971,
        isbn: '9780765356147',
      },
      source: 'Audible',
      sourceUrl: 'https://api.audible.com',
    } as unknown as never)

    const wrapper = mount(ExtractFileModal, {
      props: { visible: true, audiobookId: 7, fileId: 71 },
    })
    await flushPromises()
    await flushPromises()
    await wrapper.find('.extract-candidate').trigger('click')
    await flushPromises()

    // The destination column should show populated values (not all "—") from the
    // unwrapped envelope's inner metadata.
    const destination = wrapper.findAll('.extract-confirm-col')[1]
    expect(destination.text()).toContain('The Eye of the World')
    expect(destination.text()).toContain('Robert Jordan')
    expect(destination.text()).toContain('Rosamund Pike')
    expect(destination.text()).toContain('2021')
    expect(destination.text()).toContain('B002UZJBA8')
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
        existingAsin: 'B002UZJBA8',
        existingFileCount: 2,
        existingBasePath: '/library/Robert Jordan/The Eye of the World (different folder)',
        proposedDestinationFolder: '/library/Robert Jordan/The Eye of the World',
        recommendedStrategy: 'duplicate',
        recommendationReason: 'The existing audiobook already has files.',
        existingFiles: [
          {
            fileId: 91,
            path: 'Robert Jordan/The Eye of the World/Disc 01.mp3',
            format: 'mp3',
            size: 78_643_200,
            durationSeconds: 4500,
          },
          {
            fileId: 92,
            path: 'Robert Jordan/The Eye of the World/Disc 02.mp3',
            format: 'mp3',
            size: 75_497_472,
            durationSeconds: 4320,
          },
        ],
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

    expect(wrapper.text()).toContain('Another audiobook in your library already uses ASIN')
    // Existing-files summary lets the user compare structure (monolithic m4b vs
    // many chapter mp3s) before picking merge or duplicate.
    expect(wrapper.text()).toContain("What's already in")
    expect(wrapper.text()).toContain('Disc 01.mp3')
    expect(wrapper.text()).toContain('Disc 02.mp3')
    // The proposed-destination path is shown under the Duplicate option so the user
    // can see where a new audiobook would land — distinct from the existing record's
    // current folder.
    expect(wrapper.text()).toContain('/library/Robert Jordan/The Eye of the World')
    const options = wrapper.findAll('.extract-conflict-option')
    expect(options).toHaveLength(2)
    expect(options[1].classes()).toContain('recommended') // duplicate is recommended here
    expect(wrapper.emitted('done')).toBeFalsy()
  })

  it('sorts candidates whose narrator matches the embedded file tags to the top', async () => {
    vi.mocked(apiService.searchAudibleByTitleAndAuthor).mockResolvedValue({
      totalResults: 2,
      results: [
        {
          asin: 'B0036NHZ10',
          title: 'The Eye of the World',
          authors: [{ name: 'Robert Jordan' }],
          narrators: [{ name: 'Kate Reading' }, { name: 'Michael Kramer' }],
          imageUrl: '',
          releaseDate: '2006-01-01',
        },
        {
          asin: 'B09JT4PH62',
          title: 'The Eye of the World',
          authors: [{ name: 'Robert Jordan' }],
          narrators: [{ name: 'Rosamund Pike' }],
          imageUrl: '',
          releaseDate: '2021-06-01',
        },
      ],
    })

    const wrapper = mount(ExtractFileModal, {
      props: { visible: true, audiobookId: 7, fileId: 71 },
    })
    await flushPromises()
    await flushPromises()

    // Embedded narrator is "Rosamund Pike"; the 2021 candidate should now lead even
    // though it came back second from the search.
    const rows = wrapper.findAll('.extract-candidate')
    expect(rows).toHaveLength(2)
    expect(rows[0].text()).toContain('Rosamund Pike')
    expect(rows[0].classes()).toContain('extract-candidate--narrator-match')
    expect(rows[1].text()).toContain('Kate Reading')
    expect(rows[1].classes()).not.toContain('extract-candidate--narrator-match')
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
