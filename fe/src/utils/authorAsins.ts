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

/**
 * Author ASINs carried by a catalog result's `authors: [{ name, asin }]` array,
 * in order, de-duplicated, blanks dropped. Sent alongside a relabel so the
 * record's AuthorAsins move with its Authors instead of keeping the old
 * author's ASIN (which made the author page resolve to the wrong person).
 */
export function collectAuthorAsins(
  authors: ReadonlyArray<{ asin?: string | null } | null | undefined> | null | undefined,
): string[] {
  const seen = new Set<string>()
  const result: string[] = []
  for (const author of authors ?? []) {
    const asin = author?.asin?.trim()
    if (!asin || seen.has(asin)) continue
    seen.add(asin)
    result.push(asin)
  }
  return result
}
