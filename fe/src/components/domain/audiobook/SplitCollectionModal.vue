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
<!--
  Break a multi-book record apart: server-side clustering (subdirectory /
  embedded tag / filename stem) with a suggested existing library record per
  group. Each group can be MOVED to a record (files relocate to its folder),
  DELETED (a redundant duplicate copy — files removed from disk), or left
  alone. When the destination already holds files, the group is weighed
  against them (better / worse / same download / different audio) and can
  REPLACE them: the group moves in, then the old files are deleted.
-->
<template>
  <Modal :visible="visible" size="lg" title="Split collection" @close="onClose">
    <ModalBody>
      <div v-if="loading" class="split-empty">Analyzing files…</div>
      <div v-else-if="error" class="split-error">{{ error }}</div>
      <template v-else>
        <p class="split-intro">
          {{ clusters.length }} group{{ clusters.length === 1 ? '' : 's' }} detected. Move a group
          to the record it belongs to (files relocate and the destination re-verifies), delete a
          redundant copy, or leave it alone. A destination that already has files is compared with
          the group first — a better copy can replace what is there.
        </p>
        <div class="split-probe-box">
          <div class="split-probe-head">
            <span>
              <strong>Audio probe</strong>
              <small
                >When file names carry no information (e.g. everything renamed to "Title-001…NNN"),
                transcribe the openings of the few boundary-suspect files — small intro/epilogue
                stubs, encoding changes, whole-book-length files — and split by what the audio
                itself announces.</small
              >
            </span>
            <button
              type="button"
              class="split-change-btn"
              :disabled="applying || probeStopRequested"
              @click="runAudioProbe"
            >
              {{ probing ? 'Stop' : 'Probe audio boundaries' }}
            </button>
          </div>
          <div v-if="probeStatus" class="split-probe-status">{{ probeStatus }}</div>
        </div>
        <div class="split-clusters">
          <div v-for="c in clusters" :key="c.key" class="split-cluster">
            <div class="split-cluster-head">
              <span class="split-cluster-name">
                <strong>{{ c.displayName }}</strong>
                <small>{{ statsLine(c) }}</small>
                <small v-if="c.fileNames.length" class="split-range">{{
                  describeRange(c.fileNames)
                }}</small>
              </span>
              <div class="split-actions">
                <label
                  ><input
                    type="radio"
                    :name="`act-${c.key}`"
                    value="none"
                    :checked="c.action === 'none'"
                    @change="choose(c, 'none')"
                  />
                  Leave</label
                >
                <label
                  ><input
                    type="radio"
                    :name="`act-${c.key}`"
                    value="move"
                    :checked="c.action === 'move'"
                    :disabled="!c.targetId"
                    @change="choose(c, 'move')"
                  />
                  {{ hasExisting(c) ? 'Move alongside' : 'Move' }}</label
                >
                <label v-if="hasExisting(c)"
                  ><input
                    type="radio"
                    :name="`act-${c.key}`"
                    value="replace"
                    :checked="c.action === 'replace'"
                    @change="choose(c, 'replace')"
                  />
                  Replace existing</label
                >
                <label class="split-delete-label"
                  ><input
                    type="radio"
                    :name="`act-${c.key}`"
                    value="delete"
                    :checked="c.action === 'delete'"
                    @change="choose(c, 'delete')"
                  />
                  Delete</label
                >
              </div>
            </div>
            <div class="split-cluster-dest">
              <template v-if="c.editing">
                <input
                  v-model="c.query"
                  type="text"
                  class="split-search"
                  placeholder="Search your library…"
                />
                <div class="split-candidates">
                  <button
                    v-for="cand in candidatesFor(c)"
                    :key="cand.id"
                    type="button"
                    class="split-candidate"
                    @click="pickTarget(c, cand)"
                  >
                    {{ cand.title }}
                    <small>id {{ cand.id }}</small>
                    <small :class="cand.fileCount > 0 ? 'split-occupied' : 'split-vacant'">
                      {{ cand.fileCount > 0 ? `${cand.fileCount} files` : 'empty' }}
                    </small>
                  </button>
                  <div v-if="candidatesFor(c).length === 0" class="split-empty">No matches.</div>
                </div>
                <CatalogTargetLookup
                  :default-title="c.query || c.displayName"
                  :default-author="(audiobook?.authors || [])[0] || ''"
                  :exclude-id="audiobook?.id"
                  @added="(book) => onCatalogAdded(c, book)"
                  @selected="(book) => onCatalogAdded(c, book)"
                />
              </template>
              <template v-else>
                <span v-if="c.targetId" class="split-dest-label">
                  → {{ c.targetTitle }}
                  <small>id {{ c.targetId }}</small>
                  <small v-if="c.comparing">checking what is already there…</small>
                  <small v-else-if="c.comparison" :class="occupancyClass(c)">
                    {{
                      hasExisting(c)
                        ? `already has ${describeStats(c.comparison.existing)}`
                        : 'empty — nothing there yet'
                    }}
                  </small>
                  <small
                    v-else-if="fileCountOf(c.targetId) !== null"
                    :class="(fileCountOf(c.targetId) ?? 0) > 0 ? 'split-occupied' : 'split-vacant'"
                  >
                    {{
                      (fileCountOf(c.targetId) ?? 0) > 0
                        ? `already has ${fileCountOf(c.targetId)} file(s)`
                        : 'empty — nothing there yet'
                    }}
                  </small>
                </span>
                <span v-else-if="c.matchesSource" class="split-dest-label split-dest-own">
                  this record's own book — it stays here
                </span>
                <span v-else-if="c.anonymous" class="split-dest-label split-dest-none">
                  no destination — nothing names this group; probe the audio or choose one
                </span>
                <span v-else class="split-dest-label split-dest-none">no destination</span>
                <button type="button" class="split-change-btn" @click="c.editing = true">
                  {{ c.targetId ? 'Change…' : 'Choose…' }}
                </button>
              </template>
            </div>
            <div
              v-if="c.targetId && c.comparison && hasExisting(c)"
              class="split-verdict"
              :class="`split-verdict-${c.comparison.verdict}`"
            >
              <span>
                <strong>{{ verdictHeadline(c.comparison.verdict) }}</strong>
                {{ c.comparison.reason }}
              </span>
              <button
                v-if="c.comparison.verdict === 'better' && c.action !== 'replace'"
                type="button"
                class="split-change-btn"
                @click="choose(c, 'replace')"
              >
                Replace the existing copy
              </button>
              <button
                v-else-if="
                  (c.comparison.verdict === 'identical' || c.comparison.verdict === 'worse') &&
                  c.action !== 'delete'
                "
                type="button"
                class="split-change-btn"
                @click="choose(c, 'delete')"
              >
                Delete this group instead
              </button>
            </div>
            <blockquote v-if="c.transcript" class="split-transcript">
              “{{ c.transcript }}”
            </blockquote>
            <details class="split-files">
              <summary>files</summary>
              <div class="split-file" v-for="name in c.fileNames" :key="name">{{ name }}</div>
            </details>
          </div>
        </div>
        <div v-if="applying" class="split-progress">{{ progressText }}</div>
      </template>
    </ModalBody>
    <div class="modal-footer">
      <button type="button" class="btn" :disabled="applying" @click="onClose">Cancel</button>
      <button
        type="button"
        class="btn btn-primary"
        :disabled="applying || (moveCount === 0 && replaceCount === 0 && deleteCount === 0)"
        @click="apply"
      >
        {{ applyLabel }}
      </button>
    </div>
  </Modal>
