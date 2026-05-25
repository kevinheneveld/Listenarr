<!--
  Listenarr - Audiobook Management System
  Copyright (C) 2024-2026 Listenarr Contributors

  This program is free software: you can redistribute it and/or modify
  it under the terms of the GNU Affero General Public License as published
  by the Free Software Foundation, either version 3 of the License, or
  (at your option) any later version.

  This program is distributed in the hope that it will be useful,
  but WITHOUT ANY WARRANTY; without even the implied warranty of
  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
  GNU Affero General Public License for more details.

  You should have received a copy of the GNU Affero General Public License
  along with this program. If not, see <https://www.gnu.org/licenses/>.
-->
<template>
  <transition name="modal-fade">
    <div v-if="visible" class="modal-overlay" @click.self="onClose">
      <div class="modal duplicates-modal" role="dialog" aria-modal="true" aria-labelledby="dup-modal-title">
        <header class="modal-header">
          <h2 id="dup-modal-title">Review duplicate audiobooks</h2>
          <button class="modal-close" :disabled="merging" @click="onClose" aria-label="Close">
            <PhX />
          </button>
        </header>

        <div class="modal-body">
          <div v-if="loading" class="state-msg">Loading duplicate groups…</div>

          <div v-else-if="loadError" class="state-msg error">
            Failed to load duplicates: {{ loadError }}
          </div>

          <div v-else-if="groups.length === 0" class="state-msg">
            No duplicate audiobook rows found.
          </div>

          <template v-else>
            <!-- Tabs: ASIN dedup (the original detection) vs Title/Author
                 collisions (rows that resolve to the same canonical folder
                 path under FolderNamingPattern but have distinct ASINs).
                 The two passes have different action sets so they're rendered
                 as siblings — both visible under "All", filtered under each
                 specific tab. -->
            <div class="dup-tabs" role="tablist" aria-label="Duplicate kinds">
              <button
                v-for="t in tabs"
                :key="t.value"
                type="button"
                role="tab"
                class="dup-tab"
                :class="{ active: activeTab === t.value }"
                :aria-selected="activeTab === t.value"
                :disabled="t.count === 0 && t.value !== 'all'"
                @click="activeTab = t.value"
              >
                {{ t.label }}
                <span class="dup-tab-count">{{ t.count }}</span>
              </button>
            </div>

            <p v-if="activeTab !== 'title_author'" class="help-text">
              <strong>By ASIN:</strong> every row starts as <strong>Skip</strong>. Pick an action per row:
              <strong>Keep</strong> (this row survives) →
              <strong>Discard</strong> (delete the row, its files, and its folder from disk; references move to the Keep row) →
              <strong>Clear ASIN</strong> (keep the row and its files — just un-dupe it).
              The summary below tracks what'll be deleted; you'll get one final confirmation
              before anything is applied.
            </p>
            <p v-if="activeTab !== 'asin'" class="help-text">
              <strong>By title/author:</strong> these rows have distinct ASINs but resolve to the same
              <code>{Author}/{Title}/…</code> folder — usually edition variants, narrator variants,
              or wishlist duplicates. Pick <strong>Discard</strong> on the row(s) you want removed;
              keep at least one row marked <strong>Skip</strong> if you want Listenarr to keep
              searching for the book. Discarding every row in a group will stop tracking that book
              entirely (you'll be warned in the confirmation panel). The
              <span class="inline-narrator-legend">🎙 narrator chip</span> is highlighted orange when
              rows in the same group don't agree on their narrator — a strong hint they're different
              readings of the same book, not redundant wishlist records.
            </p>

            <div v-if="visibleGroups.length === 0" class="state-msg">
              <span v-if="activeTab === 'asin'">No same-ASIN duplicate audiobook rows found.</span>
              <span v-else-if="activeTab === 'title_author'">No title/author collisions found.</span>
              <span v-else>No duplicates found in this view.</span>
            </div>

            <div v-for="group in visibleGroups" :key="groupKey(group)" class="group">
              <div class="group-header">
                <span v-if="isAsinGroup(group)" class="group-asin">ASIN {{ group.normalizedAsin }}</span>
                <span v-else class="group-asin group-collision" :title="group.collisionKey || ''">
                  Same folder · {{ shortenPath(group.collisionKey || '') }}
                </span>
                <span class="group-meta">{{ group.rows.length }} rows</span>
              </div>
              <p v-if="group.recommendationReason" class="group-reason">
                <strong>Suggestion:</strong> {{ group.recommendationReason }}
              </p>

              <div
                v-for="row in group.rows"
                :key="row.id"
                class="row"
                :class="{
                  'row-keep': rowActions[row.id] === 'keep',
                  'row-merge': rowActions[row.id] === 'merge',
                  'row-clear': rowActions[row.id] === 'clearAsin',
                }"
              >
                <div class="row-head">
                  <div class="row-thumb">
                    <img
                      v-if="row.imageUrl"
                      :src="protectedSrc(row)"
                      :alt="row.title || 'cover'"
                      loading="lazy"
                      @error="onThumbError"
                    />
                    <div v-else class="row-thumb-placeholder">—</div>
                  </div>

                  <div class="row-main">
                    <div class="row-title">
                      <span class="title-text">{{ safeText(row.title) || '(no title)' }}</span>
                      <span v-if="row.recommendedWinner" class="badge-suggested" :title="group.recommendationReason">
                        suggested
                      </span>
                      <a
                        class="row-link"
                        :href="`/audiobooks/${row.id}`"
                        target="_blank"
                        rel="noopener"
                        title="Open audiobook detail in a new tab"
                        @click.stop
                      >
                        open in new tab ↗
                      </a>
                    </div>
                    <div class="row-sub">
                      <span v-if="row.series">{{ safeText(row.series) }}<span v-if="row.seriesNumber"> #{{ row.seriesNumber }}</span></span>
                      <span class="row-sep" v-if="row.series && row.basePath">·</span>
                      <span v-if="row.basePath" class="row-path">{{ row.basePath }}</span>
                    </div>
                    <div class="row-meta">
                      <span
                        v-if="row.narrators.length > 0"
                        class="meta-chip narrator-chip"
                        :class="{ 'narrator-mismatch': groupHasNarratorMismatch(group) }"
                        :title="row.narrators.length > 1
                          ? `Narrators: ${row.narrators.join(', ')}${groupHasNarratorMismatch(group) ? ' — differs from another row in this group' : ''}`
                          : `Narrator: ${row.narrators[0]}${groupHasNarratorMismatch(group) ? ' — differs from another row in this group' : ''}`"
                      >
                        🎙 {{ row.narrators[0] }}<span v-if="row.narrators.length > 1"> +{{ row.narrators.length - 1 }}</span>
                      </span>
                      <span
                        v-else
                        class="meta-chip narrator-chip narrator-missing"
                        :class="{ 'narrator-mismatch': groupHasNarratorMismatch(group) }"
                        :title="`No narrator metadata on this row${groupHasNarratorMismatch(group) ? ' — another row in this group has narrators listed' : ''}`"
                      >
                        🎙 (no narrator)
                      </span>
                      <span class="meta-chip" :class="{ on: row.hasAnyFile }">
                        {{ row.fileCount }} file{{ row.fileCount === 1 ? '' : 's' }}
                      </span>
                      <span
                        v-if="row.likelyDuplicateFileCount > 0"
                        class="meta-chip warn"
                        :title="`${row.likelyDuplicateFileCount} file${row.likelyDuplicateFileCount === 1 ? '' : 's'} on this row look like naming-variant duplicates of another file on the same row (e.g. ‘01 Title.mp3’ and ‘01. Title.mp3’).`"
                      >
                        {{ row.likelyDuplicateFileCount }} dupe{{ row.likelyDuplicateFileCount === 1 ? '' : 's' }}
                      </span>
                      <span class="meta-chip" :class="{ on: row.hasBookFolder }">
                        {{ row.hasBookFolder ? 'real folder' : 'no book folder' }}
                      </span>
                      <span v-if="row.totalSize > 0" class="meta-chip">{{ formatBytes(row.totalSize) }}</span>
                      <span v-if="row.runtime" class="meta-chip">{{ formatMinutes(row.runtime) }}</span>
                      <span class="meta-chip">id {{ row.id }}</span>
                    </div>
                  </div>

                  <div class="row-actions">
                    <label
                      v-for="opt in actionOptions(row, group)"
                      :key="opt.value"
                      class="action-pill"
                      :class="{
                        active: rowActions[row.id] === opt.value,
                        disabled: opt.disabled,
                      }"
                      :title="opt.tooltip"
                    >
                      <input
                        type="radio"
                        :name="`act-${row.id}`"
                        :value="opt.value"
                        :checked="rowActions[row.id] === opt.value"
                        :disabled="opt.disabled || merging"
                        @change="setRowAction(row, group, opt.value)"
                      />
                      <span>{{ opt.label }}</span>
                    </label>

                    <button
                      type="button"
                      class="expand-toggle"
                      :aria-expanded="expanded[row.id] ? 'true' : 'false'"
                      @click="expanded[row.id] = !expanded[row.id]"
                    >
                      <span v-if="expanded[row.id]">▾ details</span>
                      <span v-else>▸ details</span>
                    </button>
                  </div>
                </div>

                <div v-if="expanded[row.id]" class="row-details">
                  <div class="details-grid">
                    <div class="details-cell">
                      <div class="details-label">Authors</div>
                      <div class="details-value">{{ row.authors.join(', ') || '—' }}</div>
                    </div>
                    <div class="details-cell">
                      <div class="details-label">Narrators</div>
                      <div class="details-value">{{ row.narrators.join(', ') || '—' }}</div>
                    </div>
                    <div class="details-cell">
                      <div class="details-label">Runtime</div>
                      <div class="details-value">{{ row.runtime ? formatMinutes(row.runtime) : '—' }}</div>
                    </div>
                    <div class="details-cell">
                      <div class="details-label">Total size</div>
                      <div class="details-value">{{ row.totalSize > 0 ? formatBytes(row.totalSize) : '—' }}</div>
                    </div>
                  </div>

                  <div v-if="row.files.length > 0" class="files-list">
                    <div class="files-list-header">
                      <span>File</span><span>Size</span><span>Format</span><span>Bitrate</span><span>Duration</span>
                    </div>
                    <div v-for="f in row.files" :key="f.id" class="files-list-row">
                      <span class="file-path" :title="f.path || ''">{{ shortenPath(f.path) }}</span>
                      <span>{{ f.size ? formatBytes(f.size) : '—' }}</span>
                      <span>{{ f.format || f.codec || '—' }}</span>
                      <span>{{ f.bitrate ? `${Math.round(f.bitrate / 1000)} kbps` : '—' }}</span>
                      <span>{{ f.durationSeconds ? formatDuration(f.durationSeconds) : '—' }}</span>
                    </div>
                  </div>
                  <div v-else class="no-files">No tracked files on this row.</div>
                </div>
              </div>
            </div>
          </template>
        </div>

        <footer class="modal-footer">
          <!-- Default state: summary + Apply button -->
          <template v-if="!pendingConfirm">
            <div class="footer-summary">
              <span v-if="!loading && groups.length > 0">
                {{ plannedDeletions }} to discard · {{ plannedClears }} ASIN clear{{ plannedClears === 1 ? '' : 's' }}
                · {{ skippedCount }} group{{ skippedCount === 1 ? '' : 's' }} fully skipped
              </span>
              <span v-if="mergeError" class="error">{{ mergeError }}</span>
            </div>
            <div class="footer-actions">
              <button type="button" class="btn" :disabled="merging" @click="onClose">Cancel</button>
              <button
                type="button"
                class="btn btn-primary"
                :disabled="merging || loading || !canApply"
                @click="pendingConfirm = true"
              >
                Apply…
              </button>
            </div>
          </template>

          <!-- Confirmation state: surface the disk-impact summary explicitly
               so a misclick on Apply doesn't quietly nuke files. -->
          <template v-else>
            <div class="confirm-panel">
              <div class="confirm-title">About to apply these changes:</div>
              <ul class="confirm-list">
                <li v-if="plannedDeletions - plannedTitleAuthorDiscards > 0">
                  <strong>{{ plannedDeletions - plannedTitleAuthorDiscards }}</strong>
                  ASIN-group row{{ plannedDeletions - plannedTitleAuthorDiscards === 1 ? '' : 's' }} will be discarded —
                  {{ discardFileCount }} file{{ discardFileCount === 1 ? '' : 's' }}
                  ({{ formatBytes(discardTotalBytes) }}) and their folders will be deleted from disk.
                </li>
                <li v-if="plannedTitleAuthorDiscards > 0">
                  <strong>{{ plannedTitleAuthorDiscards }}</strong>
                  title/author row{{ plannedTitleAuthorDiscards === 1 ? '' : 's' }} will be deleted via per-row
                  <code>DELETE /library/&#123;id&#125;</code><span v-if="plannedTitleAuthorDiscardsWithFiles > 0">
                    — <strong>{{ plannedTitleAuthorDiscardsWithFiles }}</strong> of these
                    {{ plannedTitleAuthorDiscardsWithFiles === 1 ? 'has' : 'have' }} files on disk that will also be removed</span>.
                </li>
                <li v-if="plannedClears > 0">
                  <strong>{{ plannedClears }}</strong> row{{ plannedClears === 1 ? '' : 's' }} will have their ASIN cleared
                  (files on disk untouched).
                </li>
                <li v-if="skippedCount > 0">
                  <strong>{{ skippedCount }}</strong> group{{ skippedCount === 1 ? '' : 's' }} fully skipped.
                </li>
              </ul>
              <div
                v-if="fullDeleteTitleAuthorGroups.length > 0"
                class="confirm-warning confirm-warning-strong"
              >
                ⚠ <strong>{{ fullDeleteTitleAuthorGroups.length }}</strong>
                title/author group{{ fullDeleteTitleAuthorGroups.length === 1 ? ' has' : 's have' }}
                every row marked Discard — Listenarr will stop tracking and searching for:
                <ul class="confirm-warning-list">
                  <li v-for="g in fullDeleteTitleAuthorGroups" :key="g.title">
                    <em>{{ g.title }}</em> ({{ g.rowCount }} row{{ g.rowCount === 1 ? '' : 's' }})
                  </li>
                </ul>
                To keep being monitored, leave at least one row in each group set to <strong>Skip</strong>.
              </div>
              <div class="confirm-warning">
                Disk deletions cannot be undone from the app. Make sure your backups are current.
              </div>
              <div class="confirm-actions">
                <button type="button" class="btn" :disabled="merging" @click="pendingConfirm = false">
                  Back
                </button>
                <button
                  type="button"
                  class="btn btn-danger"
                  :disabled="merging"
                  @click="executeMerge"
                >
                  {{ merging ? 'Applying…' : 'Confirm and apply' }}
                </button>
              </div>
              <div v-if="mergeError" class="error confirm-error">{{ mergeError }}</div>
            </div>
          </template>
        </footer>
      </div>
    </div>
  </transition>
