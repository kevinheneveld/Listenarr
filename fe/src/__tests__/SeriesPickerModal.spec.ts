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
import { describe, it, expect, beforeEach, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import SeriesPickerModal from '@/components/domain/audiobook/SeriesPickerModal.vue'

const { mockGetSeriesCandidates, mockSelectSeries } = vi.hoisted(() => ({
  mockGetSeriesCandidates: vi.fn(),
  mockSelectSeries: vi.fn(),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getSeriesCandidates: mockGetSeriesCandidates,
    selectSeries: mockSelectSeries,
  },
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success: vi.fn(), warning: vi.fn(), error: vi.fn() }),
}))

vi.mock('@/utils/logger', () => ({ logger: { error: vi.fn(), warn: vi.fn(), debug: vi.fn() } }))

describe('SeriesPickerModal', () => {
  beforeEach(() => {
    mockGetSeriesCandidates.mockReset()
    mockSelectSeries.mockReset()
  })

  const candidatesResponse = {
    query: 'After',
    bestGuessAsin: 'B015EXHCEE',
    candidates: [
      { asin: 'B015EXHCEE', name: 'A John Matherson Novel', source: 'library', ownedMatchCount: 2 },
      {
        asin: 'B07NYRLYKW',
        name: 'After',
        image: 'after.jpg',
        source: 'audible',
        ownedMatchCount: 0,
      },
    ],
  }

  it('loads candidates, ranks library first, and pre-selects the best guess', async () => {
    mockGetSeriesCandidates.mockResolvedValue(candidatesResponse)

    const wrapper = mount(SeriesPickerModal, {
      props: { visible: true, seriesName: 'After', region: 'us' },
    })
    await flushPromises()

    expect(mockGetSeriesCandidates).toHaveBeenCalledWith('After', 'us')
    expect(wrapper.text()).toContain('A John Matherson Novel')
    expect(wrapper.text()).toContain('From 2 of your books')

    // Best guess is pre-selected.
    const checked = wrapper.find('input[type="radio"]:checked').element as HTMLInputElement
    expect(checked.value).toBe('B015EXHCEE')
  })

  it('persists the chosen series and emits the resolved catalog', async () => {
    mockGetSeriesCandidates.mockResolvedValue(candidatesResponse)
    const catalog = {
      series: { asin: 'B015EXHCEE', name: 'A John Matherson Novel' },
      books: [],
      totalBooks: 5,
    }
    mockSelectSeries.mockResolvedValue(catalog)

    const wrapper = mount(SeriesPickerModal, {
      props: { visible: true, seriesName: 'After', region: 'us' },
    })
    await flushPromises()

    // The footer "Use this series" button confirms the selected candidate (the modal body now
    // also has a "Use this ASIN" primary button for the manual paste path).
    await wrapper.find('.modal-footer .btn-primary').trigger('click')
    await flushPromises()

    expect(mockSelectSeries).toHaveBeenCalledWith('After', 'B015EXHCEE', 'us')
    expect(wrapper.emitted('selected')?.[0]?.[0]).toEqual(catalog)
  })

  it('shows a message when no candidates are found', async () => {
    mockGetSeriesCandidates.mockResolvedValue({ query: 'After', candidates: [] })

    const wrapper = mount(SeriesPickerModal, {
      props: { visible: true, seriesName: 'After', region: 'us' },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('No candidate series found')
    expect(mockSelectSeries).not.toHaveBeenCalled()
  })

  it('resolves a manually pasted series ASIN when no candidates match', async () => {
    mockGetSeriesCandidates.mockResolvedValue({ query: 'A Smugglers Tale', candidates: [] })
    const catalog = {
      series: { asin: 'B07YCKVJJT', name: "Smuggler's Tales" },
      books: [],
      totalBooks: 3,
    }
    mockSelectSeries.mockResolvedValue(catalog)

    const wrapper = mount(SeriesPickerModal, {
      props: { visible: true, seriesName: 'A Smugglers Tale', region: 'us' },
    })
    await flushPromises()

    await wrapper.find('#series-manual-asin').setValue('B07YCKVJJT')
    await wrapper.find('.picker-manual-row .btn-primary').trigger('click')
    await flushPromises()

    expect(mockSelectSeries).toHaveBeenCalledWith('A Smugglers Tale', 'B07YCKVJJT', 'us')
    expect(wrapper.emitted('selected')?.[0]?.[0]).toEqual(catalog)
  })

  it('re-searches Audible by a typed name', async () => {
    mockGetSeriesCandidates.mockResolvedValue({ query: 'A Smugglers Tale', candidates: [] })

    const wrapper = mount(SeriesPickerModal, {
      props: { visible: true, seriesName: 'A Smugglers Tale', region: 'us' },
    })
    await flushPromises()
    expect(mockGetSeriesCandidates).toHaveBeenCalledWith('A Smugglers Tale', 'us')

    await wrapper.find('.picker-search .picker-input').setValue("Smuggler's Tales")
    await wrapper.find('.picker-search .btn').trigger('click')
    await flushPromises()

    expect(mockGetSeriesCandidates).toHaveBeenLastCalledWith("Smuggler's Tales", 'us')
  })
})