</template>

<script setup lang="ts">
import { ref, computed, watch, reactive } from 'vue'
import { Modal, ModalBody } from '@/components/feedback'
import { apiService } from '@/services/api'
import CatalogTargetLookup from './CatalogTargetLookup.vue'
import { useToast } from '@/services/toastService'
import { useLibraryStore } from '@/stores/library'
import { showConfirm } from '@/composables/useConfirm'
import type { Audiobook } from '@/types'
import {
  describeStats,
  describeRange,
  verdictHeadline,
  replaceTargetsOf,
  type SplitGroupStats,
  type SplitComparison,
} from '@/utils/splitGroups'

type GroupAction = 'none' | 'move' | 'replace' | 'delete'

interface ClusterRow {
  key: string
  displayName: string
  fileIds: number[]
  fileNames: string[]
  targetId: number | null
  targetTitle: string | null
  action: GroupAction
  editing: boolean
  query: string
  /** Opening transcript snippet that justified this group (audio probe only). */
  transcript?: string | null
  stats?: SplitGroupStats | null
  /** Cut out by shape alone — its name is the bulk-rename's, not a book's. */
  anonymous?: boolean
  /** Named for the record being split: it belongs where it is. */
  matchesSource?: boolean
  /** How the group weighs against the destination's existing files. */
  comparison: SplitComparison | null
  comparing: boolean
  /** The user picked the action — don't second-guess it when a comparison lands. */
  touched: boolean
}

