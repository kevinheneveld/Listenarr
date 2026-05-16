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
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

const { getLibraryMock } = vi.hoisted(() => ({
  getLibraryMock: vi.fn(),
}))

vi.mock('@/services/api', () => ({
  apiService: {
    getLibrary: getLibraryMock,
  },
}))

vi.mock('@/services/signalr', () => ({
  signalRService: {
    onFilesRemoved: vi.fn(() => undefined),
    onAudiobookUpdate: vi.fn(() => undefined),
  },
}))

import { useLibraryStore } from '@/stores/library'

describe('library store fetchLibrary', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    getLibraryMock.mockReset()
  })

  it('dedupes concurrent fetches into one API request', async () => {
    let resolveLibrary: ((value: Array<{ id: number; title: string }>) => void) | null = null
    getLibraryMock.mockImplementation(
      () =>
        new Promise<Array<{ id: number; title: string }>>((resolve) => {
          resolveLibrary = resolve
        }),
    )

    const store = useLibraryStore()

    const first = store.fetchLibrary()
    const second = store.fetchLibrary()

    expect(getLibraryMock).toHaveBeenCalledTimes(1)
    expect(store.loading).toBe(true)

    resolveLibrary?.([{ id: 1, title: 'Book 1' }])
    await Promise.all([first, second])

    expect(getLibraryMock).toHaveBeenCalledTimes(1)
    expect(store.loading).toBe(false)
    expect(store.audiobooks).toHaveLength(1)
    expect(store.audiobooks[0]?.title).toBe('Book 1')
  })

  it('does not substitute /images/${asin} when imageUrl is missing', async () => {
    getLibraryMock.mockResolvedValue([
      { id: 1, title: 'No image, has asin', asin: 'B0CQZ5167B', imageUrl: '' },
      { id: 2, title: 'Placeholder, has asin', asin: 'B00001', imageUrl: '/placeholder.svg' },
      { id: 3, title: 'Has image', asin: 'B00002', imageUrl: 'https://example.com/c.jpg' },
    ])

    const store = useLibraryStore()
    await store.fetchLibrary()

    expect(store.audiobooks[0]?.imageUrl).toBe('')
    expect(store.audiobooks[0]?.coverArtMissing).toBe(true)

    expect(store.audiobooks[1]?.imageUrl).toBe('')
    expect(store.audiobooks[1]?.coverArtMissing).toBe(true)

    expect(store.audiobooks[2]?.imageUrl).toBe('https://example.com/c.jpg')
    expect(store.audiobooks[2]?.coverArtMissing).toBeUndefined()
  })
})
