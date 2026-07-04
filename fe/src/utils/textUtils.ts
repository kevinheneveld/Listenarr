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
const NAMED_HTML_ENTITIES: Record<string, string> = {
  amp: '&',
  lt: '<',
  gt: '>',
  quot: '"',
  apos: "'",
  nbsp: ' ',
  ndash: '-',
  mdash: '-',
  lsquo: "'",
  rsquo: "'",
  ldquo: '"',
  rdquo: '"',
  hellip: '...',
}

function decodeNumericEntity(entityBody: string): string | null {
  const isHex = entityBody.startsWith('#x') || entityBody.startsWith('#X')
  const digits = entityBody.slice(isHex ? 2 : 1)
  if (!digits) return null

  const codePoint = Number.parseInt(digits, isHex ? 16 : 10)
  if (!Number.isFinite(codePoint) || codePoint < 0 || codePoint > 0x10ffff) {
    return null
  }

  try {
    return String.fromCodePoint(codePoint)
  } catch {
    return null
  }
}

/**
 * Decodes a small, common subset of HTML entities without using `innerHTML`.
 * This avoids DOM-based HTML re-interpretation and satisfies CodeQL's DOM XSS rule.
 */
export function decodeHtmlEntities(text: string): string {
  if (!text) return text

  return text.replace(/&(#(?:x|X)?[0-9a-fA-F]+|[a-zA-Z][a-zA-Z0-9]+);/g, (match, entityBody) => {
    if (entityBody.startsWith('#')) {
      return decodeNumericEntity(entityBody) ?? match
    }

    const decoded = NAMED_HTML_ENTITIES[entityBody.toLowerCase()]
    return decoded ?? match
  })
}

/**
 * Safely renders text that might contain HTML entities
 * @param text - The text to render
 * @returns The decoded text
 */
export function safeText(text: unknown): string {
  if (text === null || text === undefined) return ''
  if (typeof text === 'string') return decodeHtmlEntities(text)
  if (typeof text === 'number' || typeof text === 'boolean' || typeof text === 'bigint') {
    return String(text)
  }
  if (Array.isArray(text)) {
    return text
      .map((item) => safeText(item))
      .filter((item) => item.length > 0)
      .join(', ')
  }
  if (typeof text === 'object') {
    const obj = text as Record<string, unknown>
    const candidate = obj.name ?? obj.title ?? obj.value ?? obj.label
    if (candidate !== undefined && candidate !== null) return safeText(candidate)
    return ''
  }
  return ''
}

/**
 * Strips HTML tags and normalizes whitespace/newlines into safe plain text.
 */
export function stripHtmlAndNormalize(text: string | undefined | null): string {
  if (!text) return ''

  const withBreaks = text
    .replace(/<\s*br\s*\/?>/gi, '\n')
    .replace(/<\/\s*p\s*>/gi, '\n')
    .replace(/<\/\s*div\s*>/gi, '\n')
    .replace(/<\/\s*li\s*>/gi, '\n')

  const container = document.createElement('div')
  container.innerHTML = withBreaks

  const raw = (container.textContent || container.innerText || '')
    .replace(/\u00a0/g, ' ')
    .replace(/\r\n?/g, '\n')
    .replace(/[ \t\f\v]+/g, ' ')
    .replace(/\n{3,}/g, '\n\n')
    .trim()

  return decodeHtmlEntities(raw)
}

/**
 * Case-, accent- and punctuation-insensitive key for grouping collection names
 * (narrators, authors) so casing/punctuation variants merge into one card.
 */
export function normalizeCollectionText(value: string | undefined | null): string {
  if (!value) return ''
  return value
    .normalize('NFKD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, ' ')
    .trim()
}

// Narrator credits that name a production or ensemble rather than a browsable
// person (a dramatized book can list "Full Cast" alongside ten real narrators).
// Compared against normalizeCollectionText output.
const NON_PERSON_NARRATOR_TOKENS = new Set([
  'full cast',
  'a full cast',
  'full cast production',
  'full cast dramatization',
  'various',
  'various narrators',
  'uncredited',
])

/**
 * True when a narrator credit isn't a real, browsable person (e.g. "Full Cast").
 * Used to exclude such tokens from narrator grouping so the library doesn't
 * sprout pseudo-narrator pages. Empty/blank credits count as non-person too.
 */
export function isNonPersonNarrator(name: string | undefined | null): boolean {
  const normalized = normalizeCollectionText(name)
  if (!normalized) return true
  if (NON_PERSON_NARRATOR_TOKENS.has(normalized)) return true
  // Catch "... full cast ..." phrasings not in the explicit set.
  return normalized.includes('full cast')
}