</template>

<script setup lang="ts">
import { ref, computed, watch, reactive } from 'vue'
import { PhX } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import { useProtectedImages } from '@/composables/useProtectedImages'
import { safeText } from '@/utils/textUtils'
import { errorTracking } from '@/services/errorTracking'
import type {
  DuplicateGroup,
  DuplicateRow,
  DuplicateRowAction,
  DuplicatesMergePair,
  MergeDuplicatesResult,
} from '@/types'

const props = defineProps<{ visible: boolean }>()
const emit = defineEmits<{
  (e: 'close'): void
  (e: 'merged', result: MergeDuplicatesResult): void
}>()

const loading = ref(false)
const loadError = ref<string | null>(null)
const merging = ref(false)
const mergeError = ref<string | null>(null)
const groups = ref<DuplicateGroup[]>([])
const pendingConfirm = ref(false)
// Per-row action keyed by audiobook id.
const rowActions = reactive<Record<number, DuplicateRowAction>>({})
const expanded = reactive<Record<number, boolean>>({})
// Tab filter — 'asin' / 'title_author' / 'all'. Defaults intelligently after
// load() based on which buckets have content.
type DupTab = 'asin' | 'title_author' | 'all'
const activeTab = ref<DupTab>('asin')

function groupKindOf(g: DuplicateGroup): 'asin' | 'title_author' {
  // Older API responses omit `kind`; treat them as ASIN groups for back-compat.
  return g.kind === 'title_author' ? 'title_author' : 'asin'
}
function isAsinGroup(g: DuplicateGroup): boolean {
  return groupKindOf(g) === 'asin'
}
function groupKey(g: DuplicateGroup): string {
  // Stable v-for key — ASIN groups key on the ASIN, title/author groups on
  // the collision key (the shared canonical target path).
  return isAsinGroup(g) ? `asin:${g.normalizedAsin}` : `ta:${g.collisionKey || ''}`
}