const props = defineProps<{
  visible: boolean
  audiobook: Audiobook | null
}>()

const emit = defineEmits<{
  (e: 'close'): void
  (e: 'done', result: { groupsMoved: number; filesMoved: number; filesDeleted: number }): void
}>()

const toast = useToast()
const libraryStore = useLibraryStore()

const loading = ref(false)
const error = ref<string | null>(null)
const clusters = ref<ClusterRow[]>([])
const applying = ref(false)
const progressText = ref('')

// Audio-probe state: one whisper run per request (~40s each), looped
// client-side with a Stop escape — the same proxy-safe pattern as the AI
// library sweep.
const probing = ref(false)
const probeStopRequested = ref(false)
const probeStatus = ref<string | null>(null)

const moveCount = computed(
  () => clusters.value.filter((c) => c.action === 'move' && c.targetId).length,
)
const replaceCount = computed(
  () => clusters.value.filter((c) => c.action === 'replace' && c.targetId).length,
)
const deleteCount = computed(() => clusters.value.filter((c) => c.action === 'delete').length)
const applyLabel = computed(() => {
  if (applying.value) return 'Working…'
  const parts: string[] = []
  if (moveCount.value > 0) parts.push(`move ${moveCount.value}`)
  if (replaceCount.value > 0) parts.push(`replace ${replaceCount.value}`)
  if (deleteCount.value > 0) parts.push(`delete ${deleteCount.value}`)
  return parts.length ? `Apply (${parts.join(', ')})` : 'Apply'
})

interface ServerGroup {
  key: string
  displayName: string
  label?: string | null
  fileIds: number[]
  fileNames: string[]
  stats?: SplitGroupStats | null
  anonymous?: boolean
  matchesSource?: boolean
  boundaryTranscript?: string | null
  suggestedTargetId?: number | null
  suggestedTargetTitle?: string | null
}

function toRow(c: ServerGroup): ClusterRow {
  return reactive({
    key: c.key,
    displayName: c.displayName,
    fileIds: c.fileIds,
    fileNames: c.fileNames,
    targetId: c.suggestedTargetId ?? null,
    targetTitle: c.suggestedTargetTitle ?? null,
    action: (c.suggestedTargetId ? 'move' : 'none') as GroupAction,
    editing: false,
    query: c.label ?? c.displayName,
    transcript: c.boundaryTranscript ?? null,
    stats: c.stats ?? null,
    anonymous: c.anonymous ?? false,
    matchesSource: c.matchesSource ?? false,
    comparison: null,
    comparing: false,
    touched: false,
  })
}

function showGroups(groups: ServerGroup[]) {
  clusters.value = groups.map(toRow)
  // One at a time: each comparison reads both records' file lists.
  void (async () => {
    for (const row of clusters.value) {
      if (row.targetId) await loadComparison(row)
    }
  })()
}

function statsLine(c: ClusterRow): string {
  return describeStats(c.stats) || `${c.fileIds.length} file${c.fileIds.length === 1 ? '' : 's'}`
}

function hasExisting(c: ClusterRow): boolean {
  return !!c.targetId && (c.comparison?.existing.fileCount ?? 0) > 0
}

