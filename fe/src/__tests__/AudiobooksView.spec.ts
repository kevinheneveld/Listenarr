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
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createRouter, createMemoryHistory } from 'vue-router'
import AudiobooksView from '@/views/library/AudiobooksView.vue'
import { useLibraryStore } from '@/stores/library'
// apiService stubbed in vi.mock below if needed

const { mockGetAudiobookDeleteCapabilities } = vi.hoisted(() => ({
  mockGetAudiobookDeleteCapabilities: vi.fn(async () => ({
    canRemoveFromLibrary: true,
    canDeleteTrackedFiles: true,
    canDeleteFolder: true,
    reason: null,
    fallbackAction: 'RemoveFromLibraryOnly' as const,
  })),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getQualityProfiles: vi.fn(async () => []),
    getImageUrl: vi.fn((url: string) => url || 'https://via.placeholder.com/300x450?text=No+Image'),
    getBootstrapConfig: vi.fn(async () => ({})),
    getStartupConfig: vi.fn(async () => ({})),
    getApplicationSettings: vi.fn(async () => ({})),
    getSeriesHealth: vi.fn(async () => ({ region: 'us', rows: [] })),
    getAudiobookDeleteCapabilities: mockGetAudiobookDeleteCapabilities,
  },
}))

type AudiobooksVm = {
  setGroupBy?: (value: string, options?: { brokenOnly?: boolean }) => Promise<void> | void
  audiobooks?: Array<{ id: number; title?: string }>
  groupedCollections?: Array<{
    name: string
    count: number
    readyCount: number
    qualityMismatchCount: number
    coverUrl?: string
    coverUrls?: string[]
  }>
  showItemDetails?: boolean
  groupBy?: string
  visibleRange?: { start: number; end: number }
  viewMode?: 'grid' | 'list'
  toggleViewMode?: () => void
  selectedFilterId?: string | null
  sortKey?: string
  sortOrder?: 'asc' | 'desc'
  sortKeyProxy?: string
  sortOptions?: Array<{ value: string; label: string }>
  confirmDelete?: (audiobook: import('@/types').Audiobook) => Promise<void>
  deleteTarget?: import('@/types').Audiobook | null
  deleteCapabilities?: import('@/types').AudiobookDeleteCapabilities | null
  showDeleteDialog?: boolean
}

const getVm = (wrapper: ReturnType<typeof mount>) => wrapper.vm as unknown as AudiobooksVm