/**
 * True when the rows in this group don't all agree on their narrator set.
 * Narrator isn't part of the search query or the match-confidence score, so
 * it's a useful signal that two rows might be different editions / readings
 * of the same book rather than redundant wishlist records. Empty / missing
 * narrators count as a distinct "set" from any populated set — surfacing the
 * "one row has narrator metadata, the other doesn't" case too.
 *
 * Cheap to compute per-render against the small row count in any group; no
 * memoization needed.
 */
function groupHasNarratorMismatch(g: DuplicateGroup): boolean {
  if (g.rows.length < 2) return false
  const sigs = g.rows.map((r) =>
    (r.narrators || [])
      .map((n) => (n || '').trim().toLowerCase())
      .filter((n) => n.length > 0)
      .sort()
      .join('|'),
  )
  return sigs.some((s) => s !== sigs[0])
}

const asinGroups = computed(() => groups.value.filter(isAsinGroup))
const titleAuthorGroups = computed(() => groups.value.filter((g) => !isAsinGroup(g)))
const visibleGroups = computed(() => {
  if (activeTab.value === 'asin') return asinGroups.value
  if (activeTab.value === 'title_author') return titleAuthorGroups.value
  return groups.value
})
const tabs = computed(() => [
  { value: 'asin' as DupTab, label: 'By ASIN', count: asinGroups.value.length },
  { value: 'title_author' as DupTab, label: 'By title/author', count: titleAuthorGroups.value.length },
  { value: 'all' as DupTab, label: 'All', count: groups.value.length },
])

