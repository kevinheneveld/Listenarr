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
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { nextTick } from 'vue'

const addToLibrary = vi.fn()
const searchAudibleByTitleAndAuthor = vi.fn()
vi.mock('@/services/api', () => ({
  apiService: {
    addToLibrary: (...args: unknown[]) => addToLibrary(...args),
    searchAudibleByTitleAndAuthor: (...args: unknown[]) => searchAudibleByTitleAndAuthor(...args),
    getAudiobooks: vi.fn().mockResolvedValue([]),
  },
}))

const toast = { info: vi.fn(), success: vi.fn(), error: vi.fn(), warning: vi.fn() }
vi.mock('@/services/toastService', () => ({ useToast: () => toast }))

import CatalogTargetLookup from '@/components/domain/audiobook/CatalogTargetLookup.vue'
import { useLibraryStore } from '@/stores/library'
import type { Audiobook } from '@/types'

const specOps = {
  id: 40,
  title: 'SpecOps',
  authors: ['Craig Alanson'],
  fileCount: 34,
} as unknown as Audiobook

async function mountOpen(excludeId?: number) {
  const pinia = createPinia()
  setActivePinia(pinia)
  const store = useLibraryStore()
  store.audiobooks = [
    specOps,
    {
      id: 338,
      title: 'Mavericks',
      authors: ['Craig Alanson'],
      fileCount: 99,
    } as unknown as Audiobook,
  ]
  const wrapper = mount(CatalogTargetLookup, {
    props: { defaultTitle: 'Mavericks-04', defaultAuthor: 'Craig Alanson', excludeId },
    global: { plugins: [pinia] },
  })
  await wrapper.find('button.catalog-toggle').trigger('click')
  await nextTick()
  return wrapper
}

describe('CatalogTargetLookup', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('offers matching library records for the typed title before any catalog add', async () => {
    const wrapper = await mountOpen(338)
    const titleInput = wrapper.findAll('input.catalog-input')[0]
    await titleInput.setValue('specops')
    await nextTick()

    const library = wrapper.find('.catalog-library')
    expect(library.exists()).toBe(true)
    expect(library.text()).toContain('SpecOps')
    expect(library.text()).toContain('34 file(s)')
    // The source record itself is never offered.
    expect(library.text()).not.toContain('Mavericks')

    await library.find('button.catalog-select-btn').trigger('click')
    const selected = wrapper.emitted('selected')
    expect(selected).toHaveLength(1)
    expect((selected![0][0] as Audiobook).id).toBe(40)
    expect(wrapper.emitted('added')).toBeUndefined()
    expect(addToLibrary).not.toHaveBeenCalled()
  })

  it('selects the existing record when the add comes back 409 already-exists', async () => {
    const wrapper = await mountOpen(338)
    searchAudibleByTitleAndAuthor.mockResolvedValue({
      results: [{ asin: 'B06W5861HP', title: 'SpecOps', authors: [{ name: 'Craig Alanson' }] }],
    })
    const conflict = Object.assign(new Error('API error: 409 {...}'), {
      status: 409,
      body: JSON.stringify({ message: 'Audiobook already exists in library', audiobook: specOps }),
    })
    addToLibrary.mockRejectedValue(conflict)

    await wrapper.findAll('input.catalog-input')[0].setValue('spec ops')
    await wrapper.find('button.catalog-search-btn').trigger('click')
    await vi.waitFor(() =>
      expect(wrapper.find('button.catalog-add-btn:not(.catalog-select-btn)').exists()).toBe(true),
    )
    await wrapper.find('button.catalog-add-btn:not(.catalog-select-btn)').trigger('click')
    await vi.waitFor(() => expect(wrapper.emitted('selected')).toHaveLength(1))

    expect((wrapper.emitted('selected')![0][0] as Audiobook).id).toBe(40)
    expect(wrapper.emitted('added')).toBeUndefined()
    expect(toast.info).toHaveBeenCalledWith(
      'Already in your library',
      expect.stringContaining('SpecOps'),
    )
    expect(toast.error).not.toHaveBeenCalled()
  })

  it('shows the server message, not the raw JSON, for other add failures', async () => {
    const wrapper = await mountOpen(338)
    searchAudibleByTitleAndAuthor.mockResolvedValue({
      results: [{ asin: 'B000', title: 'Anything', authors: [] }],
    })
    addToLibrary.mockRejectedValue(
      Object.assign(new Error('API error: 500 {"message":"Provider unavailable"}'), {
        status: 500,
        body: '{"message":"Provider unavailable"}',
      }),
    )

    await wrapper.findAll('input.catalog-input')[0].setValue('anything at all')
    await wrapper.find('button.catalog-search-btn').trigger('click')
    await vi.waitFor(() =>
      expect(wrapper.find('button.catalog-add-btn:not(.catalog-select-btn)').exists()).toBe(true),
    )
    await wrapper.find('button.catalog-add-btn:not(.catalog-select-btn)').trigger('click')
    await vi.waitFor(() => expect(toast.error).toHaveBeenCalled())

    expect(toast.error).toHaveBeenCalledWith('Add failed', 'Provider unavailable')
    expect(wrapper.emitted('selected')).toBeUndefined()
  })
})
