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

// updateAudiobook forwards its options.cacheImageLocally as a query parameter
// so the Audiobook DTO model-binder isn't polluted with a non-domain flag.

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('ApiService.updateAudiobook', () => {
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('does not append cacheImageLocally when no options are passed', async () => {
    vi.resetModules()
    const fetchMock = vi.fn(() =>
      Promise.resolve(jsonResponse({ message: 'ok', audiobook: { id: 5 } })),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    await actual.apiService.updateAudiobook(5, { title: 'New' })

    expect(fetchMock).toHaveBeenCalledTimes(1)
    const [url] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain('/library/5')
    expect(url).not.toContain('cacheImageLocally')
  })

  it('appends ?cacheImageLocally=true when the caller opts in', async () => {
    vi.resetModules()
    const fetchMock = vi.fn(() =>
      Promise.resolve(jsonResponse({ message: 'ok', audiobook: { id: 5 } })),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    await actual.apiService.updateAudiobook(
      5,
      { imageUrl: 'https://example.com/cover.jpg' },
      { cacheImageLocally: true },
    )

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(url).toContain('/library/5')
    expect(url).toContain('cacheImageLocally=true')
    // The flag must not leak into the body — it's a request modifier, not
    // a domain field.
    const body = JSON.parse(String(init.body))
    expect(body).toEqual({ imageUrl: 'https://example.com/cover.jpg' })
    expect(body.cacheImageLocally).toBeUndefined()
  })

  it('omits the query parameter when cacheImageLocally is explicitly false', async () => {
    vi.resetModules()
    const fetchMock = vi.fn(() =>
      Promise.resolve(jsonResponse({ message: 'ok', audiobook: { id: 5 } })),
    )
    vi.stubGlobal('fetch', fetchMock)

    const actual = await vi.importActual<typeof import('@/services/api')>('@/services/api')
    await actual.apiService.updateAudiobook(5, { title: 'Whatever' }, { cacheImageLocally: false })

    const [url] = fetchMock.mock.calls[0] as [string, RequestInit]
    // false is the backend default anyway — sending an explicit ?cacheImageLocally=false
    // would just be noise on the wire.
    expect(url).not.toContain('cacheImageLocally')
  })
})