const { getProtectedImageSrc } = useProtectedImages()

const plannedDeletions = computed(() => {
  let total = 0
  for (const g of groups.value) {
    for (const r of g.rows) {
      if (rowActions[r.id] === 'merge') total++
    }
  }
  return total
})
// Count of title/author Discards specifically, so the confirmation panel
// can describe them differently (no Keep row to fold references into; either
// 0 files or a destructive per-row delete).
const plannedTitleAuthorDiscards = computed(() => {
  let total = 0
  for (const g of groups.value) {
    if (isAsinGroup(g)) continue
    for (const r of g.rows) {
      if (rowActions[r.id] === 'merge') total++
    }
  }
  return total
})
const plannedTitleAuthorDiscardsWithFiles = computed(() => {
  let total = 0
  for (const g of groups.value) {
    if (isAsinGroup(g)) continue
    for (const r of g.rows) {
      if (rowActions[r.id] === 'merge' && r.fileCount > 0) total++
    }
  }
  return total
})
/**
 * Title/author groups where every row is marked Discard. Deleting every
 * row in such a group removes the book from Listenarr entirely — it stops
 * being monitored and won't be searched for again. Surface these as a
 * loud warning in the confirmation panel so the user doesn't accidentally
 * delete a book they wanted to keep tracking under one consolidated ASIN.
 */
const fullDeleteTitleAuthorGroups = computed(() => {
  const out: { title: string; rowCount: number }[] = []
  for (const g of groups.value) {
    if (isAsinGroup(g)) continue
    if (g.rows.length === 0) continue
    const allDiscarded = g.rows.every((r) => rowActions[r.id] === 'merge')
    if (allDiscarded) {
      out.push({
        title: g.rows[0].title || '(untitled)',
        rowCount: g.rows.length,
      })
    }
  }
  return out
})

const plannedClears = computed(() => {
  let total = 0
  for (const g of groups.value) {
    for (const r of g.rows) {
      if (rowActions[r.id] === 'clearAsin') total++
    }
  }
  return total
})

const skippedCount = computed(() => {
  let count = 0
  for (const g of groups.value) {
    const allSkip = g.rows.every((r) => (rowActions[r.id] || 'skip') === 'skip')
    if (allSkip) count++
  }
  return count
})

const canApply = computed(() => plannedDeletions.value > 0 || plannedClears.value > 0)

