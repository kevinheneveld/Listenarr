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
import { collectAuthorAsins } from '@/utils/authorAsins'

describe('collectAuthorAsins', () => {
  it('collects the ASINs a picked catalog candidate carries, in order and de-duplicated', () => {
    expect(
      collectAuthorAsins([
        { asin: 'B0FONDA001' },
        { asin: ' B0FONDA001 ' },
        { asin: 'B0OTHER001' },
      ]),
    ).toEqual(['B0FONDA001', 'B0OTHER001'])
  })

  it('drops blanks and tolerates missing arrays so a relabel can still clear stale ASINs', () => {
    expect(collectAuthorAsins([{ asin: '' }, { asin: null }, null, undefined])).toEqual([])
    expect(collectAuthorAsins(undefined)).toEqual([])
    expect(collectAuthorAsins(null)).toEqual([])
  })
})