function occupancyClass(c: ClusterRow): string {
  return hasExisting(c) ? 'split-occupied' : 'split-vacant'
}

function choose(c: ClusterRow, action: GroupAction) {
  c.action = action
  c.touched = true
}

// Weigh the group against what its destination already holds. A destination
// with files is never moved onto by default — landing a second copy beside
// the first is rarely what's wanted — so an untouched "move" steps back to
// "leave" and the verdict line offers the real choices.
async function loadComparison(c: ClusterRow) {
  const book = props.audiobook
  const targetId = c.targetId
  c.comparison = null
  if (!book || !targetId) return
  c.comparing = true
  try {
    const comparison = await apiService.compareSplitGroup(book.id, targetId, c.fileIds)
    if (c.targetId !== targetId) return // destination changed while this was in flight
    c.comparison = comparison
    if (comparison.existing.fileCount > 0) {
      if (c.action === 'move' && !c.touched) c.action = 'none'
    } else if (c.action === 'replace') {
      c.action = 'move'
    }
  } catch {
    // Advisory only — without it the row simply behaves as before.
  } finally {
    if (c.targetId === targetId) c.comparing = false
  }
}

watch(
  () => props.visible,
  async (visible) => {
    if (!visible || !props.audiobook) return
    loading.value = true
    error.value = null
    clusters.value = []
    applying.value = false
    probing.value = false
    probeStopRequested.value = false
    probeStatus.value = null
    if (libraryStore.audiobooks.length === 0) {
      void libraryStore.fetchLibrary().catch(() => {})
    }
    try {
      const preview = await apiService.getSplitPreview(props.audiobook.id)
      showGroups(preview.clusters)
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Could not analyze this record.'
    } finally {
      loading.value = false
    }
  },
)

// Common words carry no signal — "The Rolling Stones" must not surface every
// record containing "the".
const STOPWORDS = new Set([
  'the',
  'a',
  'an',
  'of',
  'and',
  'or',
  'in',
  'on',
  'to',
  'for',
  'by',
  'at',
  'is',
  'it',
  'vol',
  'volume',
  'book',
  'part',
  'unabridged',
  'abridged',
])

function tokenize(text: string): string[] {
  // Digit tokens are kept at ANY length: in series titles the number is the
  // only discriminator ("… Volume 4" vs "… Volume 6" — 'volume' is a
  // stopword, so dropping single-char '4' made every volume tokenize
  // identically and the picker couldn't tell them apart).
  return (text || '')
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter((t) => (t.length > 1 || /^\d$/.test(t)) && !STOPWORDS.has(t))
}

// File count for a library record, so destinations can say whether files
// already live there (move onto an empty record vs. alongside existing ones).
function fileCountOf(id: number | null | undefined): number | null {
  if (id == null) return null
  const b = libraryStore.audiobooks.find((x) => x.id === id)
  if (!b) return null
  return b.fileCount ?? b.files?.length ?? 0
}

function candidatesFor(c: ClusterRow) {
  const sourceId = props.audiobook?.id
  const tokens = tokenize(c.query)
  if (tokens.length === 0) return []
  return libraryStore.audiobooks
    .filter((b) => b.id !== sourceId)
    .map((b) => {
      const titleTokens = new Set(tokenize(b.title || ''))
      // Digit hits count double: matching the volume/book number matters
      // more than sharing the series words every sibling record shares.
      const score = tokens.reduce(
        (s, t) => s + (titleTokens.has(t) ? (/^\d+$/.test(t) ? 2 : 1) : 0),
        0,
      )
      return {
        id: b.id,
        title: b.title || '',
        score,
        fileCount: b.fileCount ?? b.files?.length ?? 0,
      }
    })
    .filter((x) => x.score > 0)
    .sort((a, b) => b.score - a.score || a.title.localeCompare(b.title))
    .slice(0, 8)
}

function pickTarget(c: ClusterRow, cand: { id: number; title: string }) {
  c.targetId = cand.id
  c.targetTitle = cand.title
  c.action = 'move'
  c.touched = false
  c.editing = false
  void loadComparison(c)
}

