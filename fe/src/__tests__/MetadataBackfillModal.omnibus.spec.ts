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

// A 2-hour story file in the user's library.
const STORY_FILE = {
  id: 22,
  title: 'A Story From The Omnibus',
  authors: ['Some Author'],
  narrators: [] as string[],
  runtime: 120, // 2 hours in minutes
  asin: 'B0OMNISTORY',
  files: [] as Array<{ id: number; path: string }>,
}

// Same length book — runtime matches the file, no omnibus suspicion.
const STANDALONE_METADATA = {
  metadata: {
    asin: 'B0OMNISTORY',
    title: 'A Story From The Omnibus',
    authors: [{ name: 'Some Author' }],
    lengthMinutes: 125, // similar to current
  },
}

// The omnibus that contains the story — much longer.
const OMNIBUS_METADATA = {
  metadata: {
    asin: 'B0OMNIBOOK1',
    title: 'The Complete Collection',
    subtitle: 'Books 1-5',
    authors: [{ name: 'Some Author' }],
    narrators: [{ name: 'A Narrator' }],
    series: 'The Collection',
    description: 'All five novels in one volume.',
    imageUrl: 'https://example.com/omnibus.jpg',
    lengthMinutes: 720, // 12 hours — 6× the file
  },
}

describe('MetadataBackfillModal — omnibus detection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    document.body.innerHTML = ''
  })

  it('does NOT trigger when fresh runtime is close to current runtime', async () => {
    apiMocks.getAudibleMetadata.mockResolvedValue(STANDALONE_METADATA)

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: STORY_FILE },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    expect(document.body.textContent).not.toContain(
      'This Audible record is much longer than your file',
    )

    wrapper.unmount()
  })

  it('triggers when fresh runtime is ≥3× current runtime, shows banner', async () => {
    apiMocks.getAudibleMetadata.mockResolvedValue(OMNIBUS_METADATA)

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: STORY_FILE },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    // Banner is visible with the runtime comparison.
    const banner = document.body.querySelector('.omnibus-banner')
    expect(banner, 'expected omnibus banner element').toBeTruthy()
    expect(banner!.textContent).toContain('much longer than your file')
    // 720m = 12h, 120m = 2h
    expect(banner!.textContent).toContain('12h')
    expect(banner!.textContent).toContain('2h')

    wrapper.unmount()
  })

  it('pre-unchecks Title, Subtitle, and Runtime when the omnibus heuristic fires', async () => {
    apiMocks.getAudibleMetadata.mockResolvedValue(OMNIBUS_METADATA)

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: STORY_FILE },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    // Find each row in the compare table; check the checkbox state for the
    // sticky fields. The book's current values for Title/Subtitle/Runtime
    // are present (Title) or empty (Subtitle) — the heuristic should
    // override the "empty → default ON" rule for these three.
    const rows = Array.from(document.body.querySelectorAll('.compare-table tbody tr'))
    const findRowByField = (fieldLabel: string) =>
      rows.find((r) => r.querySelector('.col-field')?.textContent?.trim().startsWith(fieldLabel))

    const titleRow = findRowByField('Title')
    const subtitleRow = findRowByField('Subtitle')
    const runtimeRow = findRowByField('Runtime')

    expect(titleRow, 'Title row missing').toBeTruthy()
    expect(subtitleRow, 'Subtitle row missing').toBeTruthy()
    expect(runtimeRow, 'Runtime row missing').toBeTruthy()

    expect((titleRow!.querySelector('input[type=checkbox]') as HTMLInputElement)?.checked).toBe(false)
    expect((subtitleRow!.querySelector('input[type=checkbox]') as HTMLInputElement)?.checked).toBe(false)
    expect((runtimeRow!.querySelector('input[type=checkbox]') as HTMLInputElement)?.checked).toBe(false)

    // And the *other* empty-on-the-book fields stay defaulted ON — so the
    // user still gets the cover/description/etc. without manually
    // re-checking them. (`series` in mapFresh expects the Audible array
    // shape `[{ name, position }]` rather than a plain string, so we
    // check description and Cover art here for the "stays ON" half.)
    const descriptionRow = findRowByField('Description')
    const coverRow = findRowByField('Cover art')
    expect((descriptionRow!.querySelector('input[type=checkbox]') as HTMLInputElement)?.checked).toBe(true)
    expect((coverRow!.querySelector('input[type=checkbox]') as HTMLInputElement)?.checked).toBe(true)

    wrapper.unmount()
  })

  it('does NOT trigger when the book has no current runtime (can\'t compute a ratio)', async () => {
    apiMocks.getAudibleMetadata.mockResolvedValue(OMNIBUS_METADATA)

    const bookWithoutRuntime = { ...STORY_FILE, runtime: undefined }

    const wrapper = mount(MetadataBackfillModal, {
      attachTo: document.body,
      props: { visible: false, audiobook: bookWithoutRuntime },
    })
    await wrapper.setProps({ visible: true })
    await flushPromises()

    expect(document.body.querySelector('.omnibus-banner')).toBeNull()

    wrapper.unmount()
  })
})