// Sum of files / bytes across rows currently marked for Discard inside
// ASIN groups (the bulk merge path). Title/author Discards are described
// separately in the confirmation panel since they don't fold-references-
// into-a-Keep-row — they're straight per-row deletes.
const discardFileCount = computed(() => {
  let n = 0
  for (const g of groups.value) {
    if (!isAsinGroup(g)) continue
    for (const r of g.rows) {
      if (rowActions[r.id] === 'merge') n += r.fileCount
    }
  }
  return n
})
const discardTotalBytes = computed(() => {
  let n = 0
  for (const g of groups.value) {
    if (!isAsinGroup(g)) continue
    for (const r of g.rows) {
      if (rowActions[r.id] === 'merge') n += r.totalSize
    }
  }
  return n
})

function protectedSrc(row: DuplicateRow): string {
  if (!row.imageUrl) return ''
  return getProtectedImageSrc(row.imageUrl, `dup:${row.id}`) || row.imageUrl
}

function onThumbError(e: Event) {
  const img = e.target as HTMLImageElement | null
  if (img) img.style.display = 'none'
}

interface ActionOption {
  value: DuplicateRowAction
  label: string
  disabled: boolean
  tooltip: string
}

function actionOptions(row: DuplicateRow, group: DuplicateGroup): ActionOption[] {
  // Title/author groups: distinct ASINs. The merge endpoint rejects
  // cross-ASIN attempts, so Keep / Clear ASIN don't apply. But Discard
  // *does* — it just deletes the audiobook row (and its files / folder if
  // the user opts in). Routes through DELETE /library/{id} per-row rather
  // than through the merge endpoint.
  if (!isAsinGroup(group)) {
    const hasFiles = row.fileCount > 0
    return [
      {
        value: 'skip',
        label: 'Skip',
        disabled: false,
        tooltip: 'Leave this row untouched.',
      },
      {
        // We reuse the internal value 'merge' for backward compatibility
        // with the existing per-row action state; the executeMerge() path
        // branches on group.kind to route ASIN-merge vs. straight delete.
        value: 'merge',
        label: 'Discard',
        disabled: false,
        tooltip: hasFiles
          ? `Delete this audiobook row AND its ${row.fileCount} file${row.fileCount === 1 ? '' : 's'} from disk. Destructive — use "open in new tab ↗" to verify first.`
          : 'Delete this audiobook row. No files on disk to remove (this is a monitored-but-not-downloaded record).',
      },
    ]
  }

  // Disable 'Keep' if another row in this group is already 'keep'.
  // Disable 'Discard' (internal value: 'merge') if no 'keep' row exists yet —
  // discarded rows reassign their FK refs into a winner, so a winner must
  // exist first.
  const keepRow = group.rows.find((r) => rowActions[r.id] === 'keep')
  const anotherIsKeep = keepRow != null && keepRow.id !== row.id
  const noKeepSelected = keepRow == null

  return [
    {
      value: 'skip',
      label: 'Skip',
      disabled: false,
      tooltip: 'Leave this row untouched.',
    },
    {
      value: 'keep',
      label: 'Keep',
      disabled: anotherIsKeep,
      tooltip: anotherIsKeep
        ? `Already keeping id ${keepRow!.id}. Switch that one first.`
        : 'This row survives. Any Discard rows will fold their references into it.',
    },
    {
      value: 'merge',
      label: 'Discard',
      disabled: noKeepSelected,
      tooltip: noKeepSelected
        ? 'Pick a Keep row in this group first.'
        : `Delete this row, its files, and its folder from disk. Downloads/History/MoveJobs references move to id ${keepRow!.id}. Empty parent folders are cleaned up too.`,
    },
    {
      value: 'clearAsin',
      label: 'Clear ASIN',
      disabled: false,
      tooltip: 'Keep the row, but null out its ASIN so it stops being detected as a duplicate.',
    },
  ]
}

function setRowAction(row: DuplicateRow, group: DuplicateGroup, action: DuplicateRowAction) {
  // When a row switches to 'keep', if another row was already 'keep', demote
  // that row back to 'skip' so there's at most one Keep per group at a time.
  if (action === 'keep') {
    for (const other of group.rows) {
      if (other.id !== row.id && rowActions[other.id] === 'keep') {
        rowActions[other.id] = 'skip'
      }
    }
  }
  // If the user moves away from 'keep' and other rows in the group are
  // currently 'merge', they'd become invalid (no winner). Demote them.
  if (action !== 'keep' && rowActions[row.id] === 'keep') {
    for (const other of group.rows) {
      if (other.id !== row.id && rowActions[other.id] === 'merge') {
        rowActions[other.id] = 'skip'
      }
    }
  }
  rowActions[row.id] = action
}

async function load() {
  loading.value = true
  loadError.value = null
  mergeError.value = null
  try {
    const resp = await apiService.getDuplicateAudiobooks()
    groups.value = resp.groups || []
    // Default every row to Skip (safe default — see kevinheneveld/Listenarr#6).
    for (const g of groups.value) {
      for (const r of g.rows) {
        rowActions[r.id] = 'skip'
      }
    }
    // Default-tab heuristic: if the only content is title/author collisions,
    // open that tab; otherwise stay on ASIN (the historical default + where
    // the actionable Keep/Discard live).
    if (asinGroups.value.length === 0 && titleAuthorGroups.value.length > 0) {
      activeTab.value = 'title_author'
    } else {
      activeTab.value = 'asin'
    }
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : 'Unknown error'
    errorTracking.captureException(err as Error, {
      component: 'DuplicatesReviewModal',
      operation: 'load',
    })
  } finally {
    loading.value = false
  }
}

