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
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

type ActivityCategory = 'InProgress' | 'Blocked' | 'Imported' | 'Failed' | 'Stalled'

type Row = {
  id: string
  title?: string
  status?: string
  category?: ActivityCategory
  progress?: number
  reason?: string
  attemptCount?: number
  downloadClientId?: string
  downloadClient?: string
  downloadClientType?: string
  canRemove?: boolean
}

type ActivityViewVm = {
  allActivityItems: Row[]
  filteredQueue: Row[]
  filterText: string
  activeCategory: ActivityCategory | null
  selectCategory: (c: ActivityCategory | null) => void
  summaryChips: Array<{ key: string; label: string; count: number; category: ActivityCategory | null }>
  showRemoveModal: boolean
  clientHasQueueEntry: boolean | null
  queueHealthClients: Array<{ name: string; isUnavailable?: boolean }>
  removeFromQueue: (item: Row) => Promise<void> | void
  confirmRemove: () => Promise<void>
}

// --- builders for the activity endpoint payload ---------------------------------

const makeItem = (over: Partial<Record<string, unknown>> = {}) => ({
  id: 'item-1',
  title: 'A Book',
  artist: '',
  category: 'InProgress' as ActivityCategory,
  status: 'Downloading',
  progress: 0,
  totalSize: 1000,
  downloadedSize: 0,
  startedAt: new Date().toISOString(),
  activityAt: new Date().toISOString(),
  completedAt: undefined,
  reason: undefined,
  attemptCount: 1,
  downloadClientId: 'qbit',
  downloadClientName: 'qBittorrent',
  ...over,
})

const makeResponse = (
  items: Array<Record<string, unknown>>,
  summary: Partial<Record<string, unknown>> = {},
) => ({
  summary: {
    inProgress: 0,
    blocked: 0,
    imported: 0,
    failed: 0,
    stalled: 0,
    windowHours: 24,
    failureReasons: [],
    ...summary,
  },
  items,
  totalItems: items.length,
})

const mockSignalR = () => {
  vi.doMock('@/services/signalr', () => ({
    signalRService: {
      onQueueUpdate: vi.fn(() => () => undefined),
    },
  }))
}

const mockApi = (overrides: Record<string, unknown> = {}) => {
  const apiService = {
    getQueue: vi.fn(async () => []),
    getActivity: vi.fn(async () => makeResponse([])),
    removeFromQueue: vi.fn(async () => undefined),
    cancelDownload: vi.fn(async () => undefined),
    ...overrides,
  }

  vi.doMock('@/services/api', () => ({ apiService }))
  return apiService
}

const mockLibraryStore = (audiobooks: Array<{ id: number; title: string }> = []) => {
  vi.doMock('@/stores/library', () => ({
    useLibraryStore: () => ({ audiobooks }),
  }))
}

const mountActivityView = async () => {
  const { default: ActivityViewComponent } = await import('@/views/activity/ActivityView.vue')
  const wrapper = mount(ActivityViewComponent, {
    global: {
      stubs: {
        CustomSelect: true,
        RouterLink: { template: '<a><slot /></a>' },
      },
    },
  })

  await flushPromises()
  await new Promise((resolve) => setTimeout(resolve, 0))
  return wrapper
}

