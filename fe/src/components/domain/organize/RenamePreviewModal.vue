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
  <Modal :visible="visible" size="lg" @close="handleClose">
    <template #header>
      <ModalHeader title="Organize Files" :icon="PhFolderOpen" @close="handleClose" />
    </template>

    <template #default>
      <ModalBody compact maxHeight="72vh" class="organize-modal-body">
        <div v-if="loading" class="organize-state">
          <PhSpinner class="ph-spin organize-icon" />
          <p>Computing expected paths…</p>
        </div>

        <div v-else-if="error" class="organize-state organize-error">
          <PhWarningCircle class="organize-icon" />
          <p>{{ error }}</p>
        </div>

        <div v-else-if="executing" class="organize-state">
          <PhSpinner class="ph-spin organize-icon" />
          <p>Organizing selected audiobooks…</p>
        </div>

        <div
          v-else-if="loaded && changedPreviews.length === 0"
          class="organize-state organize-success"
        >
          <PhCheckCircle class="organize-icon" />
          <p>Everything already matches the current naming pattern.</p>
        </div>

        <div v-else-if="loaded" class="organize-preview">
          <div class="info-section organize-info-section">
            <PhInfo />
            <p>
              Review the proposed folder and filename changes before organizing files on disk.
              Selected items will be updated to match your current naming settings.
            </p>
          </div>

          <div class="form-group organize-group">
            <label class="form-label">
              <PhCheckSquare />
              Selection
            </label>
            <div class="form-control-card">
              <div class="organize-toolbar">
                <p class="organize-toolbar-title">
                  <strong>{{ selectedCount }}</strong> of {{ changedPreviews.length }} audiobook(s)
                  selected
                </p>
                <div class="toolbar-actions">
                  <button
                    type="button"
                    class="btn btn-secondary organize-action-btn"
                    @click="selectAll"
                  >
                    Select All
                  </button>
                  <button
                    type="button"
                    class="btn btn-secondary organize-action-btn"
                    @click="clearSelection"
                  >
                    Clear
                  </button>
                </div>
              </div>
            </div>
          </div>

          <div class="form-group organize-group">
            <label class="form-label">
              <PhFolderOpen />
              Preview
            </label>
            <div class="organize-preview-list">
              <div
                v-for="preview in changedPreviews"
                :key="preview.audiobookId"
                class="form-control-card preview-card"
              >
                <label class="preview-header">
                  <span class="preview-heading">
                    <input
                      type="checkbox"
                      class="preview-checkbox"
                      :checked="selected.has(preview.audiobookId)"
                      @change="toggleSelected(preview.audiobookId)"
                    />
                    <span class="preview-title">{{
                      preview.audiobookTitle || `Audiobook #${preview.audiobookId}`
                    }}</span>
                  </span>
                  <span class="preview-meta">{{ previewChangeSummary(preview) }}</span>
                </label>

                <div v-if="preview.folderChanged" class="preview-section">
                  <span class="preview-label">Folder</span>
                  <RenamePathDiff
                    :old-path="preview.currentFolderPath"
                    :new-path="preview.newFolderPath"
                  />
                </div>

                <div
                  v-for="file in preview.fileRenames.filter((entry) => entry.changed)"
                  :key="file.fileId"
                  class="preview-section"
                >
                  <span class="preview-label">File</span>
                  <RenamePathDiff :old-path="file.currentFilename" :new-path="file.newFilename" />
                </div>
              </div>
            </div>
          </div>
        </div>

        <div
          v-if="finished && hasConflicts"
          class="form-group organize-conflicts-group"
        >
          <label class="form-label">
            <PhWarning />
            Resolve conflicts
          </label>
          <div class="info-section conflict-info-section">
            <PhInfo />
            <p>
              A file already exists at the destination. Open each book in a new tab to preview the
              audio and metadata, then choose what to do. Deletes can't be undone — check your
              backups.
            </p>
          </div>

          <div class="conflict-list">
            <div
              v-for="card in conflictCards"
              :key="card.audiobookId"
              class="form-control-card conflict-card"
              :class="{ resolved: card.status === 'resolved' }"
            >
              <div class="conflict-compare">
                <!-- Incoming: the book being organized -->
                <div class="conflict-side">
                  <div class="conflict-side-head">
                    <span class="conflict-side-label">Incoming (being organized)</span>
                    <a
                      class="conflict-open"
                      :href="`/audiobooks/${card.audiobookId}`"
                      target="_blank"
                      rel="noopener"
                      title="Open in a new tab to preview"
                    >
                      {{ card.title }} <PhArrowSquareOut :size="13" />
                    </a>
                  </div>
                  <div
                    v-for="(pair, i) in card.files"
                    :key="`in-${i}`"
                    class="conflict-file"
                  >
                    <span class="conflict-file-name" :title="pair.incoming.path || ''">{{
                      fileName(pair.incoming.path)
                    }}</span>
                    <span class="conflict-file-meta">
                      {{ formatBytes(pair.incoming.size) }} ·
                      {{ fileFormatLabel(pair.incoming) }} ·
                      {{ formatBitrate(pair.incoming.bitrate) }} ·
                      {{ formatDuration(pair.incoming.durationSeconds) }}
                      <template v-if="pair.incoming.modifiedAt">
                        · {{ formatModified(pair.incoming.modifiedAt) }}
                      </template>
                    </span>
                  </div>
                </div>

                <!-- Existing: what's already at the destination -->
                <div class="conflict-side">
                  <div class="conflict-side-head">
                    <span class="conflict-side-label">Existing (at destination)</span>
                    <a
                      v-if="card.existingTracked && card.existingAudiobookId != null"
                      class="conflict-open"
                      :href="`/audiobooks/${card.existingAudiobookId}`"
                      target="_blank"
                      rel="noopener"
                      title="Open in a new tab to preview"
                    >
                      {{ card.existingAudiobookTitle || `Audiobook #${card.existingAudiobookId}` }}
                      <PhArrowSquareOut :size="13" />
                    </a>
                    <span v-else class="conflict-untracked">Untracked file on disk</span>
                  </div>
                  <div
                    v-for="(pair, i) in card.files"
                    :key="`ex-${i}`"
                    class="conflict-file"
                  >
                    <span class="conflict-file-name" :title="pair.existing.path || ''">{{
                      fileName(pair.existing.path)
                    }}</span>
                    <span class="conflict-file-meta">
                      {{ formatBytes(pair.existing.size) }} ·
                      {{ fileFormatLabel(pair.existing) }} ·
                      {{ formatBitrate(pair.existing.bitrate) }} ·
                      {{ formatDuration(pair.existing.durationSeconds) }}
                      <template v-if="pair.existing.modifiedAt">
                        · {{ formatModified(pair.existing.modifiedAt) }}
                      </template>
                    </span>
                  </div>
                </div>
              </div>

              <!-- Resolved state -->
              <div v-if="card.status === 'resolved'" class="conflict-resolved-msg">
                <PhCheckCircle :size="16" />
                <span>{{ card.message }}</span>
              </div>

              <!-- Resolving spinner -->
              <div v-else-if="card.status === 'resolving'" class="conflict-resolving-msg">
                <PhSpinner class="ph-spin" :size="16" />
                <span>Applying…</span>
              </div>

              <!-- Pending: action buttons + confirm gate -->
              <template v-else>
                <p v-if="card.message" class="conflict-card-error">{{ card.message }}</p>

                <div v-if="!card.pendingAction" class="conflict-actions">
                  <button
                    type="button"
                    class="btn btn-secondary conflict-btn"
                    @click="requestConflictAction(card, 'overwrite')"
                  >
                    {{ card.existingTracked ? 'Overwrite (replace existing book)' : 'Overwrite file' }}
                  </button>
                  <button
                    type="button"
                    class="btn btn-secondary conflict-btn"
                    @click="requestConflictAction(card, 'delete-incoming')"
                  >
                    Delete duplicate (this book)
                  </button>
                  <button
                    type="button"
                    class="btn btn-secondary conflict-btn"
                    @click="skipConflict(card)"
                  >
                    Skip
                  </button>
                </div>

                <div v-else class="conflict-confirm">
                  <p class="conflict-confirm-text">
                    <PhWarning :size="15" />
                    <span v-if="card.pendingAction === 'delete-incoming'">
                      Permanently delete <strong>{{ card.title }}</strong> and its files from disk?
                    </span>
                    <span v-else-if="card.existingTracked">
                      Permanently delete
                      <strong>{{ card.existingAudiobookTitle || `Audiobook #${card.existingAudiobookId}` }}</strong>
                      and its files, then move <strong>{{ card.title }}</strong> into place?
                    </span>
                    <span v-else>
                      Overwrite the existing file on disk with <strong>{{ card.title }}</strong>?
                    </span>
                  </p>
                  <div class="conflict-confirm-actions">
                    <button type="button" class="btn cancel-button" @click="card.pendingAction = null">
                      Back
                    </button>
                    <button type="button" class="btn btn-danger" @click="applyConflictAction(card)">
                      Confirm
                    </button>
                  </div>
                </div>
              </template>
            </div>
          </div>
        </div>

        <div
          v-if="finished && nonConflictResults.length > 0"
          class="form-group organize-results-group"
        >
          <label class="form-label">
            <PhCheckCircle />
            Results
          </label>
          <div class="form-control-card results-list">
            <div
              v-for="result in nonConflictResults"
              :key="result.audiobookId"
              class="result-row"
              :class="{ success: result.success, error: !result.success }"
            >
              <component
                :is="result.success ? PhCheckCircle : PhWarningCircle"
                class="result-icon"
              />
              <span class="result-title">{{ titleFor(result.audiobookId) }}</span>
              <span class="result-detail">
                {{ result.success ? 'Organized successfully' : result.error || 'Organize failed' }}
              </span>
            </div>
          </div>
        </div>
      </ModalBody>
    </template>

    <template #footer>
      <ModalFooter :showCancel="false">
        <template #left>
          <button type="button" class="btn cancel-button" @click="handleClose">
            <PhX :size="16" />
            {{ finished ? 'Close' : 'Cancel' }}
          </button>
        </template>
        <template #default>
          <button
            v-if="!finished"
            type="button"
            class="btn btn-primary"
            :disabled="loading || executing || selectedCount === 0"
            @click="confirm"
          >
            <PhSpinner v-if="executing" class="ph-spin" :size="16" />
            <PhFolderOpen v-else :size="16" />
            {{ executing ? 'Organizing…' : `Organize ${selectedCount}` }}
          </button>
          <button v-else type="button" class="btn btn-primary" @click="handleDone">
            <PhCheckCircle :size="16" />
            Done
          </button>
        </template>
      </ModalFooter>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import {
  PhArrowSquareOut,
  PhCheckCircle,
  PhCheckSquare,
  PhFolderOpen,
  PhInfo,
  PhSpinner,
  PhWarning,
  PhWarningCircle,
  PhX,
} from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalFooter, ModalHeader } from '@/components/feedback'
import RenamePathDiff from './RenamePathDiff.vue'
import { apiService } from '@/services/api'
import type {
  ConflictFileInfo,
  FileRenameResultItem,
  RenameOperation,
  RenamePreview,
  RenameResult,
} from '@/types'

