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
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import LibrarySearchView from '@/views/library/LibrarySearchView.vue'
import { useLibraryStore } from '@/stores/library'

const makeRouter = () =>
  createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', name: 'home', component: { template: '<div />' } },
      { path: '/search', name: 'search', component: LibrarySearchView },
      { path: '/audiobooks/:id', name: 'audiobook-detail', component: { template: '<div />' } },
      { path: '/collection/:type/:name', name: 'collection', component: { template: '<div />' } },
    ],
  })

const seedLibrary = () => {
  const books = Array.from({ length: 15 }, (_, i) => ({
    id: i + 1,
    title: `Test Book ${String(i + 1).padStart(2, '0')}`,
    authors: ['Test Author'],
    narrators: ['Test Narrator'],
    series: 'Test Series',
    imageUrl: `cover${i + 1}.jpg`,
    publishYear: String(2000 + i),
    files: [],
  })) as unknown as import('@/types').Audiobook[]
  const store = useLibraryStore()
  store.audiobooks = books
  store.fetchLibrary = vi.fn(async () => undefined)
  return store
}

const mountAt = async (query: string) => {
  const pinia = createPinia()
  setActivePinia(pinia)
  const router = makeRouter()
  await router.push(`/search?q=${encodeURIComponent(query)}`)
  await router.isReady().catch(() => {})
  seedLibrary()
  const wrapper = mount(LibrarySearchView, { global: { plugins: [pinia, router] } })
  await flushPromises()
  return wrapper
}

describe('LibrarySearchView', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
  })

  it('renders a tab per non-empty category with counts', async () => {
    const wrapper = await mountAt('test')
    const tabs = wrapper.findAll('.search-tab').map((t) => t.text().replace(/\s+/g, ' ').trim())
    expect(tabs).toEqual(['All 18', 'Books 15', 'Series 1', 'Authors 1', 'Narrators 1'])
  })

  it('caps each section in the All overview and offers "See all"', async () => {
    const wrapper = await mountAt('test')
    // Books section is capped to the preview limit (12) with a See-all jump.
    const booksSection = wrapper.findAll('.result-section')[0]
    expect(booksSection.findAll('.result-card')).toHaveLength(12)
    expect(booksSection.find('.see-all-link').exists()).toBe(true)
    expect(booksSection.find('.see-all-link').text()).toContain('15')
  })

  it('shows the full list when a category tab is selected', async () => {
    const wrapper = await mountAt('test')
    const booksTab = wrapper.findAll('.search-tab').find((t) => t.text().includes('Books'))!
    await booksTab.trigger('click')
    await flushPromises()
    // Only the Books section now, with all 15 and no truncation.
    expect(wrapper.findAll('.result-section')).toHaveLength(1)
    expect(wrapper.findAll('.result-card')).toHaveLength(15)
    expect(wrapper.find('.see-all-link').exists()).toBe(false)
  })

  it('toggles between grid and list view and persists the choice', async () => {
    const wrapper = await mountAt('test')
    expect(wrapper.find('.card-grid').exists()).toBe(true)
    await wrapper.find('.view-toggle .icon-btn:last-child').trigger('click')
    await flushPromises()
    expect(wrapper.find('.result-list').exists()).toBe(true)
    expect(wrapper.find('.card-grid').exists()).toBe(false)
    expect(localStorage.getItem('listenarr.search.viewMode')).toBe('list')
  })

  it('exposes category-appropriate sort options on a category tab and sorts books', async () => {
    const wrapper = await mountAt('test')
    const booksTab = wrapper.findAll('.search-tab').find((t) => t.text().includes('Books'))!
    await booksTab.trigger('click')
    await flushPromises()

    const sortValues = wrapper.findAll('.sort-select option').map((o) => o.text())
    expect(sortValues).toEqual(['Title', 'Author', 'Year'])

    // Default Title ascending: first card is "Test Book 01".
    const firstTitleAsc = wrapper.find('.result-card .card-label').text()
    expect(firstTitleAsc).toBe('Test Book 01')

    // Flip to descending -> "Test Book 15" leads.
    await wrapper.find('.sort-control .icon-btn').trigger('click')
    await flushPromises()
    expect(wrapper.find('.result-card .card-label').text()).toBe('Test Book 15')
  })

  it('hides the sort control on the All overview', async () => {
    const wrapper = await mountAt('test')
    expect(wrapper.find('.sort-control').exists()).toBe(false)
  })

  it('shows an empty state when nothing matches', async () => {
    const wrapper = await mountAt('zzzznomatch')
    expect(wrapper.find('.search-state').exists()).toBe(true)
    expect(wrapper.text()).toContain('No matches')
    expect(wrapper.find('.search-tab').exists()).toBe(false)
  })
})