async function executeMerge() {
  mergeError.value = null
  const merges: DuplicatesMergePair[] = []
  // Title/author Discards route through DELETE /library/{id} per row since
  // the merge endpoint requires shared ASIN. Collect them here.
  const titleAuthorDiscards: { row: DuplicateRow; deleteFiles: boolean }[] = []
  for (const g of groups.value) {
    if (!isAsinGroup(g)) {
      for (const r of g.rows) {
        if (rowActions[r.id] === 'merge') {
          // For phantom rows (no files) the delete is harmless — nothing on
          // disk. For rows with files the user opted in via Discard's
          // tooltip warning + the modal's overall confirmation panel; delete
          // files and the book folder.
          titleAuthorDiscards.push({ row: r, deleteFiles: r.fileCount > 0 })
        }
      }
      continue
    }
    const keepRow = g.rows.find((r) => rowActions[r.id] === 'keep')
    const losers = g.rows.filter((r) => rowActions[r.id] === 'merge').map((r) => r.id)
    const clears = g.rows.filter((r) => rowActions[r.id] === 'clearAsin').map((r) => r.id)
    if (losers.length === 0 && clears.length === 0) continue
    if (losers.length > 0 && !keepRow) {
      mergeError.value = `Group ${g.normalizedAsin} has Discard rows but no Keep row.`
      return
    }
    merges.push({
      winnerId: keepRow ? keepRow.id : null,
      loserIds: losers,
      clearAsinIds: clears,
    })
  }
  if (merges.length === 0 && titleAuthorDiscards.length === 0) {
    mergeError.value = 'No actions selected.'
    return
  }

  merging.value = true
  try {
    // Title/author Discards run first as independent per-row calls so a
    // mid-loop failure leaves the ASIN merge un-executed (recoverable state)
    // rather than half-done. Per-row failures are collected and surfaced.
    const taFailures: string[] = []
    for (const { row, deleteFiles } of titleAuthorDiscards) {
      try {
        await apiService.removeFromLibrary(row.id, {
          deleteFiles,
          deleteFolder: deleteFiles,
        })
      } catch (err) {
        const msg = err instanceof Error ? err.message : 'unknown error'
        taFailures.push(`id ${row.id} (${row.title || 'untitled'}): ${msg}`)
        errorTracking.captureException(err as Error, {
          component: 'DuplicatesReviewModal',
          operation: 'executeMerge.titleAuthorDiscard',
          metadata: { audiobookId: row.id, deleteFiles },
        })
      }
    }
    if (taFailures.length > 0) {
      mergeError.value = `Some title/author Discards failed: ${taFailures.slice(0, 3).join('; ')}${taFailures.length > 3 ? `; …(+${taFailures.length - 3} more)` : ''}`
      // Don't return — the ASIN-side merge can still run if some rows succeeded.
    }

    let result: MergeDuplicatesResult | null = null
    if (merges.length > 0) {
      result = await apiService.mergeDuplicateAudiobooks(merges)
    }
    // Fabricate a result-like object so the parent's "merged" listener
    // always gets a usable count, even when only title/author Discards ran.
    const merged: MergeDuplicatesResult = result ?? {
      groupsProcessed: 0,
      rowsDeleted: 0,
      asinsCleared: 0,
      downloadsReassigned: 0,
      historyReassigned: 0,
      moveJobsReassigned: 0,
      diskFilesDeleted: 0,
      diskFoldersDeleted: 0,
      diskParentFoldersDeleted: 0,
      warnings: [],
    }
    merged.rowsDeleted += titleAuthorDiscards.length - taFailures.length
    emit('merged', merged)
    emit('close')
  } catch (err) {
    mergeError.value = err instanceof Error ? err.message : 'Apply failed'
    errorTracking.captureException(err as Error, {
      component: 'DuplicatesReviewModal',
      operation: 'executeMerge',
    })
  } finally {
    merging.value = false
  }
}

function onClose() {
  if (merging.value) return
  emit('close')
}

function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unitIdx = 0
  while (value >= 1024 && unitIdx < units.length - 1) {
    value /= 1024
    unitIdx++
  }
  const decimals = value < 10 && unitIdx > 0 ? 1 : 0
  return `${value.toFixed(decimals)} ${units[unitIdx]}`
}