function onCatalogAdded(c: ClusterRow, book: Audiobook) {
  // Fresh record OR an existing one the picker found: make it visible to the
  // occupancy badges (a fresh one isn't in the store yet) and pick it.
  if (!libraryStore.audiobooks.some((b) => b.id === book.id)) {
    libraryStore.audiobooks.push(book)
  }
  pickTarget(c, { id: book.id, title: book.title || 'New record' })
}

async function runAudioProbe() {
  const book = props.audiobook
  if (!book) return
  if (probing.value) {
    // Button doubles as Stop: the in-flight probe finishes, then the loop
    // exits and plans from what was collected so far.
    probeStopRequested.value = true
    probeStatus.value = 'Stopping after the current file…'
    return
  }

  probing.value = true
  probeStopRequested.value = false
  probeStatus.value = 'Finding boundary-suspect files…'
  try {
    const { whisperAvailable, candidates, totalFiles } = await apiService.getSplitProbeCandidates(
      book.id,
    )
    if (!whisperAvailable) {
      probeStatus.value = 'Whisper is not available on the server — audio probing needs it.'
      return
    }
    if (candidates.length === 0) {
      probeStatus.value = 'No boundary suspects found — the files look uniform.'
      return
    }

    const probes: Array<{ fileId: number; transcript: string | null }> = []
    for (const [index, candidate] of candidates.entries()) {
      if (probeStopRequested.value || !props.visible) break
      probeStatus.value = `Transcribing ${candidate.fileName} (${index + 1}/${candidates.length}, ${candidate.reason})…`
      try {
        const result = await apiService.probeSplitBoundary(book.id, candidate.fileId)
        probes.push(result)
      } catch {
        // A single unreadable file must not sink the run — the planner
        // treats a missing probe as "no evidence here".
        probes.push({ fileId: candidate.fileId, transcript: null })
      }
    }

    const heard = probes.filter((p) => p.transcript).length
    if (heard === 0) {
      probeStatus.value = 'No transcripts recovered — nothing to split by.'
      return
    }

    probeStatus.value = 'Building groups from what the audio says…'
    const plan = await apiService.planSplitFromProbes(book.id, probes)
    showGroups(plan.clusters)
    probeStatus.value = `${plan.clusters.length} group(s) from ${heard} probed opening(s) across ${totalFiles} files. Review below — the quoted openings are what the audio itself says.`
  } catch (err) {
    probeStatus.value = err instanceof Error ? err.message : 'Audio probe failed.'
  } finally {
    probing.value = false
    probeStopRequested.value = false
  }
}

async function deleteFiles(
  audiobookId: number,
  fileIds: number[],
  label: string,
  failures: string[],
): Promise<number> {
  let deleted = 0
  for (const fileId of fileIds) {
    try {
      await apiService.deleteAudiobookFile(audiobookId, fileId, { deleteFromDisk: true })
      deleted++
    } catch (err) {
      failures.push(`${label}: ${err instanceof Error ? err.message : 'delete failed'}`)
      break
    }
  }
  return deleted
}