describe('AudiobooksView', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    mockGetAudiobookDeleteCapabilities.mockReset()
    mockGetAudiobookDeleteCapabilities.mockResolvedValue({
      canRemoveFromLibrary: true,
      canDeleteTrackedFiles: true,
      canDeleteFolder: true,
      reason: null,
      fallbackAction: 'RemoveFromLibraryOnly',
    })
  })

  it('shows extra details in grid view when showItemDetails is enabled', async () => {
    // ensure ResizeObserver is defined for the mount in vtu
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    // Minimal WebSocket stub so SignalRService doesn't throw during tests
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 123,
        title: 'The Test Book',
        authors: ['Test Author'],
        narrators: ['Test Narrator'],
        publisher: 'Test Publisher',
        publishYear: 2020,
        imageUrl: 'https://example.com/cover.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    // Persist 'showItemDetails' so component mounts with details on
    localStorage.setItem('listenarr.showItemDetails', 'true')
    // Prevent real fetchLibrary from running during mount (we set audiobooks directly)
    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Find the rendered extra details block under the poster in the grid
    const bottomDetails = wrapper.find('.grid-bottom-details')
    expect(bottomDetails.exists()).toBe(true)
    expect(wrapper.text()).toContain('The Test Book')
    expect(wrapper.text()).toContain('Test Author')
    expect(wrapper.text()).toContain('Test Narrator')
    expect(wrapper.text()).toContain('Test Publisher')
    expect(wrapper.text()).toContain('2020')
  })

  it('sorts by canonical date added with unknown dates last', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      { id: 1, title: 'Oldest', authors: ['A'], added: '2023-01-01T00:00:00Z', files: [] },
      { id: 2, title: 'Newest', authors: ['A'], added: '2025-06-01T00:00:00Z', files: [] },
      { id: 3, title: 'Middle', authors: ['A'], added: '2024-03-15T00:00:00Z', files: [] },
      { id: 4, title: 'Unknown', authors: ['A'], added: null, files: [] },
    ] as import('@/types').Audiobook[]
    store.fetchLibrary = vi.fn(async () => undefined)

    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 0))

    const vm = getVm(wrapper)
    await vm.setGroupBy?.('books')
    await wrapper.vm.$nextTick()
    expect(vm.sortOptions?.map((option) => option.value)).toContain('added')

    vm.sortKey = 'added'
    vm.sortOrder = 'desc'
    await wrapper.vm.$nextTick()
    expect(vm.audiobooks?.map((book) => book.title)).toEqual([
      'Newest',
      'Middle',
      'Oldest',
      'Unknown',
    ])

    vm.sortOrder = 'asc'
    await wrapper.vm.$nextTick()
    expect(vm.audiobooks?.map((book) => book.title)).toEqual([
      'Oldest',
      'Middle',
      'Newest',
      'Unknown',
    ])

    vm.sortKey = 'title'
    vm.sortOrder = 'asc'
    vm.sortKeyProxy = 'added'
    await wrapper.vm.$nextTick()
    expect(vm.sortKey).toBe('added')
    expect(vm.sortOrder).toBe('desc')

    const year = new Date().getFullYear()
    store.audiobooks = [
      {
        id: 1,
        title: 'Old publication',
        added: new Date(year, 0, 2).toISOString(),
        publishYear: '1900',
      },
      { id: 2, title: 'Previous year', added: new Date(year - 1, 0, 2).toISOString() },
      {
        id: 3,
        title: 'Old addition',
        added: new Date(year - 2, 0, 2).toISOString(),
        publishYear: String(year),
      },
      { id: 4, title: 'Unknown addition', added: null, publishYear: String(year) },
    ] as import('@/types').Audiobook[]
    vm.selectedFilterId = 'recent'
    await wrapper.vm.$nextTick()
    expect(vm.audiobooks?.map((book) => book.title)).toEqual(['Old publication', 'Previous year'])
  })

  it('ignores stale delete capabilities when a newer audiobook becomes the target', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const books = [
      { id: 1, title: 'First', authors: ['Author'], files: [] },
      { id: 2, title: 'Second', authors: ['Author'], files: [] },
    ] as unknown as import('@/types').Audiobook[]
    const store = useLibraryStore()
    store.audiobooks = books
    store.fetchLibrary = vi.fn(async () => undefined)

    let resolveFirst!: (value: import('@/types').AudiobookDeleteCapabilities) => void
    let resolveSecond!: (value: import('@/types').AudiobookDeleteCapabilities) => void
    mockGetAudiobookDeleteCapabilities
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveFirst = resolve
          }),
      )
      .mockImplementationOnce(
        () =>
          new Promise((resolve) => {
            resolveSecond = resolve
          }),
      )

    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((resolve) => setTimeout(resolve, 0))
    const vm = getVm(wrapper)

    const firstRequest = vm.confirmDelete?.(books[0]!)
    const secondRequest = vm.confirmDelete?.(books[1]!)
    resolveSecond({
      canRemoveFromLibrary: true,
      canDeleteTrackedFiles: false,
      canDeleteFolder: false,
      reason: 'Second target is protected.',
      fallbackAction: 'RemoveFromLibraryOnly',
    })
    await secondRequest
    resolveFirst({
      canRemoveFromLibrary: true,
      canDeleteTrackedFiles: true,
      canDeleteFolder: true,
      reason: 'Stale first target.',
      fallbackAction: 'RemoveFromLibraryOnly',
    })
    await firstRequest

    expect(vm.deleteTarget?.id).toBe(2)
    expect(vm.deleteCapabilities?.reason).toBe('Second target is protected.')
    expect(vm.showDeleteDialog).toBe(true)
  })
})

