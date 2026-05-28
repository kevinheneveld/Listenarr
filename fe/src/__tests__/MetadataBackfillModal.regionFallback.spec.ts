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
  // kevin/live's modal opens an embedded-tag fetch in parallel; stub it.
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

const bookWithoutAsin = {
  id: 11,
  title: 'Star Kingdom Omnibus II',
  authors: ['Jay Allan'],
  narrators: [] as string[],
  files: [] as Array<{ id: number; path: string }>,
}

function ukMatch() {
  return {
    asin: '1774241595',
    title: 'Star Kingdom Omnibus II',
    authors: [{ name: 'Jay Allan' }],
    region: 'uk',
  }
}

function usMatch() {
  return {
    asin: 'B0XXXXUSXX',
    title: 'Star Kingdom Omnibus II',
    authors: [{ name: 'Jay Allan' }],
    region: 'us',
  }
}

describe('MetadataBackfillModal — region fallback', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    document.body.innerHTML = ''
  })

  it('uses US results directly when the US store has matches (no fallback fires)', async () => {
    apiMocks.searchAudibleByTitleAndAuthor.mockImplementation((_t, _a, _p, _l, region) =>
      Promise.resolve(
        region === 'us' ? { totalResults: 1, results: [usMatch()] } : { totalResults: 0, results: [] },
      ),
    )

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: bookWithoutAsin },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    // US is the first region in the fallback chain — should be the only call.
    expect(apiMocks.searchAudibleByTitleAndAuthor).toHaveBeenCalledTimes(1)
    expect(apiMocks.searchAudibleByTitleAndAuthor).toHaveBeenCalledWith(
      'Star Kingdom Omnibus II',
      'Jay Allan',
      1,
      25,
      'us',
    )
    // No region-fallback notice when US worked.
    expect(document.body.textContent).not.toContain('US returned no matches')

    wrapper.unmount()
  })

  it('falls back to UK when US returns zero results, stops at first match', async () => {
    apiMocks.searchAudibleByTitleAndAuthor.mockImplementation((_t, _a, _p, _l, region) => {
      if (region === 'us') return Promise.resolve({ totalResults: 0, results: [] })
      if (region === 'uk') return Promise.resolve({ totalResults: 1, results: [ukMatch()] })
      return Promise.resolve({ totalResults: 0, results: [] })
    })

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: bookWithoutAsin },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    // US then UK, then stop — never queries CA / AU.
    const regions = apiMocks.searchAudibleByTitleAndAuthor.mock.calls.map((c) => c[4])
    expect(regions).toEqual(['us', 'uk'])
    expect(document.body.textContent).toContain('US returned no matches')
    expect(document.body.textContent).toContain('Showing results from UK')

    wrapper.unmount()
  })

  it('walks the entire chain and shows a multi-region "no matches" error when all regions are empty', async () => {
    apiMocks.searchAudibleByTitleAndAuthor.mockResolvedValue({ totalResults: 0, results: [] })

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: bookWithoutAsin },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    const regions = apiMocks.searchAudibleByTitleAndAuthor.mock.calls.map((c) => c[4])
    expect(regions).toEqual(['us', 'uk', 'ca', 'au'])
    expect(document.body.textContent).toContain('No matches in any region')
    expect(document.body.textContent).toContain('US, UK, CA, AU')

    wrapper.unmount()
  })

  it('skips fallback when the user explicitly picks a region (locked to that store)', async () => {
    apiMocks.searchAudibleByTitleAndAuthor.mockResolvedValue({ totalResults: 0, results: [] })

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: bookWithoutAsin },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    // Initial Auto run already happened; clear and re-query as explicit UK.
    apiMocks.searchAudibleByTitleAndAuthor.mockClear()
    const selects = document.body.querySelectorAll('select')
    const regionSelect = Array.from(selects).find(
      (s) => s.querySelector('option[value="us"]') !== null,
    ) as HTMLSelectElement | undefined
    expect(regionSelect, 'expected a region dropdown in the candidate-search form').toBeTruthy()
    regionSelect!.value = 'uk'
    regionSelect!.dispatchEvent(new Event('change'))
    await flushPromises()

    // Trigger the "Search again" path.
    const buttons = Array.from(document.body.querySelectorAll('button')) as HTMLButtonElement[]
    const searchBtn = buttons.find((b) => /Search again/i.test(b.textContent || ''))
    expect(searchBtn, 'expected the Search again button').toBeTruthy()
    searchBtn!.click()
    await flushPromises()

    // Only UK should have been queried — no walking the chain.
    const regions = apiMocks.searchAudibleByTitleAndAuthor.mock.calls.map((c) => c[4])
    expect(regions).toEqual(['uk'])

    wrapper.unmount()
  })

  it('routes the per-ASIN metadata lookup to the candidate\'s own region (UK pick → UK lookup)', async () => {
    apiMocks.searchAudibleByTitleAndAuthor.mockImplementation((_t, _a, _p, _l, region) =>
      Promise.resolve(
        region === 'uk' ? { totalResults: 1, results: [ukMatch()] } : { totalResults: 0, results: [] },
      ),
    )
    apiMocks.getAudibleMetadata.mockResolvedValue({
      metadata: { asin: '1774241595', title: 'Star Kingdom Omnibus II' },
    })

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: bookWithoutAsin },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    // Click the (only) UK candidate.
    const candidate = document.body.querySelector('.candidate-item') as HTMLElement | null
    expect(candidate, 'expected a candidate row').toBeTruthy()
    candidate!.click()
    await flushPromises()

    // The metadata lookup must hit the UK store, not US.
    expect(apiMocks.getAudibleMetadata).toHaveBeenCalledWith('1774241595', 'uk')

    wrapper.unmount()
  })
})