const props = withDefaults(
  defineProps<{
    visible?: boolean
    audiobookIds?: number[]
  }>(),
  {
    visible: false,
    audiobookIds: () => [],
  },
)

const emit = defineEmits<{
  close: []
  done: []
}>()

const loading = ref(false)
const loaded = ref(false)
const executing = ref(false)
const finished = ref(false)
const error = ref<string | null>(null)
const previews = ref<RenamePreview[]>([])
const results = ref<RenameResult[]>([])
const selected = ref<Set<number>>(new Set())
// True once any conflict resolution has touched the library (a delete or a
// successful overwrite), so the parent refreshes its data on close.
const libraryChanged = ref(false)
// Declared up here (not in the conflict-resolution block below) so the
// immediate visibility watcher's reset() — which runs during setup — can
// reference it without hitting a temporal-dead-zone error.
const conflictCards = reactive<ConflictCard[]>([])

const changedPreviews = computed(() => previews.value.filter((preview) => preview.hasChanges))
const selectedCount = computed(
  () => changedPreviews.value.filter((preview) => selected.value.has(preview.audiobookId)).length,
)

watch(
  () => props.visible,
  async (visible) => {
    if (visible && props.audiobookIds.length > 0) {
      await load()
    } else if (!visible) {
      reset()
    }
  },
  { immediate: true },
)