describe('AudiobooksView Grouping', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    // Fork: the selected filter and per-group sort persist to localStorage.
    localStorage.clear()
  })

  it('groups audiobooks by author when groupBy is authors', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book 2',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover2.jpg',
        files: [],
      },
      {
        id: 3,
        title: 'Book 3',
        authors: ['Author B'],
        series: 'Series 2',
        imageUrl: 'cover3.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Set groupBy to authors
    const vm = getVm(wrapper)
    await vm.setGroupBy?.('authors')
    await wrapper.vm.$nextTick()

    const groupedCollections = vm.groupedCollections ?? []
    expect(groupedCollections).toHaveLength(2)
    expect(groupedCollections.find((g) => g.name === 'Author A')).toEqual({
      name: 'Author A',
      count: 2,
      readyCount: 0,
      qualityMismatchCount: 0,
      coverUrl: undefined,
    })
    expect(groupedCollections.find((g) => g.name === 'Author B')).toEqual({
      name: 'Author B',
      count: 1,
      readyCount: 0,
      qualityMismatchCount: 0,
      coverUrl: undefined,
    })

    // Default sorting when grouped by authors should be author-last ascending
    expect((vm as unknown).sortKey).toBe('author-last')
    expect((vm as unknown).sortOrder).toBe('asc')
  })

  it('groups audiobooks by series when groupBy is series', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book 2',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover2.jpg',
        files: [],
      },
      {
        id: 3,
        title: 'Book 3',
        authors: ['Author B'],
        series: 'Series 2',
        imageUrl: 'cover3.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Set groupBy to series
    const vm = getVm(wrapper)
    await vm.setGroupBy?.('series')
    await wrapper.vm.$nextTick()

    const groupedCollections = vm.groupedCollections ?? []
    expect(groupedCollections).toHaveLength(2)
    expect(groupedCollections.find((g) => g.name === 'Series 1')).toEqual({
      name: 'Series 1',
      count: 2,
      readyCount: 0,
      qualityMismatchCount: 0,
      coverUrls: ['cover1.jpg', 'cover2.jpg'],
    })
    expect(groupedCollections.find((g) => g.name === 'Series 2')).toEqual({
      name: 'Series 2',
      count: 1,
      readyCount: 0,
      qualityMismatchCount: 0,
      coverUrls: ['cover3.jpg'],
    })
  })

  it('updates toolbar sort options and sorts grouped collections by count/name depending on grouping', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      { id: 1, title: 'A1', authors: ['Author A'], series: 'Series X', imageUrl: 'c1', files: [] },
      { id: 2, title: 'A2', authors: ['Author A'], series: 'Series X', imageUrl: 'c2', files: [] },
      { id: 3, title: 'B1', authors: ['Author B'], series: 'Series Y', imageUrl: 'c3', files: [] },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    const vm = wrapper.vm as unknown as unknown

    // Switch to authors grouping and verify sortOptions exposed for collections
    await vm.setGroupBy('authors')
    await wrapper.vm.$nextTick()

    const optValues = (vm.sortOptions || []).map((o: unknown) => o.value)
    expect(optValues).toContain('author-last')
    expect(optValues).toContain('author-first')
    expect(optValues).toContain('count')

    // Default sorting when grouped by authors should be author-last ascending
    expect((vm as unknown).sortKey).toBe('author-last')
    expect((vm as unknown).sortOrder).toBe('asc')

    // CustomSelect should not be marked "active" for the default author sort
    const csStub = wrapper.find('custom-select-stub')
    expect(csStub.exists()).toBe(true)
    expect(csStub.attributes('active')).toBe('false')

    // Sort collections by count descending (non-default) — control should become active
    vm.sortKey = 'count'
    vm.sortOrder = 'desc'
    await wrapper.vm.$nextTick()
    expect(wrapper.find('custom-select-stub').attributes('active')).toBe('true')
    expect(vm.groupedCollections[0].name).toBe('Author A')

    // Sort collections by author-last ascending (back to default) — control should be inactive
    vm.sortKey = 'author-last'
    vm.sortOrder = 'asc'
    await wrapper.vm.$nextTick()
    expect(wrapper.find('custom-select-stub').attributes('active')).toBe('false')
    expect(vm.groupedCollections[0].name).toBe('Author A')

    // Switch to series grouping and verify options
    await vm.setGroupBy('series')
    await wrapper.vm.$nextTick()
    const seriesOpt = (vm.sortOptions || []).map((o: unknown) => o.value)
    expect(seriesOpt).toContain('title')
    expect(seriesOpt).toContain('count')
    expect(seriesOpt).toContain('completion')
    expect(seriesOpt).toContain('missing')
    expect(seriesOpt).not.toContain('author-last')

    // Series default should be `title` ascending and the control should NOT be active
    expect((vm as unknown).sortKey).toBe('title')
    expect((vm as unknown).sortOrder).toBe('asc')
    expect(wrapper.find('custom-select-stub').attributes('active')).toBe('false')

    // Sort series by count ascending (non-default)
    vm.sortKey = 'count'
    vm.sortOrder = 'asc'
    await wrapper.vm.$nextTick()
    expect(wrapper.find('custom-select-stub').attributes('active')).toBe('true')
    expect(vm.groupedCollections[0].name).toBe('Series Y')
  })

  it('shows individual books when groupBy is books', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    // Ensure groupBy is 'books'
    localStorage.setItem('listenarr.groupBy', 'books')
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // groupBy defaults to 'books'
    const vm = getVm(wrapper)
    const groupedCollections = vm.groupedCollections ?? []
    expect(groupedCollections).toHaveLength(0)
  })

  it("'Clear Filters' button resets search, custom filter and builtin filters", async () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    // single audiobook that would be shown when no filters/search applied
    store.audiobooks = [
      { id: 1, title: 'Visible Book', authors: ['Author A'], imageUrl: 'c1', files: [] },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })

    const vm = wrapper.vm as unknown as unknown

    // Apply a search that yields no results and a custom filter selection
    vm.searchQuery = 'no-match-query'
    vm.selectedFilterId = 'custom-1'
    vm.filterMonitored = 'monitored'
    await wrapper.vm.$nextTick()

    // Should show the 'No audiobooks match your filters' empty state
    expect(wrapper.text()).toContain('No audiobooks match your filters')

    // Click the Clear Filters button and verify everything resets
    const clearBtn = wrapper.find('button.btn.btn-primary')
    expect(clearBtn.exists()).toBe(true)
    expect(clearBtn.text()).toContain('Clear Filters')

    await clearBtn.trigger('click')
    await wrapper.vm.$nextTick()

    expect(vm.searchQuery).toBe('')
    expect(vm.selectedFilterId).toBeNull()
    expect(vm.filterMonitored).toBe('all')

    // After clearing, the audiobook should be visible again
    expect(wrapper.text()).toContain('Visible Book')
  })

  it('route query group parameter overrides stored preference on initial load', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    // Simulate previous preference saved as 'series'
    localStorage.setItem('listenarr.groupBy', 'series')
    // Navigate to audiobooks with explicit group=books in URL
    await router.push({ path: '/audiobooks', query: { group: 'books' } })
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Expect the component to use the route query 'books' despite stored 'series'
    expect((wrapper.vm as unknown as { groupBy: string }).groupBy).toBe('books')
  })

  it('resets the virtual range when returning to books grouping', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push({ path: '/audiobooks', query: { group: 'authors' } })
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = Array.from({ length: 50 }, (_, index) => ({
      id: index + 1,
      title: `Book ${index + 1}`,
      authors: [`Author ${index % 5}`],
      imageUrl: `cover${index + 1}.jpg`,
      files: [],
    })) as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    const vm = getVm(wrapper)
    vm.visibleRange = { start: 40, end: 50 }
    await vm.setGroupBy?.('books')
    await wrapper.vm.$nextTick()

    expect(vm.groupBy).toBe('books')
    expect(vm.visibleRange?.start).toBe(0)
  })

  it('clears selection when changing grouping mode', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book 2',
        authors: ['Author B'],
        series: 'Series 2',
        imageUrl: 'cover2.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Select one item
    store.toggleSelection(1)
    expect(store.selectedIds.size).toBeGreaterThan(0)

    // Switch group and expect selection cleared
    const vm = getVm(wrapper)
    await vm.setGroupBy?.('authors')
    await wrapper.vm.$nextTick()
    expect(store.selectedIds.size).toBe(0)
  })

  it('shows Repair Selected with the number of selected books', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book 2',
        authors: ['Author B'],
        series: 'Series 2',
        imageUrl: 'cover2.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    store.toggleSelection(1)
    store.toggleSelection(2)
    await wrapper.vm.$nextTick()

    const repair = wrapper.get('button.repair-btn')
    expect(repair.text()).toContain('Repair Selected (2)')
  })

  describe('Broken books group', () => {
    // brokenReasons come from the server
    const books = [
      { id: 1, title: 'Fine', brokenReasons: [] },
      { id: 2, title: 'No ASIN', brokenReasons: ['missing_asin'] },
      {
        id: 3,
        title: '',
        brokenReasons: ['missing_title', 'missing_region'],
      },
      { id: 4, title: 'Also fine', brokenReasons: [] },
    ]

    async function mountLibrary(
      library: Array<{ id: number; title: string; brokenReasons: string[] }>,
      path = '/audiobooks',
    ) {
      if (
        typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver ===
        'undefined'
      ) {
        ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
          observe() {}
          disconnect() {}
        }
      }
      const pinia = createPinia()
      setActivePinia(pinia)
      const router = createRouter({
        history: createMemoryHistory(),
        routes: [
          { path: '/', name: 'home', component: { template: '<div />' } },
          { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
        ],
      })
      await router.push(path)
      await router.isReady().catch(() => {})
      const store = useLibraryStore()
      store.audiobooks = library.map((b) => ({
        ...b,
        authors: ['Author'],
        files: [],
      })) as unknown as import('@/types').Audiobook[]
      store.fetchLibrary = vi.fn(async () => undefined)
      const wrapper = mount(AudiobooksView, {
        global: {
          plugins: [pinia, router],
          stubs: [
            'BulkEditModal',
            'EditAudiobookModal',
            'CustomFilterModal',
            'FiltersDropdown',
            'CustomSelect',
          ],
        },
      })
      await new Promise((r) => setTimeout(r, 0))
      return { wrapper, router }
    }

    const shownIds = (wrapper: ReturnType<typeof mount>) =>
      (getVm(wrapper).audiobooks ?? []).map((b) => b.id).sort()

    beforeEach(() => {
      localStorage.removeItem('listenarr.groupBy')
    })

    afterEach(() => {
      // The group is remembered: later tests must not open on it
      localStorage.removeItem('listenarr.groupBy')
    })

    it('lists only the books the server gives reasons for, from the last item of the group menu', async () => {
      const { wrapper, router } = await mountLibrary(books)

      await wrapper.get('.group-btn').trigger('click')
      expect(wrapper.findAll('.group-menu .menu-item').map((i) => i.text())).toEqual([
        'Books',
        'Authors',
        'Series',
        'Narrators',
        'Broken books',
      ])
      await wrapper.get('[data-testid="group-broken"]').trigger('click')
      await new Promise((r) => setTimeout(r, 0))

      expect(shownIds(wrapper)).toEqual([2, 3])
      expect(wrapper.get('.group-btn').text()).toContain('Broken books')
      expect(router.currentRoute.value.query.group).toBe('broken')
      expect(localStorage.getItem('listenarr.groupBy')).toBe('broken')

      await getVm(wrapper).setGroupBy?.('books')
      await new Promise((r) => setTimeout(r, 0))
      expect(shownIds(wrapper)).toEqual([1, 2, 3, 4])
      expect(router.currentRoute.value.query.group).toBe('books')
    })

    it("shows each book's reasons as the server gives them, only in the broken group", async () => {
      const { wrapper } = await mountLibrary(books, '/audiobooks?group=broken')

      const tagsPerBook = wrapper
        .findAll('[data-testid="broken-reasons"]')
        .map((r) => r.findAll('.broken-reason').map((tag) => tag.text()))
      expect(tagsPerBook).toEqual(expect.arrayContaining([['No ASIN'], ['No title', 'No region']]))

      await getVm(wrapper).setGroupBy?.('books')
      await new Promise((r) => setTimeout(r, 0))
      expect(wrapper.find('[data-testid="broken-reasons"]').exists()).toBe(false)
    })

    it('opens on the broken books from the address', async () => {
      const { wrapper } = await mountLibrary(books, '/audiobooks?group=broken')

      expect(shownIds(wrapper)).toEqual([2, 3])
    })

    const clickSelectAll = async (wrapper: ReturnType<typeof mount>) => {
      // Fork: the button's label carries the count of books shown.
      await wrapper.get('[data-testid="select-all-audiobooks"]').trigger('click')
      await wrapper.vm.$nextTick()
    }

    it('selects only the broken books with Select All, so Repair leaves the healthy ones out', async () => {
      const { wrapper } = await mountLibrary(books, '/audiobooks?group=broken')

      await clickSelectAll(wrapper)

      expect([...useLibraryStore().selectedIds].sort()).toEqual([2, 3])
      expect(wrapper.get('button.repair-btn').text()).toContain('Repair Selected (2)')
    })

    it('still selects every book with Select All outside the broken group', async () => {
      const { wrapper } = await mountLibrary(books)

      await clickSelectAll(wrapper)

      expect([...useLibraryStore().selectedIds].sort()).toEqual([1, 2, 3, 4])
    })

    it('says when no book is broken, keeps the group menu and offers all books', async () => {
      const { wrapper } = await mountLibrary([books[0]!], '/audiobooks?group=broken')

      expect(wrapper.text()).toContain('No broken books')
      expect(wrapper.find('.group-btn').exists()).toBe(true)
    })
  })

  it('series bottom placard is only visible when showItemDetails is enabled', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    // Ensure persisted item details are cleared for this test (deterministic)
    localStorage.setItem('listenarr.showItemDetails', 'false')

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        series: 'Series 1',
        imageUrl: 'cover1.jpg',
        files: [],
      },
      {
        id: 2,
        title: 'Book 2',
        authors: ['Author B'],
        series: 'Series 2',
        imageUrl: 'cover2.jpg',
        files: [],
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    // Set groupBy to series
    const vm = getVm(wrapper)
    await vm.setGroupBy?.('series')
    await wrapper.vm.$nextTick()

    // By default, details should be hidden and placard not present
    expect(vm.showItemDetails).toBe(false)
    expect(wrapper.find('.series-bottom-placard').exists()).toBe(false)

    // Enable details and confirm placard is shown
    if (vm) {
      vm.showItemDetails = true
    }
    await wrapper.vm.$nextTick()
    expect(wrapper.find('.series-bottom-placard').exists()).toBe(true)
  })

  it('tracks readyCount and qualityMismatchCount per collection from server status', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }

    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})

    const store = useLibraryStore()
    // Author A: one at-cutoff book and one below-cutoff book (both ready);
    // Author B: one wanted book with no audio (not ready).
    store.audiobooks = [
      {
        id: 1,
        title: 'Book 1',
        authors: ['Author A'],
        imageUrl: 'cover1.jpg',
        status: 'quality-match',
      },
      {
        id: 2,
        title: 'Book 2',
        authors: ['Author A'],
        imageUrl: 'cover2.jpg',
        status: 'quality-mismatch',
      },
      {
        id: 3,
        title: 'Book 3',
        authors: ['Author B'],
        imageUrl: 'cover3.jpg',
        status: 'no-file',
      },
    ] as unknown as import('@/types').Audiobook[]

    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))

    const vm = getVm(wrapper)
    await vm.setGroupBy?.('authors')
    await wrapper.vm.$nextTick()

    const groups = vm.groupedCollections ?? []
    const a = groups.find((g) => g.name === 'Author A')!
    const b = groups.find((g) => g.name === 'Author B')!
    expect(a.count).toBe(2)
    expect(a.readyCount).toBe(2)
    expect(a.qualityMismatchCount).toBe(1)
    expect(b.count).toBe(1)
    expect(b.readyCount).toBe(0)
    expect(b.qualityMismatchCount).toBe(0)
  })

  it.each([
    ['authors', 'Author A', 'Author B'],
    ['series', 'Series 1', 'Series 2'],
    ['narrators', 'Narrator A', 'Narrator B'],
  ] as const)(
    'renders collection list rows when viewMode is list and groupBy is %s',
    async (group, firstName, secondName) => {
      if (
        typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver ===
        'undefined'
      ) {
        ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
          observe() {}
          disconnect() {}
        }
      }
      if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
        ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
          /* noop */
        }
      }

      // Clear persistence keys so this test isn't affected by other tests.
      localStorage.removeItem('listenarr.viewMode')
      localStorage.removeItem('listenarr.viewMode.books')
      localStorage.removeItem('listenarr.viewMode.authors')
      localStorage.removeItem('listenarr.viewMode.series')
      localStorage.removeItem('listenarr.viewMode.narrators')

      const pinia = createPinia()
      setActivePinia(pinia)
      const router = createRouter({
        history: createMemoryHistory(),
        routes: [
          { path: '/', name: 'home', component: { template: '<div />' } },
          { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
          {
            path: '/collection/:type/:name',
            name: 'collection',
            component: { template: '<div />' },
          },
        ],
      })
      await router.push('/audiobooks')
      await router.isReady().catch(() => {})

      const store = useLibraryStore()
      store.audiobooks = [
        {
          id: 1,
          title: 'Book 1',
          authors: ['Author A'],
          series: 'Series 1',
          narrators: ['Narrator A'],
          imageUrl: 'cover1.jpg',
        },
        {
          id: 2,
          title: 'Book 2',
          authors: ['Author A'],
          series: 'Series 1',
          narrators: ['Narrator A'],
          imageUrl: 'cover2.jpg',
        },
        {
          id: 3,
          title: 'Book 3',
          authors: ['Author B'],
          series: 'Series 2',
          narrators: ['Narrator B'],
          imageUrl: 'cover3.jpg',
        },
      ] as unknown as import('@/types').Audiobook[]

      store.fetchLibrary = vi.fn(async () => undefined)
      const wrapper = mount(AudiobooksView, {
        global: {
          plugins: [pinia, router],
          stubs: [
            'BulkEditModal',
            'EditAudiobookModal',
            'CustomFilterModal',
            'FiltersDropdown',
            'CustomSelect',
          ],
        },
      })
      await new Promise((r) => setTimeout(r, 0))

      const vm = getVm(wrapper)
      // Per-grouping viewMode persistence: switch grouping first, then toggle
      // that grouping's own mode (matches the real user flow).
      await vm.setGroupBy?.(group)
      vm.toggleViewMode?.()
      await wrapper.vm.$nextTick()

      expect(vm.viewMode).toBe('list')
      const rows = wrapper.findAll('.collection-list-item')
      expect(rows).toHaveLength(2)
      // Grid-mode card markup should NOT be present when list mode is active.
      expect(wrapper.find('.grouped-grid').exists()).toBe(false)

      const names = rows.map((r) => r.find('.audiobook-title').text())
      expect(names).toContain(firstName)
      expect(names).toContain(secondName)
    },
  )

  it('opens Organize from Repair at once, the library refreshed behind it', async () => {
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [{ path: '/audiobooks', name: 'audiobooks', component: AudiobooksView }],
    })
    await router.push('/audiobooks')
    await router.isReady().catch(() => {})
    const store = useLibraryStore()
    store.audiobooks = [
      { id: 123, title: 'The Test Book', authors: ['Test Author'], files: [] },
    ] as unknown as import('@/types').Audiobook[]
    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
          'RepairBooksModal',
          'RenamePreviewModal',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))
    // The library's refresh held (a big library)
    let endRefresh!: () => void
    store.fetchLibrary = vi.fn(() => new Promise<void>((resolve) => (endRefresh = resolve)))

    wrapper.findComponent({ name: 'RepairBooksModal' }).vm.$emit('organize', [123])
    await flushPromises()

    const organize = wrapper.findComponent({ name: 'RenamePreviewModal' })
    expect(organize.props('visible')).toBe(true)
    expect(organize.props('audiobookIds')).toEqual([123])
    expect(store.fetchLibrary).toHaveBeenCalledTimes(1)
    endRefresh()
  })
})