async function apply() {
  const book = props.audiobook
  if (!book || applying.value) return
  const moves = clusters.value.filter(
    (c) => (c.action === 'move' || c.action === 'replace') && c.targetId,
  )
  const deletes = clusters.value.filter((c) => c.action === 'delete')
  if (moves.length === 0 && deletes.length === 0) return

  applying.value = true
  progressText.value = 'Checking destinations…'

  // What each replaced destination holds NOW, listed before anything moves
  // in: these — and only these — are deleted once its groups have arrived.
  const replaced = new Map<number, { title: string; existingFileIds: number[] }>()
  try {
    for (const targetId of replaceTargetsOf(clusters.value)) {
      const row = clusters.value.find((c) => c.action === 'replace' && c.targetId === targetId)!
      const fresh = await apiService.compareSplitGroup(book.id, targetId, row.fileIds)
      replaced.set(targetId, {
        title: row.targetTitle || `record ${targetId}`,
        existingFileIds: fresh.existingFileIds,
      })
    }
  } catch (err) {
    applying.value = false
    toast.error(
      'Split not started',
      err instanceof Error ? err.message : 'Could not read the destination being replaced.',
    )
    return
  }
  applying.value = false

  const warnings: string[] = []
  for (const { title, existingFileIds } of replaced.values()) {
    if (existingFileIds.length > 0) {
      warnings.push(
        `Replace the ${existingFileIds.length} existing file(s) on "${title}" — they are deleted from disk once the new copy has moved in.`,
      )
    }
  }
  if (deletes.length > 0) {
    const fileCount = deletes.reduce((n, c) => n + c.fileIds.length, 0)
    const names = deletes.map((c) => `"${c.displayName}"`).join(', ')
    warnings.push(`Delete ${fileCount} file(s) from disk for group(s) ${names}.`)
  }
  if (warnings.length > 0) {
    const ok = await showConfirm(
      `${warnings.join(' ')} This cannot be undone.`,
      'Confirm deletion',
      { danger: true, confirmText: 'Delete files', cancelText: 'Cancel' },
    )
    if (!ok) return
  }

  applying.value = true
  let groupsMoved = 0
  let filesMoved = 0
  let filesDeleted = 0
  let filesReplaced = 0
  const failures: string[] = []
  // A destination keeps its old files unless EVERY group bound for it
  // arrived whole — a half-landed copy must never cost the complete one.
  const incomplete = new Set<number>()

  for (const [index, c] of moves.entries()) {
    progressText.value = `Moving "${c.displayName}" (${index + 1}/${moves.length})…`
    try {
      const result = await apiService.transferAudiobookFiles(book.id, c.targetId!, c.fileIds)
      groupsMoved++
      filesMoved += result.transferred
      if (result.transferred < c.fileIds.length) incomplete.add(c.targetId!)
      if (result.warnings?.length) failures.push(`${c.displayName}: ${result.warnings.join(' ')}`)
    } catch (err) {
      incomplete.add(c.targetId!)
      failures.push(`${c.displayName}: ${err instanceof Error ? err.message : 'move failed'}`)
    }
  }

  for (const [targetId, { title, existingFileIds }] of replaced) {
    if (incomplete.has(targetId)) {
      failures.push(`${title}: the new copy did not fully arrive — existing files kept`)
      continue
    }
    progressText.value = `Removing the replaced copy of "${title}"…`
    filesReplaced += await deleteFiles(targetId, existingFileIds, title, failures)
  }

  for (const c of deletes) {
    progressText.value = `Deleting "${c.displayName}"…`
    filesDeleted += await deleteFiles(book.id, c.fileIds, c.displayName, failures)
  }
  applying.value = false

  const summary = [
    filesMoved > 0 ? `moved ${filesMoved} file(s) across ${groupsMoved} group(s)` : null,
    filesReplaced > 0 ? `replaced ${filesReplaced} existing file(s)` : null,
    filesDeleted > 0 ? `deleted ${filesDeleted} file(s)` : null,
  ]
    .filter(Boolean)
    .join('; ')
  if (failures.length > 0) {
    toast.warning(`Split finished with issues — ${summary}`, failures.slice(0, 3).join(' · '))
  } else {
    toast.success('Split collection complete', `${summary}.`)
  }
  emit('done', { groupsMoved, filesMoved, filesDeleted: filesDeleted + filesReplaced })
}

function onClose() {
  if (!applying.value) emit('close')
}
</script>

<style scoped>
.split-intro {
  color: #adb5bd;
  font-size: 0.9rem;
  margin: 0 0 1rem;
}

.split-probe-box {
  border: 1px dashed rgba(255, 255, 255, 0.14);
  border-radius: 6px;
  padding: 0.55rem 0.75rem;
  margin: 0 0 0.8rem;
}

.split-probe-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.8rem;
}

.split-probe-head span {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  color: #d8dee6;
  font-size: 0.9rem;
}

.split-probe-head small {
  color: #8a93a0;
  font-size: 0.78rem;
  line-height: 1.35;
}

.split-probe-status {
  margin-top: 0.45rem;
  color: #4dabf7;
  font-size: 0.85rem;
}