async function load() {
  loading.value = true
  loaded.value = false
  executing.value = false
  finished.value = false
  error.value = null
  results.value = []

  try {
    previews.value = await apiService.previewRename(props.audiobookIds)
    selected.value = new Set(changedPreviews.value.map((preview) => preview.audiobookId))
    loaded.value = true
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to load organize preview.'
  } finally {
    loading.value = false
  }
}

function buildOperation(preview: RenamePreview, overwrite = false): RenameOperation {
  return {
    audiobookId: preview.audiobookId,
    newFolderPath: preview.folderChanged ? preview.newFolderPath : undefined,
    fileRenames: preview.fileRenames
      .filter((entry) => entry.changed)
      .map((entry) => ({
        fileId: entry.fileId,
        currentPath: entry.currentPath || '',
        newPath: entry.newPath || '',
        onConflict: overwrite ? ('Overwrite' as const) : undefined,
      })),
  }
}

async function confirm() {
  const operations: RenameOperation[] = changedPreviews.value
    .filter((preview) => selected.value.has(preview.audiobookId))
    .map((preview) => buildOperation(preview))

  if (operations.length === 0) {
    return
  }

  executing.value = true
  error.value = null
  try {
    results.value = await apiService.executeRename(operations)
    rebuildConflictCards()
    finished.value = true
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Failed to organize files.'
  } finally {
    executing.value = false
  }
}

