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
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import TagAutocompleteInput from '@/components/form/TagAutocompleteInput.vue'

const ROSTER = [
  'Robert Jordan',
  'Brandon Sanderson',
  'Rosamund Pike',
  'Kate Reading',
  'Michael Kramer',
  'Jack Garrett',
]

describe('TagAutocompleteInput', () => {
  it('shows substring matches from the suggestions list as the user types', async () => {
    const wrapper = mount(TagAutocompleteInput, {
      props: { suggestions: ROSTER },
    })

    const input = wrapper.get('input')
    await input.trigger('focus')
    await input.setValue('Ros')

    const options = wrapper.findAll('.tag-autocomplete-option')
    const labels = options.map((o) => o.text())
    expect(labels).toContain('Rosamund Pike')
    expect(labels).not.toContain('Robert Jordan')
  })

  it('matches punctuation-insensitively (smart quotes, hyphens, etc.)', async () => {
    const wrapper = mount(TagAutocompleteInput, {
      props: { suggestions: ['Robert "Bob" Jordan', "O'Brien"] },
    })
    const input = wrapper.get('input')
    await input.trigger('focus')
    await input.setValue('Obrien')

    const labels = wrapper.findAll('.tag-autocomplete-option').map((o) => o.text())
    expect(labels).toContain("O'Brien")
  })

  it('excludes already-added values from the dropdown', async () => {
    const wrapper = mount(TagAutocompleteInput, {
      props: { suggestions: ROSTER, excluded: ['Rosamund Pike'] },
    })
    const input = wrapper.get('input')
    await input.trigger('focus')
    await input.setValue('Ros')

    const labels = wrapper.findAll('.tag-autocomplete-option').map((o) => o.text())
    expect(labels).not.toContain('Rosamund Pike')
  })

  it('clicking a suggestion emits add with that value and clears the input', async () => {
    const wrapper = mount(TagAutocompleteInput, {
      props: { suggestions: ROSTER },
    })
    const input = wrapper.get<HTMLInputElement>('input')
    await input.trigger('focus')
    await input.setValue('Sander')

    await wrapper.find('.tag-autocomplete-option').trigger('mousedown')

    const events = wrapper.emitted('add')
    expect(events).toBeTruthy()
    expect(events![0]).toEqual(['Brandon Sanderson'])
    expect(input.element.value).toBe('')
  })

  it('pressing Enter with no highlighted suggestion commits the raw typed value (lets the user add a brand-new tag)', async () => {
    const wrapper = mount(TagAutocompleteInput, {
      props: { suggestions: ROSTER },
    })
    const input = wrapper.get<HTMLInputElement>('input')
    await input.trigger('focus')
    await input.setValue('A New Author Not In Library')
    await input.trigger('keydown', { key: 'Enter' })

    expect(wrapper.emitted('add')?.[0]).toEqual(['A New Author Not In Library'])
  })

  it('arrow keys navigate the dropdown and Enter commits the highlighted entry', async () => {
    const wrapper = mount(TagAutocompleteInput, {
      props: { suggestions: ROSTER },
    })
    const input = wrapper.get('input')
    await input.trigger('focus')
    await input.setValue('a') // matches Brandon, Kate Reading, Jack Garrett (all contain 'a')

    // Step to second match
    await input.trigger('keydown', { key: 'ArrowDown' })
    await input.trigger('keydown', { key: 'ArrowDown' })
    await input.trigger('keydown', { key: 'Enter' })

    const events = wrapper.emitted('add')
    expect(events).toBeTruthy()
    expect(events![0][0]).toBeTypeOf('string')
    // Whichever match landed at index 1, it should be one of the matching candidates.
    expect(ROSTER).toContain(events![0][0])
  })
})