function formatMinutes(minutes: number): string {
  if (!minutes) return '—'
  const h = Math.floor(minutes / 60)
  const m = minutes % 60
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

function formatDuration(seconds: number): string {
  if (!seconds || seconds <= 0) return '—'
  const totalMin = Math.round(seconds / 60)
  return formatMinutes(totalMin)
}

function shortenPath(p: string | null): string {
  if (!p) return '—'
  // Trim the leading /audiobooks/ to save horizontal space; users still see
  // the full path in the title attribute.
  const trimmed = p.replace(/^\/?audiobooks\//, '')
  return trimmed || p
}

watch(
  () => props.visible,
  (v) => {
    if (v) {
      groups.value = []
      for (const k of Object.keys(rowActions)) delete rowActions[Number(k)]
      for (const k of Object.keys(expanded)) delete expanded[Number(k)]
      pendingConfirm.value = false
      mergeError.value = null
      void load()
    }
  },
  { immediate: true },
)
</script>

<style scoped>
.modal-overlay {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.55);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 3000;
  padding: 1rem;
}
.modal {
  background: #1a1a1a;
  border-radius: 8px;
  width: 100%;
  max-width: 1100px;
  max-height: 90vh;
  display: flex;
  flex-direction: column;
  border: 1px solid rgba(255, 255, 255, 0.08);
}
.modal-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 14px 16px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
}
.modal-header h2 {
  font-size: 16px;
  margin: 0;
  color: #fff;
}
.modal-close {
  background: transparent;
  border: 0;
  color: #aaa;
  cursor: pointer;
  font-size: 18px;
}
.modal-close:hover {
  color: #fff;
}
.modal-body {
  flex: 1;
  overflow-y: auto;
  padding: 14px 16px;
}
.modal-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 12px 16px;
  border-top: 1px solid rgba(255, 255, 255, 0.06);
  gap: 12px;
  flex-wrap: wrap;
}
.footer-summary {
  font-size: 12px;
  color: #aaa;
}
.footer-summary .error {
  color: #f06060;
  display: block;
  margin-top: 4px;
}
.footer-actions {
  display: flex;
  gap: 8px;
}
.btn {
  padding: 6px 14px;
  border-radius: 4px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.04);
  color: #ddd;
  font-size: 13px;
  cursor: pointer;
}
.btn:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.08);
}
.btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.btn-primary {
  background: #2a6fd0;
  border-color: #2a6fd0;
  color: #fff;
}
.btn-primary:hover:not(:disabled) {
  background: #3380e5;
}
.btn-danger {
  background: #b03030;
  border-color: #b03030;
  color: #fff;
}
.btn-danger:hover:not(:disabled) {
  background: #c84545;
}
.confirm-panel {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.confirm-title {
  font-size: 13px;
  color: #fff;
  font-weight: 600;
}
.confirm-list {
  margin: 0;
  padding-left: 18px;
  color: #ddd;
  font-size: 12px;
  line-height: 1.5;
}
.confirm-list li {
  margin-bottom: 2px;
}
.confirm-warning {
  font-size: 12px;
  color: #f0b060;
  padding: 6px 10px;
  background: rgba(240, 176, 96, 0.08);
  border-left: 3px solid #b08040;
  border-radius: 3px;
}
.confirm-warning-strong {
  color: #ff8080;
  background: rgba(255, 90, 90, 0.10);
  border-left-color: #c04040;
  margin-bottom: 6px;
}
.confirm-warning-list {
  margin: 4px 0 2px;
  padding-left: 20px;
}
.confirm-warning-list li {
  margin-bottom: 2px;
}
/* Mini-callout in the title/author help paragraph that visually mirrors the
   real narrator-mismatch chip in the row list. */
.inline-narrator-legend {
  display: inline-block;
  font-size: 10px;
  padding: 1px 6px;
  border-radius: 8px;
  background: rgba(232, 170, 60, 0.18);
  color: #f0c068;
  outline: 1px solid rgba(232, 170, 60, 0.45);
  vertical-align: baseline;
}
.confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 4px;
}
.confirm-error {
  font-size: 12px;
}
.state-msg {
  padding: 24px 8px;
  text-align: center;
  color: #aaa;
}
.state-msg.error {
  color: #f06060;
}
.help-text {
  font-size: 12px;
  color: #bbb;
  margin: 0 0 14px;
  line-height: 1.5;
}
.help-text code {
  font-family: monospace;
  background: rgba(255, 255, 255, 0.06);
  padding: 1px 5px;
  border-radius: 3px;
  color: #d8d8d8;
}
.dup-tabs {
  display: flex;
  gap: 4px;
  margin: 0 0 12px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
}
.dup-tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  background: transparent;
  border: none;
  border-bottom: 2px solid transparent;
  color: #aaa;
  padding: 8px 12px;
  font-size: 12px;
  cursor: pointer;
  transition: color 120ms ease, border-color 120ms ease;
}
.dup-tab:hover:not(:disabled) {
  color: #ddd;
}
.dup-tab.active {
  color: #fff;
  border-bottom-color: #5ea1ff;
}
.dup-tab:disabled {
  color: #555;
  cursor: not-allowed;
}
.dup-tab-count {
  font-size: 10px;
  background: rgba(255, 255, 255, 0.08);
  border-radius: 8px;
  padding: 1px 7px;
  color: inherit;
}
.dup-tab.active .dup-tab-count {
  background: rgba(94, 161, 255, 0.18);
}
.group-collision {
  font-family: monospace;
  letter-spacing: 0.02em;
  color: #c9b88a;
  max-width: 70%;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.group {
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  margin-bottom: 14px;
  background: rgba(255, 255, 255, 0.015);
}
.group-header {
  display: flex;
  justify-content: space-between;
  padding: 8px 12px;
  font-size: 11px;
  color: #888;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  background: rgba(255, 255, 255, 0.02);
}
.group-asin {
  font-family: monospace;
  letter-spacing: 0.04em;
}
.group-reason {
  margin: 0;
  padding: 6px 12px;
  font-size: 11px;
  color: #9bb;
  background: rgba(42, 111, 208, 0.04);
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
}
.row {
  border-top: 1px solid rgba(255, 255, 255, 0.03);
  padding: 10px 12px;
}
.row:first-of-type {
  border-top: 0;
}
.row.row-keep {
  background: rgba(46, 117, 232, 0.06);
}
.row.row-merge {
  background: rgba(232, 99, 99, 0.05);
}
.row.row-clear {
  background: rgba(232, 200, 99, 0.04);
}
.row-head {
  display: grid;
  grid-template-columns: 48px 1fr auto;
  gap: 12px;
  align-items: flex-start;
}
.row-thumb {
  width: 48px;
  height: 48px;
  border-radius: 4px;
  overflow: hidden;
  background: rgba(255, 255, 255, 0.04);
  flex-shrink: 0;
}
.row-thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}
.row-thumb-placeholder {
  width: 100%;
  height: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #555;
}
.row-main {
  min-width: 0;
}
.row-title {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.title-text {
  font-size: 13px;
  color: #fff;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.row-link {
  font-size: 11px;
  color: #6c9be8;
  text-decoration: none;
}
.row-link:hover {
  text-decoration: underline;
}
.badge-suggested {
  font-size: 10px;
  color: #2a6fd0;
  background: rgba(42, 111, 208, 0.15);
  border: 1px solid rgba(42, 111, 208, 0.4);
  padding: 1px 6px;
  border-radius: 8px;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  cursor: help;
}
.row-sub {
  font-size: 11px;
  color: #aaa;
  margin-top: 2px;
  display: flex;
  gap: 6px;
  align-items: center;
  flex-wrap: wrap;
}
.row-sep {
  color: #555;
}
.row-path {
  font-family: monospace;
  color: #999;
}
.row-meta {
  display: flex;
  gap: 4px;
  margin-top: 4px;
  flex-wrap: wrap;
}
.meta-chip {
  font-size: 10px;
  padding: 2px 6px;
  border-radius: 8px;
  background: rgba(255, 255, 255, 0.05);
  color: #888;
}
.meta-chip.on {
  background: rgba(80, 180, 100, 0.12);
  color: #6fc080;
}
.meta-chip.warn {
  background: rgba(232, 170, 60, 0.12);
  color: #e8aa3c;
  cursor: help;
}
/* Narrator chip is informational by default — slightly tinted so it stands
   apart from generic meta chips without screaming for attention. */
.narrator-chip {
  background: rgba(120, 160, 220, 0.12);
  color: #a8c4ec;
  max-width: 240px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  cursor: help;
}
.narrator-chip.narrator-missing {
  background: rgba(255, 255, 255, 0.04);
  color: #777;
  font-style: italic;
}
/* When the rows in a group disagree about narrators — different readings of
   the same book — flag the chip so the user notices before discarding. */
.narrator-chip.narrator-mismatch {
  background: rgba(232, 170, 60, 0.18);
  color: #f0c068;
  outline: 1px solid rgba(232, 170, 60, 0.45);
}
.row-actions {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 4px;
  min-width: 0;
}
.action-pill {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 3px 10px;
  border-radius: 12px;
  border: 1px solid rgba(255, 255, 255, 0.1);
  background: rgba(255, 255, 255, 0.04);
  color: #aaa;
  font-size: 11px;
  cursor: pointer;
  user-select: none;
  white-space: nowrap;
}
.action-pill input {
  position: absolute;
  opacity: 0;
  pointer-events: none;
}
.action-pill:hover:not(.disabled) {
  background: rgba(255, 255, 255, 0.08);
  color: #ddd;
}
.action-pill.active {
  background: #2a6fd0;
  border-color: #2a6fd0;
  color: #fff;
}
.action-pill.disabled {
  opacity: 0.35;
  cursor: not-allowed;
}
.expand-toggle {
  margin-top: 4px;
  background: transparent;
  border: 0;
  color: #888;
  font-size: 11px;
  cursor: pointer;
  padding: 0;
}
.expand-toggle:hover {
  color: #ccc;
}
.row-details {
  margin-top: 10px;
  padding: 10px 12px;
  background: rgba(0, 0, 0, 0.2);
  border-radius: 4px;
  border: 1px solid rgba(255, 255, 255, 0.04);
}
.details-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 8px 16px;
  margin-bottom: 10px;
}
.details-cell {
  min-width: 0;
}
.details-label {
  font-size: 10px;
  color: #888;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.details-value {
  font-size: 12px;
  color: #ddd;
  word-break: break-word;
}
.files-list {
  border-top: 1px solid rgba(255, 255, 255, 0.04);
  padding-top: 8px;
  font-size: 11px;
}
.files-list-header {
  display: grid;
  grid-template-columns: minmax(0, 2.5fr) 70px 70px 80px 70px;
  gap: 8px;
  padding: 2px 0;
  color: #777;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  font-size: 10px;
}
.files-list-row {
  display: grid;
  grid-template-columns: minmax(0, 2.5fr) 70px 70px 80px 70px;
  gap: 8px;
  padding: 3px 0;
  color: #bbb;
  border-top: 1px solid rgba(255, 255, 255, 0.02);
}
.file-path {
  font-family: monospace;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  color: #ccc;
}
.no-files {
  font-size: 11px;
  color: #777;
  font-style: italic;
}
.modal-fade-enter-active,
.modal-fade-leave-active {
  transition: opacity 0.15s;
}
.modal-fade-enter-from,
.modal-fade-leave-to {
  opacity: 0;
}

@media (max-width: 760px) {
  .row-head {
    grid-template-columns: 48px 1fr;
  }
  .row-actions {
    grid-column: 1 / -1;
    align-items: flex-start;
    flex-direction: row;
    flex-wrap: wrap;
    margin-top: 6px;
  }
}
</style>