// ---- Conflict resolution -------------------------------------------------
// When a file move dead-ends on "target file already exists", the backend now
// returns the colliding files' metadata (and, when tracked, the audiobook that
// owns the destination). We surface a small resolver — mirroring the duplicate
// review flow — letting the user open each book in a new tab to preview, then
// Delete the duplicate, let the incoming book Overwrite, or Skip.

type ConflictAction = 'delete-incoming' | 'overwrite' | null

interface ConflictCard {
  audiobookId: number
  title: string
  existingTracked: boolean
  existingAudiobookId?: number
  existingAudiobookTitle?: string
  files: { incoming: ConflictFileInfo; existing: ConflictFileInfo }[]
  pendingAction: ConflictAction
  status: 'pending' | 'resolving' | 'resolved'
  message: string
}

function rebuildConflictCards() {
  conflictCards.splice(0, conflictCards.length)
  for (const result of results.value) {
    const conflictItems = (result.renamedFiles || []).filter(
      (f: FileRenameResultItem) => f.isConflict && f.conflict,
    )
    if (conflictItems.length === 0) continue
    const first = conflictItems[0].conflict!
    conflictCards.push({
      audiobookId: result.audiobookId,
      title: titleFor(result.audiobookId),
      existingTracked: !!first.existingTracked,
      existingAudiobookId: first.existingAudiobookId,
      existingAudiobookTitle: first.existingAudiobookTitle,
      files: conflictItems.map((f) => ({
        incoming: f.conflict!.incoming,
        existing: f.conflict!.existing,
      })),
      pendingAction: null,
      status: 'pending',
      message: '',
    })
  }
}

const hasConflicts = computed(() => conflictCards.length > 0)
// Non-conflict result rows (successes + plain failures) still render normally.
const nonConflictResults = computed(() =>
  results.value.filter(
    (r) => !(r.renamedFiles || []).some((f) => f.isConflict),
  ),
)

function requestConflictAction(card: ConflictCard, action: ConflictAction) {
  card.pendingAction = card.pendingAction === action ? null : action
}

async function applyConflictAction(card: ConflictCard) {
  if (!card.pendingAction || card.status === 'resolving') return
  const action = card.pendingAction
  card.status = 'resolving'
  error.value = null
  try {
    if (action === 'delete-incoming') {
      await apiService.removeFromLibrary(card.audiobookId, {
        deleteFiles: true,
        deleteFolder: true,
      })
      libraryChanged.value = true
      card.status = 'resolved'
      card.message = 'Deleted the duplicate book and its files.'
    } else if (action === 'overwrite') {
      if (card.existingTracked && card.existingAudiobookId != null) {
        // The destination is owned by another record — remove that record (and
        // its files) via the proven delete path, then re-run organize so the
        // incoming book moves into the now-free destination.
        await apiService.removeFromLibrary(card.existingAudiobookId, {
          deleteFiles: true,
          deleteFolder: true,
        })
        libraryChanged.value = true
      }
      const preview = previews.value.find((p) => p.audiobookId === card.audiobookId)
      if (!preview) {
        card.status = 'resolved'
        card.message = 'Removed the existing book, but could not re-run organize automatically — reopen Organize to finish.'
        return
      }
      // Untracked destination → tell the backend to overwrite the orphan file;
      // tracked destination was just deleted, so a normal move suffices.
      const overwrite = !card.existingTracked
      const [res] = await apiService.executeRename([buildOperation(preview, overwrite)])
      libraryChanged.value = true
      const stillConflicted = (res?.renamedFiles || []).some((f) => f.isConflict)
      if (res?.success) {
        card.status = 'resolved'
        card.message = 'Files moved into place.'
      } else if (stillConflicted) {
        card.status = 'pending'
        card.message = 'Still conflicting — the destination is occupied. Resolve the other book first.'
      } else {
        card.status = 'resolved'
        card.message = res?.error || 'Organize completed with warnings.'
      }
    }
  } catch (err) {
    card.status = 'pending'
    card.message = err instanceof Error ? err.message : 'Resolution failed.'
  } finally {
    card.pendingAction = null
  }
}