.split-transcript {
  margin: 0.45rem 0 0;
  padding: 0.35rem 0.6rem;
  border-left: 2px solid rgba(77, 171, 247, 0.5);
  color: #aab6c3;
  font-size: 0.82rem;
  font-style: italic;
  overflow-wrap: anywhere;
}

.split-clusters {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  max-height: 420px;
  overflow-y: auto;
}

.split-cluster {
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  padding: 0.6rem 0.75rem;
}

.split-cluster-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.6rem;
  flex-wrap: wrap;
}

.split-cluster-name {
  display: flex;
  align-items: baseline;
  gap: 0.6rem;
  flex-wrap: wrap;
}

.split-cluster-name small {
  color: #8a93a0;
}

.split-range {
  flex-basis: 100%;
  font-size: 0.78rem;
  overflow-wrap: anywhere;
}

.split-dest-own {
  color: #51cf66;
}

/* How the group weighs against the destination's existing files. */
.split-verdict {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.6rem;
  flex-wrap: wrap;
  margin-top: 0.4rem;
  padding: 0.35rem 0.6rem;
  border-left: 2px solid #8a93a0;
  color: #aab6c3;
  font-size: 0.82rem;
}

.split-verdict strong {
  color: #d8dee6;
  margin-right: 0.3rem;
}

.split-verdict-better {
  border-left-color: #51cf66;
}

.split-verdict-worse,
.split-verdict-identical {
  border-left-color: #f39c12;
}

.split-verdict-different {
  border-left-color: #e74c3c;
}

.split-verdict .split-change-btn {
  align-self: center;
  white-space: nowrap;
}

.split-actions {
  display: flex;
  gap: 0.9rem;
  font-size: 0.85rem;
  color: #adb5bd;
}

.split-actions label {
  display: flex;
  align-items: center;
  gap: 0.3rem;
  cursor: pointer;
}

.split-delete-label {
  color: #e74c3c;
}

.split-cluster-dest {
  margin: 0.4rem 0 0 0;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.split-dest-label {
  color: #d8dee6;
  font-size: 0.9rem;
}

.split-dest-label small {
  color: #8a93a0;
  margin-left: 0.35rem;
}

.split-dest-none {
  color: #f39c12;
  font-style: italic;
}

/* Destination occupancy: amber when files already live there (a move adds
   alongside — possible duplicate copy), muted green when the record is empty. */
.split-occupied {
  color: #f39c12 !important;
}

.split-vacant {
  color: #51cf66 !important;
}

.split-change-btn {
  align-self: flex-start;
  background: none;
  border: 1px solid var(--brand-500, #4dabf7);
  color: var(--brand-500, #4dabf7);
  border-radius: 5px;
  padding: 2px 10px;
  font-size: 0.8rem;
  cursor: pointer;
}

.split-search {
  width: 100%;
  padding: 0.45rem 0.6rem;
  border: 1px solid #444;
  border-radius: 6px;
  background-color: #1a1a1a;
  color: #fff;
  font-size: 0.9rem;
}

.split-candidates {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.split-candidate {
  text-align: left;
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.06);
  color: #d8dee6;
  border-radius: 5px;
  padding: 0.35rem 0.6rem;
  cursor: pointer;
}

.split-candidate:hover {
  background: rgba(77, 171, 247, 0.12);
}

.split-candidate small {
  color: #8a93a0;
  margin-left: 0.4rem;
}

.split-files {
  margin: 0.45rem 0 0 0;
  font-size: 0.82rem;
  color: #8a93a0;
}

.split-files summary {
  cursor: pointer;
}

.split-file {
  padding-left: 0.8rem;
  overflow-wrap: anywhere;
}

.split-progress {
  margin-top: 0.8rem;
  color: #4dabf7;
  font-size: 0.9rem;
}

.split-empty {
  color: #8a93a0;
  font-size: 0.9rem;
  padding: 0.3rem 0;
}

.split-error {
  color: #e74c3c;
}

.modal-footer {
  display: flex;
  justify-content: flex-end;
  gap: 0.75rem;
  padding: 1rem 1.5rem;
}
</style>
