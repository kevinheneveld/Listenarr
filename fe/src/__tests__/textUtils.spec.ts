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
import { describe, expect, it } from 'vitest'
import {
  decodeHtmlEntities,
  stripHtmlAndNormalize,
  normalizeCollectionText,
  isNonPersonNarrator,
} from '@/utils/textUtils'

describe('textUtils', () => {
  it('decodes common named HTML entities', () => {
    expect(decodeHtmlEntities('Tom &amp; Jerry &quot;Test&quot;')).toBe('Tom & Jerry "Test"')
    expect(decodeHtmlEntities('Rock&nbsp;&amp;&nbsp;Roll')).toBe('Rock & Roll')
  })

  it('decodes numeric HTML entities', () => {
    expect(decodeHtmlEntities('&#39;Hello&#39;')).toBe("'Hello'")
    expect(decodeHtmlEntities('&#x41;&#x42;&#x43;')).toBe('ABC')
  })

  it('leaves unknown entities unchanged', () => {
    expect(decodeHtmlEntities('Hello &notarealentity;')).toBe('Hello &notarealentity;')
  })

  it('strips html and normalizes whitespace', () => {
    expect(stripHtmlAndNormalize('<p>Hello&nbsp;<strong>world</strong></p><br>Next')).toBe(
      'Hello world\n\nNext',
    )
  })
})

describe('normalizeCollectionText', () => {
  it('lowercases, strips punctuation and diacritics, collapses whitespace', () => {
    expect(normalizeCollectionText('  R.C. Bray  ')).toBe('r c bray')
    expect(normalizeCollectionText('Brandon Sanderson')).toBe('brandon sanderson')
    expect(normalizeCollectionText('Tomás de Torquemada')).toBe('tomas de torquemada')
  })

  it('buckets casing/punctuation variants to the same key', () => {
    expect(normalizeCollectionText('full cast')).toBe(normalizeCollectionText('Full Cast'))
    expect(normalizeCollectionText('R. C. Bray')).toBe(normalizeCollectionText('r c bray'))
    expect(normalizeCollectionText('Tim  Gerard   Reynolds')).toBe('tim gerard reynolds')
  })

  it('returns empty string for blank/nullish input', () => {
    expect(normalizeCollectionText(undefined)).toBe('')
    expect(normalizeCollectionText('   ')).toBe('')
    expect(normalizeCollectionText('!!!')).toBe('')
  })
})

describe('isNonPersonNarrator', () => {
  it('treats production/ensemble credits as non-person', () => {
    expect(isNonPersonNarrator('Full Cast')).toBe(true)
    expect(isNonPersonNarrator('A Full Cast')).toBe(true)
    expect(isNonPersonNarrator('Full Cast Production')).toBe(true)
    expect(isNonPersonNarrator('various')).toBe(true)
    expect(isNonPersonNarrator('uncredited')).toBe(true)
  })

  it('treats blank/nullish credits as non-person', () => {
    expect(isNonPersonNarrator('')).toBe(true)
    expect(isNonPersonNarrator(undefined)).toBe(true)
  })

  it('keeps real people', () => {
    expect(isNonPersonNarrator('Ray Porter')).toBe(false)
    expect(isNonPersonNarrator('Tim Gerard Reynolds')).toBe(false)
  })
})