function skipConflict(card: ConflictCard) {
  card.pendingAction = null
  card.status = 'resolved'
  card.message = 'Skipped — both files left as they were.'
}

function selectAll() {
  selected.value = new Set(changedPreviews.value.map((preview) => preview.audiobookId))
}

function clearSelection() {
  selected.value = new Set()
}

function toggleSelected(id: number) {
  const next = new Set(selected.value)
  if (next.has(id)) {
    next.delete(id)
  } else {
    next.add(id)
  }
  selected.value = next
}

function titleFor(audiobookId: number) {
  return (
    previews.value.find((preview) => preview.audiobookId === audiobookId)?.audiobookTitle ||
    `Audiobook #${audiobookId}`
  )
}

function previewChangeSummary(preview: RenamePreview) {
  const parts: string[] = []
  const changedFiles = preview.fileRenames.filter((entry) => entry.changed).length

  if (preview.folderChanged) {
    parts.push('Folder')
  }

  if (changedFiles > 0) {
    parts.push(`${changedFiles} file${changedFiles === 1 ? '' : 's'}`)
  }

  return parts.join(' + ') || 'No changes'
}

function fileName(path?: string): string {
  if (!path) return '—'
  const parts = path.split(/[\\/]/)
  return parts[parts.length - 1] || path
}

