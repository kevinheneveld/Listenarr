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
import { describe, it, beforeEach, expect, vi } from 'vitest'
import SeriesTriageView from '@/views/library/SeriesTriageView.vue'
import type { SeriesTriageResponse } from '@/types'

const triageResponse: SeriesTriageResponse = {
  summary: {
    candidates: 3,
    dismissed: 0,
    ownedBooksInCandidates: 47,
    singleBook: 1,
    withCatalog: 2,
  },
  rows: [
    {
      name: 'Jack Reacher',
      seriesAsin: 'B00REACHER',
      authors: ['Lee Child'],
      owned: 26,
      missingTracked: 28,
      catalogTotal: 97,
      completion: 0.268,
      editions: 1,
      dismissed: false,
      dismissedAt: null,
      note: null,
    },
    {
      name: 'The Dresden Files',
      seriesAsin: null,
      authors: ['Jim Butcher'],
      owned: 20,
      missingTracked: 8,
      catalogTotal: 35,
      completion: 0.571,
      editions: 1,
      dismissed: false,
      dismissedAt: null,
      note: null,
    },
    {
      name: 'Lonely Single',
      seriesAsin: null,
      authors: ['Solo Author'],
      owned: 1,
      missingTracked: 0,
      catalogTotal: null,
      completion: null,
      editions: null,
      dismissed: false,
      dismissedAt: null,
      note: null,
    },
  ],
}

// vi.mock factories are hoisted above imports, so the shared mock object must be
// created via vi.hoisted (it also returns the response deep-copied per call so
// row mutations inside the component never leak between tests).
const api = vi.hoisted(() => ({
  getSeriesTriage: vi.fn(),
  dismissSeries: vi.fn(),
  undismissSeries: vi.fn(),
  monitorSeries: vi.fn(),
}))

vi.mock('@/services/api', () => ({ apiService: api }))
vi.mock('@/services/toastService', () => ({
  useToast: () => ({ success: vi.fn(), warning: vi.fn(), error: vi.fn(), info: vi.fn() }),
}))
vi.mock('@/utils/logger', () => ({ logger: { error: vi.fn(), warn: vi.fn(), debug: vi.fn() } }))
vi.mock('@/services/errorTracking', () => ({ errorTracking: { captureException: vi.fn() } }))

function mountView() {
  return mount(SeriesTriageView, {
    global: {
      stubs: {
        RouterLink: { template: '<a class="stub-link"><slot /></a>' },
      },
    },
  })
}

describe('SeriesTriageView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.getSeriesTriage.mockImplementation(async () => structuredClone(triageResponse))
    api.dismissSeries.mockImplementation(async (payload: { seriesName: string }) => ({
      seriesName: payload.seriesName,
      decision: 'dismissed',
    }))
    api.undismissSeries.mockImplementation(async (seriesName: string) => ({
      seriesName,
      removed: true,
    }))
    api.monitorSeries.mockImplementation(async () => ({
      message: 'Series monitoring enabled',
      monitoredSeries: { id: 1 },
      addedCount: 4,
      existingCount: 2,
      failedCount: 0,
    }))
    try {
      localStorage.removeItem('listenarr.seriesTriage.hideSingle')
    } catch {
      /* ignore */
    }
  })

  it('renders the candidate series with ownership counts and the summary', async () => {
    const wrapper = mountView()
    await flushPromises()

    const rows = wrapper.findAll('[data-testid="triage-row"]')
    expect(rows).toHaveLength(3)
    expect(rows[0].text()).toContain('Jack Reacher')
    expect(rows[0].text()).toContain('Lee Child')
    expect(rows[0].text()).toContain('26')
    expect(rows[0].text()).toContain('/97')
    expect(rows[2].text()).toContain('no catalog yet')
    expect(wrapper.find('[data-testid="triage-summary"]').text()).toContain('3 series')
    expect(wrapper.find('[data-testid="triage-summary"]').text()).toContain('47 owned books')
  })

  it('"Not interested" dismisses the series by name and removes the row', async () => {
    const wrapper = mountView()
    await flushPromises()

    await wrapper.findAll('[data-testid="triage-dismiss"]')[1].trigger('click')
    await flushPromises()

    expect(api.dismissSeries).toHaveBeenCalledWith({
      seriesName: 'The Dresden Files',
      seriesAsin: null,
    })
    const names = wrapper.findAll('[data-testid="triage-row"]').map((r) => r.text())
    expect(names.some((t) => t.includes('The Dresden Files'))).toBe(false)
    expect(names).toHaveLength(2)
  })

  it('"Collect" monitors the series with its name and ASIN', async () => {
    const wrapper = mountView()
    await flushPromises()

    await wrapper.findAll('[data-testid="triage-collect"]')[0].trigger('click')
    await flushPromises()

    expect(api.monitorSeries).toHaveBeenCalledWith({ name: 'Jack Reacher', asin: 'B00REACHER' })
    expect(api.undismissSeries).not.toHaveBeenCalled()
    expect(wrapper.findAll('[data-testid="triage-row"]')).toHaveLength(2)
  })

  it('"Hide single-book series" filters series with one owned book and persists', async () => {
    const wrapper = mountView()
    await flushPromises()

    await wrapper.find('[data-testid="triage-hide-single"]').setValue(true)
    await flushPromises()

    expect(wrapper.findAll('[data-testid="triage-row"]')).toHaveLength(2)
    expect(localStorage.getItem('listenarr.seriesTriage.hideSingle')).toBe('1')
  })

  it('bulk "Not interested" dismisses every selected series', async () => {
    const wrapper = mountView()
    await flushPromises()

    const checks = wrapper.findAll('[data-testid="triage-row"] input[type="checkbox"]')
    await checks[0].setValue(true)
    await checks[2].setValue(true)
    await wrapper.find('[data-testid="triage-bulk-dismiss"]').trigger('click')
    await flushPromises()

    expect(api.dismissSeries).toHaveBeenCalledTimes(2)
    expect(wrapper.findAll('[data-testid="triage-row"]')).toHaveLength(1)
  })
})
