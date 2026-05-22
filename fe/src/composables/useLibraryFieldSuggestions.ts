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
import { computed, type ComputedRef } from 'vue'
import { useLibraryStore } from '@/stores/library'

/**
 * Aggregates the unique values already present in the user's library for each of the
 * free-text tag-style fields a curator might want to autocomplete: authors,
 * narrators, genres, and tags. The four lists are computed from the in-memory
 * library store, so they update whenever audiobooks are added, edited, or
 * removed — no separate fetch.
 *
 * De-duplication is case-insensitive (so "Brandon Sanderson" and
 * "brandon sanderson" become one entry, keeping the first casing seen so the
 * user sees a real-looking name). Whitespace is trimmed, blanks are dropped, and
 * the result is sorted lexicographically so the dropdown has a stable order.
 */
export interface LibraryFieldSuggestions {
  authors: ComputedRef<string[]>
  narrators: ComputedRef<string[]>
  genres: ComputedRef<string[]>
  tags: ComputedRef<string[]>
}

export function useLibraryFieldSuggestions(): LibraryFieldSuggestions {
  const libraryStore = useLibraryStore()

  function collectUnique(getter: (book: { [key: string]: unknown }) => unknown): string[] {
    const seen = new Map<string, string>() // normalized -> first-seen original casing
    for (const book of libraryStore.audiobooks) {
      const raw = getter(book as unknown as { [key: string]: unknown })
      if (!Array.isArray(raw)) continue
      for (const value of raw) {
        if (typeof value !== 'string') continue
        const trimmed = value.trim()
        if (!trimmed) continue
        const key = trimmed.toLowerCase()
        if (!seen.has(key)) seen.set(key, trimmed)
      }
    }
    return Array.from(seen.values()).sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base' }))
  }

  const authors = computed(() => collectUnique((b) => b.authors))
  const narrators = computed(() => collectUnique((b) => b.narrators))
  const genres = computed(() => collectUnique((b) => b.genres))
  const tags = computed(() => collectUnique((b) => b.tags))

  return { authors, narrators, genres, tags }
}