function formatBytes(bytes?: number): string {
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

function formatDuration(seconds?: number): string {
  if (!seconds || seconds <= 0) return '—'
  const total = Math.round(seconds)
  const h = Math.floor(total / 3600)
  const m = Math.floor((total % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  return `${m}m`
}

function formatBitrate(bitrate?: number): string {
  if (!bitrate || bitrate <= 0) return '—'
  return `${Math.round(bitrate / 1000)} kbps`
}

function formatModified(iso?: string): string {
  if (!iso) return '—'
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
}

function fileFormatLabel(info: ConflictFileInfo): string {
  return info.format || info.container || info.codec || '—'
}

function handleDone() {
  emit('done')
}

function handleClose() {
  if (finished.value || libraryChanged.value) {
    emit('done')
  } else {
    emit('close')
  }
}

function reset() {
  loading.value = false
  loaded.value = false
  executing.value = false
  finished.value = false
  error.value = null
  previews.value = []
  results.value = []
  selected.value = new Set()
  conflictCards.splice(0, conflictCards.length)
  libraryChanged.value = false
}
</script>

<style scoped>
.info-section {
  display: flex;
  align-items: flex-start;
  gap: 0.6rem;
  padding: 0.75rem;
  background-color: rgba(52, 152, 219, 0.09);
  border: 1px solid rgba(52, 152, 219, 0.28);
  border-radius: 6px;
  color: #3498db;
}

.info-section p {
  margin: 0;
  font-size: 0.95rem;
  line-height: 1.4;
  color: #ccc;
}

.info-section strong {
  color: #5dade2;
}

.form-group {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.form-label {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
  font-weight: 500;
  margin: 0;
}

.organize-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.75rem;
  padding: 2rem;
  text-align: center;
}

.organize-icon {
  width: 28px;
  height: 28px;
}

.organize-error {
  color: var(--text-danger, #ef5350);
}

.organize-success {
  color: var(--text-success, #66bb6a);
}

.organize-preview {
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
}

.organize-info-section {
  margin-bottom: 0;
}

.organize-group {
  margin-bottom: 0;
}

.organize-toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
  flex-wrap: wrap;
}

.organize-toolbar-title {
  margin: 0;
  color: var(--text-secondary, #c7ced8);
  line-height: 1.4;
}

.toolbar-actions {
  display: flex;
  gap: 0.75rem;
  align-items: center;
}

.organize-action-btn {
  min-height: 36px;
  padding: 0.45rem 0.85rem;
  font-size: 0.88rem;
}

.organize-preview-list {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
}

.preview-card {
  padding: 1rem;
}

.preview-card > * + * {
  margin-top: 0.75rem;
}

.preview-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 0.85rem;
  margin-bottom: 0.9rem;
  cursor: pointer;
}

.preview-heading {
  display: inline-flex;
  align-items: flex-start;
  gap: 0.65rem;
  min-width: 0;
}

.preview-checkbox {
  margin-top: 0.15rem;
}

.preview-title {
  font-weight: 600;
  color: var(--text-primary, #f3f6fb);
  line-height: 1.4;
}

.preview-meta {
  flex-shrink: 0;
  color: var(--text-muted, #97a2af);
  font-size: 0.82rem;
  font-weight: 500;
  line-height: 1.4;
}

.preview-section {
  display: flex;
  flex-direction: column;
  gap: 0.45rem;
  margin-top: 0;
}

.preview-label {
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--text-muted, #97a2af);
}

.results-list {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.result-row {
  display: grid;
  grid-template-columns: 20px minmax(0, 1fr) auto;
  align-items: center;
  gap: 0.75rem;
  padding: 0.65rem 0.85rem;
  border-radius: 8px;
}

.result-row.success {
  background: rgba(102, 187, 106, 0.08);
  color: var(--text-success, #66bb6a);
}

.result-row.error {
  background: rgba(239, 83, 80, 0.08);
  color: var(--text-danger, #ef5350);
}

.result-icon {
  width: 18px;
  height: 18px;
}

.result-title {
  font-weight: 600;
}

.result-detail {
  color: var(--text-secondary, #c7ced8);
}

.cancel-button {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
}

/* ---- Conflict resolver ---- */
.organize-conflicts-group {
  margin-bottom: 1rem;
}

.conflict-info-section {
  margin-bottom: 0.85rem;
}

.conflict-list {
  display: flex;
  flex-direction: column;
  gap: 0.85rem;
}

.conflict-card {
  padding: 1rem;
  border-left: 3px solid rgba(232, 170, 60, 0.55);
}

.conflict-card.resolved {
  border-left-color: rgba(102, 187, 106, 0.55);
}

.conflict-compare {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0.85rem;
  margin-bottom: 0.85rem;
}

.conflict-side {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  min-width: 0;
}

.conflict-side-head {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
}

.conflict-side-label {
  font-size: 0.7rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--text-muted, #97a2af);
}

.conflict-open {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  color: #6c9be8;
  text-decoration: none;
  font-weight: 600;
  font-size: 0.9rem;
  min-width: 0;
}

.conflict-open:hover {
  text-decoration: underline;
}

.conflict-untracked {
  font-size: 0.9rem;
  color: var(--text-secondary, #c7ced8);
  font-style: italic;
}

.conflict-file {
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
  padding: 0.35rem 0.5rem;
  background: rgba(255, 255, 255, 0.025);
  border-radius: 5px;
}

.conflict-file-name {
  font-family: monospace;
  font-size: 0.82rem;
  color: var(--text-primary, #f3f6fb);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.conflict-file-meta {
  font-size: 0.74rem;
  color: var(--text-muted, #97a2af);
}

.conflict-actions {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.conflict-btn {
  min-height: 34px;
  padding: 0.4rem 0.75rem;
  font-size: 0.85rem;
}

.conflict-confirm {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  padding: 0.6rem 0.75rem;
  background: rgba(239, 83, 80, 0.07);
  border: 1px solid rgba(239, 83, 80, 0.25);
  border-radius: 6px;
}

.conflict-confirm-text {
  display: flex;
  align-items: flex-start;
  gap: 0.4rem;
  margin: 0;
  font-size: 0.88rem;
  color: var(--text-secondary, #c7ced8);
  line-height: 1.4;
}

.conflict-confirm-text strong {
  color: var(--text-primary, #f3f6fb);
}

.conflict-confirm-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
}

.btn-danger {
  background: #b03030;
  border-color: #b03030;
  color: #fff;
}

.btn-danger:hover {
  background: #c84545;
}

.conflict-card-error {
  margin: 0 0 0.5rem;
  font-size: 0.82rem;
  color: var(--text-danger, #ef5350);
}

.conflict-resolved-msg {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  color: var(--text-success, #66bb6a);
  font-size: 0.88rem;
}

.conflict-resolving-msg {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  color: var(--text-secondary, #c7ced8);
  font-size: 0.88rem;
}

@media (max-width: 768px) {
  .conflict-compare {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 768px) {
  .toolbar-actions {
    width: 100%;
    justify-content: flex-start;
  }

  .preview-header {
    flex-direction: column;
    align-items: stretch;
  }

  .preview-meta {
    align-self: flex-start;
  }
}
</style>