describe('AudiobooksView filter/URL sync', () => {
  beforeEach(() => {
    const pinia = createPinia()
    setActivePinia(pinia)
    localStorage.clear()
    if (
      typeof (globalThis as unknown as { ResizeObserver?: unknown }).ResizeObserver === 'undefined'
    ) {
      ;(globalThis as unknown as Record<string, unknown>).ResizeObserver = class {
        observe() {}
        disconnect() {}
      }
    }
    if (typeof (globalThis as unknown as { WebSocket?: unknown }).WebSocket === 'undefined') {
      ;(globalThis as unknown as Record<string, unknown>).WebSocket = function () {
        /* noop */
      }
    }
  })

  async function mountAt(path: string) {
    const pinia = createPinia()
    setActivePinia(pinia)
    const router = createRouter({
      history: createMemoryHistory(),
      routes: [
        { path: '/', name: 'home', component: { template: '<div />' } },
        { path: '/audiobooks', name: 'audiobooks', component: AudiobooksView },
      ],
    })
    await router.push(path)
    await router.isReady().catch(() => {})
    const store = useLibraryStore()
    store.audiobooks = [] as unknown as import('@/types').Audiobook[]
    store.fetchLibrary = vi.fn(async () => undefined)
    const wrapper = mount(AudiobooksView, {
      global: {
        plugins: [pinia, router],
        stubs: [
          'BulkEditModal',
          'EditAudiobookModal',
          'CustomFilterModal',
          'FiltersDropdown',
          'CustomSelect',
        ],
      },
    })
    await new Promise((r) => setTimeout(r, 0))
    return { wrapper, router }
  }

  it('persists a deep-linked filter to localStorage and keeps it in the URL', async () => {
    const { router } = await mountAt('/audiobooks?filter=needs-review')
    await new Promise((r) => setTimeout(r, 0))

    // The deep-linked filter must survive into storage (a later bare visit
    // restores it) and stay in the normalized URL alongside the added group.
    expect(localStorage.getItem('listenarr.selectedFilter')).toBe('needs-review')
    expect(router.currentRoute.value.query.filter).toBe('needs-review')
    expect(typeof router.currentRoute.value.query.group).toBe('string')
  })

  it('mirrors a filter change into the URL so history entries self-describe', async () => {
    const { wrapper, router } = await mountAt('/audiobooks?group=books')
    const vm = wrapper.vm as unknown as { selectedFilterId: string | null }

    vm.selectedFilterId = 'needs-review'
    await new Promise((r) => setTimeout(r, 0))
    expect(router.currentRoute.value.query.filter).toBe('needs-review')
    expect(router.currentRoute.value.query.group).toBe('books')

    vm.selectedFilterId = null
    await new Promise((r) => setTimeout(r, 0))
    expect(router.currentRoute.value.query.filter).toBeUndefined()
    expect(router.currentRoute.value.query.group).toBe('books')
  })

  it('adopts the filter from the URL on back/forward without remounting', async () => {
    const { wrapper, router } = await mountAt('/audiobooks?group=books&filter=needs-review')
    const vm = wrapper.vm as unknown as { selectedFilterId: string | null }
    expect(vm.selectedFilterId).toBe('needs-review')

    // Same route record with a different query — component is reused.
    await router.replace('/audiobooks?group=books&filter=missing')
    await new Promise((r) => setTimeout(r, 0))
    expect(vm.selectedFilterId).toBe('missing')
  })
})