describe('ActivityView', () => {
  beforeEach(() => {
    vi.resetModules()
    vi.clearAllMocks()
    vi.spyOn(globalThis, 'setInterval').mockReturnValue(
      1 as unknown as ReturnType<typeof setInterval>,
    )
    vi.spyOn(globalThis, 'clearInterval').mockImplementation(() => undefined)
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('maps activity items to rows with category-derived status badges', async () => {
    mockSignalR()
    mockApi({
      getActivity: vi.fn(async () =>
        makeResponse([
          makeItem({ id: 'a', category: 'InProgress', status: 'ImportPending' }),
          makeItem({ id: 'b', category: 'Blocked', status: 'ImportBlocked' }),
          makeItem({ id: 'c', category: 'Imported', status: 'Moved' }),
          makeItem({ id: 'd', category: 'Failed', status: 'Failed' }),
          makeItem({ id: 'e', category: 'Stalled', status: 'Failed' }),
        ]),
      ),
    })
    mockLibraryStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm
    const byId = (id: string) => vm.allActivityItems.find((i) => i.id === id)

    expect(byId('a')?.status).toBe('importpending') // InProgress keeps the specific in-flight state
    expect(byId('b')?.status).toBe('importblocked')
    expect(byId('c')?.status).toBe('imported')
    expect(byId('d')?.status).toBe('failed')
    expect(byId('e')?.status).toBe('stalled')
  })

  it('keeps the authoritative DB status even when the live queue says downloading (no bounce)', async () => {
    // The DB record is ImportBlocked, but the still-seeding torrent shows up in the live queue
    // snapshot as "downloading". The row must stay blocked, not flip.
    mockSignalR()
    mockApi({
      getActivity: vi.fn(async () =>
        makeResponse([makeItem({ id: 'seed', category: 'Blocked', status: 'ImportBlocked', progress: 100 })]),
      ),
      getQueue: vi.fn(async () => [
        { id: 'seed', title: 'A Book', status: 'downloading', progress: 100, downloadClientId: 'qbit' },
      ]),
    })
    mockLibraryStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.allActivityItems.find((i) => i.id === 'seed')?.status).toBe('importblocked')
  })

  it('overlays live download progress onto in-progress rows', async () => {
    mockSignalR()
    mockApi({
      getActivity: vi.fn(async () =>
        makeResponse([makeItem({ id: 'dl', category: 'InProgress', status: 'Downloading', progress: 10 })]),
      ),
      getQueue: vi.fn(async () => [
        { id: 'dl', title: 'A Book', status: 'downloading', progress: 82, downloadClientId: 'qbit' },
      ]),
    })
    mockLibraryStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.allActivityItems.find((i) => i.id === 'dl')?.progress).toBe(82)
  })

  it('filters the activity list by text across title, status and reason', async () => {
    mockSignalR()
    mockApi({
      getActivity: vi.fn(async () =>
        makeResponse([
          makeItem({ id: 'one', title: 'One', category: 'InProgress' }),
          makeItem({ id: 'two', title: 'Two', category: 'Blocked', status: 'ImportBlocked' }),
        ]),
      ),
    })
    mockLibraryStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    vm.filterText = 'two'
    await flushPromises()

    expect(vm.filteredQueue).toHaveLength(1)
    expect(vm.filteredQueue[0]?.id).toBe('two')
  })

  it('exposes summary counts and drills into a category on chip click', async () => {
    mockSignalR()
    const apiService = mockApi({
      getActivity: vi.fn(async () =>
        makeResponse([makeItem({ id: 'one', category: 'InProgress' })], {
          inProgress: 3,
          failed: 2,
        }),
      ),
    })
    mockLibraryStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.summaryChips.find((c) => c.key === 'inprogress')?.count).toBe(3)
    expect(vm.summaryChips.find((c) => c.key === 'failed')?.count).toBe(2)

    vm.selectCategory('Failed')
    await flushPromises()
    expect(vm.activeCategory).toBe('Failed')
    expect(apiService.getActivity).toHaveBeenLastCalledWith({ category: 'Failed' })

    // Clicking the active chip returns to the default view.
    vm.selectCategory('Failed')
    await flushPromises()
    expect(vm.activeCategory).toBeNull()
    expect(apiService.getActivity).toHaveBeenLastCalledWith({ category: undefined })
  })

  it('surfaces the attempt count for collapsed rows', async () => {
    mockSignalR()
    mockApi({
      getActivity: vi.fn(async () =>
        makeResponse([makeItem({ id: 'collapsed', attemptCount: 4 })]),
      ),
    })
    mockLibraryStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.allActivityItems.find((i) => i.id === 'collapsed')?.attemptCount).toBe(4)
  })

  it('removes a queue-backed item through the client', async () => {
    mockSignalR()
    const apiService = mockApi({
      getActivity: vi.fn(async () =>
        makeResponse([makeItem({ id: 'q1', category: 'InProgress', downloadClientId: 'qbittorrent' })]),
      ),
      getQueue: vi.fn(async () => [
        { id: 'q1', title: 'A Book', status: 'downloading', progress: 50, downloadClientId: 'qbittorrent' },
      ]),
    })
    mockLibraryStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm
    const item = vm.allActivityItems.find((i) => i.id === 'q1')
    expect(item).toBeDefined()

    await vm.removeFromQueue(item!)
    expect(vm.showRemoveModal).toBe(true)
    expect(vm.clientHasQueueEntry).toBe(true)

    await vm.confirmRemove()
    expect(apiService.removeFromQueue).toHaveBeenCalledWith('q1', 'qbittorrent')
  })

  it('offers Listenarr-only removal when the item is no longer in the client queue', async () => {
    mockSignalR()
    const apiService = mockApi({
      getActivity: vi.fn(async () =>
        makeResponse([makeItem({ id: 'gone', category: 'Blocked', status: 'ImportBlocked', downloadClientId: 'SABnzbd' })]),
      ),
      getQueue: vi.fn(async () => []),
    })
    mockLibraryStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm
    const item = vm.allActivityItems.find((i) => i.id === 'gone')
    expect(item).toBeDefined()

    await vm.removeFromQueue(item!)
    expect(vm.showRemoveModal).toBe(true)
    expect(vm.clientHasQueueEntry).toBe(false)

    await vm.confirmRemove()
    expect(apiService.cancelDownload).toHaveBeenCalledWith('gone')
  })

  it('shows unavailable client health even when no queue items are returned', async () => {
    mockSignalR()
    mockApi({
      getQueue: vi.fn(async () => ({
        items: [],
        clients: [
          {
            clientId: 'qb-1',
            clientName: 'qBittorrent',
            clientType: 'qbittorrent',
            snapshotState: 'unavailable',
            isStaleSnapshot: false,
            isUnavailable: true,
            snapshotFailureReason: 'timeout',
            itemCount: 0,
          },
        ],
        generatedAt: new Date().toISOString(),
        hasStaleData: false,
        hasUnavailableClients: true,
      })),
    })
    mockLibraryStore()

    const wrapper = await mountActivityView()
    const vm = wrapper.vm as unknown as ActivityViewVm

    expect(vm.queueHealthClients).toHaveLength(1)
    expect(vm.queueHealthClients[0]?.name).toBe('qBittorrent')
    expect(wrapper.text()).toContain('Some queue data is unavailable')
    expect(wrapper.text()).toContain('qBittorrent unavailable after a timeout')
  })
})
