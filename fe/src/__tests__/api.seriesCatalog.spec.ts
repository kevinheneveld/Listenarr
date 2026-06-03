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
import { describe, it, expect, vi, afterEach } from 'vitest'

// The catalog methods must treat a 404 from the metadata provider as a
// legitimate "not found" (return null) while surfacing every other failure so
// the UI can show real detail instead of a generic "Error Loading Library".
describe('ApiService catalog 404 handling', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  const stubFetch = (response: Response) => {
    const fetchMock = vi.fn(() => Promise.resolve(response))
    vi.stubGlobal('fetch', fetchMock)
    return fetchMock
  }

  const loadApi = async () => {
    vi.resetModules()
    return vi.importActual<typeof import('@/services/api')>('@/services/api')
  }

  it('returns null when the series catalog 404s (series not found)', async () => {
    stubFetch(new Response('Series not found', { status: 404 }))
    const { apiService } = await loadApi()

    await expect(apiService.getSeriesCatalog('Nonexistent', 'us')).resolves.toBeNull()
  })

  it('returns null when the author catalog 404s (author not found)', async () => {
    stubFetch(new Response('Author not found', { status: 404 }))
    const { apiService } = await loadApi()

    await expect(apiService.getAuthorCatalog('Nobody', 'us')).resolves.toBeNull()
  })

  it('throws (rather than swallowing) on a real server error so the message is surfaced', async () => {
    stubFetch(new Response('boom', { status: 500 }))
    const { apiService } = await loadApi()

    await expect(apiService.getSeriesCatalog("Seeker's Tale", 'us')).rejects.toThrow(/500/)
  })

  it('returns the parsed catalog on success', async () => {
    const payload = { series: { asin: 'S1', name: "Seeker's Tale" }, totalBooks: 1, books: [] }
    stubFetch(
      new Response(JSON.stringify(payload), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )
    const { apiService } = await loadApi()

    await expect(apiService.getSeriesCatalog("Seeker's Tale", 'us')).resolves.toMatchObject({
      series: { name: "Seeker's Tale" },
    })
  })
})
