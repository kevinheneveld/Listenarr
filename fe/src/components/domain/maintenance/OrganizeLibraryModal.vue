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
      <div class="modal organize-modal" role="dialog" aria-modal="true" aria-labelledby="org-modal-title">
        <header class="modal-header">
          <h2 id="org-modal-title">Organize library folders</h2>
          <button class="modal-close" :disabled="applying" @click="onClose" aria-label="Close">
            <PhX />
          </button>
        </header>

        <div class="modal-body">
          <!-- Always-visible move-queue status banner: polls /library/move/summary
               on a 5s tick while open. Useful both BEFORE Apply (shows leftover
               state from a previous run) and AFTER (live progress). Renders
               nothing when the queue has never had any jobs. -->
          <MoveQueueStatusBanner v-if="visible" />

          <div v-if="loading" class="state-msg">Computing canonical paths for your library…</div>

          <div v-else-if="loadError" class="state-msg error">
            Failed to load preview: {{ loadError }}
          </div>

          <template v-else-if="preview">
            <p class="help-text">
              Each audiobook below has been compared against its canonical
              <code>{Author}/{Title}</code> path under the configured
              <strong>Folder Naming Pattern</strong>. Confirm the selection then click Apply to
              queue per-book moves. <strong>Collision</strong> and
              <strong>Invalid target</strong> rows are surfaced for your awareness — they
              must be resolved by hand (run the duplicates tool, or fix missing metadata)
              before they can be organized.
            </p>

            <div class="bucket-summary">
              <span class="bucket pill pill-action">{{ preview.willMoveCount }} will move</span>
              <span class="bucket pill pill-ok">{{ preview.alreadyCanonicalCount }} already canonical</span>
              <span class="bucket pill pill-warn">{{ preview.collisionCount }} collision{{ preview.collisionCount === 1 ? '' : 's' }}</span>
              <span class="bucket pill pill-warn">{{ preview.invalidTargetCount }} invalid target{{ preview.invalidTargetCount === 1 ? '' : 's' }}</span>
            </div>

            <section v-if="willMoveRows.length > 0" class="section">
              <header class="section-header">
                <h3>Will move ({{ willMoveRows.length }})</h3>
                <div class="section-tools">
                  <button type="button" class="link" @click="toggleAll(true)">Select all</button>
                  <button type="button" class="link" @click="toggleAll(false)">Select none</button>
                  <span class="section-count">{{ selectedCount }} selected · {{ formatBytes(selectedBytes) }}</span>
                </div>
              </header>
              <div class="rows">
                <label v-for="row in willMoveRows" :key="row.id" class="row">
                  <input
                    type="checkbox"
                    :checked="selected[row.id] === true"
                    :disabled="applying"
                    @change="onToggleRow(row.id, ($event.target as HTMLInputElement).checked)"
                  />
                  <div class="row-body">
                    <div class="row-title">
                      <strong>{{ row.title || '(no title)' }}</strong>
                      <span class="row-author">{{ row.author || 'Unknown Author' }}</span>
                    </div>
                    <div class="row-paths">
                      <div class="row-path" :title="row.currentPath || ''">
                        <span class="path-label">from</span>
                        <span class="path-value">{{ row.currentPath || '(empty)' }}</span>
                      </div>
                      <div class="row-path" :title="row.targetPath || ''">
                        <span class="path-label">to</span>
                        <span class="path-value path-target">{{ row.targetPath }}</span>
                      </div>
                    </div>
                    <div class="row-meta">
                      {{ row.fileCount }} file{{ row.fileCount === 1 ? '' : 's' }} · {{ formatBytes(row.totalSize) }}
                    </div>
                  </div>
                </label>
              </div>
            </section>

            <section v-if="collisionGroups.length > 0" class="section">
              <header class="section-header">
                <h3>Collisions ({{ preview.collisionCount }})</h3>
                <p class="section-help">
                  These audiobooks compute to the same canonical folder. Organizing them would
                  clobber each other on disk. Resolve via the duplicates tool (same ASIN) or
                  by fixing the metadata difference (different ASIN but identical
                  <code>{Author}/{Title}</code>).
                </p>
              </header>
              <div v-for="group in collisionGroups" :key="group.key" class="collision-group">
                <div class="collision-target">→ {{ group.targetPath }}</div>
                <ul class="collision-members">
                  <li v-for="r in group.rows" :key="r.id">
                    <strong>id {{ r.id }}</strong>: {{ r.title || '(no title)' }} —
                    <em>{{ r.author || 'Unknown Author' }}</em>
                    <div class="row-path">
                      <span class="path-label">from</span>
                      <span class="path-value">{{ r.currentPath || '(empty)' }}</span>
                    </div>
                  </li>
                </ul>
              </div>
            </section>

            <section v-if="invalidRows.length > 0" class="section">
              <header class="section-header">
                <h3>Invalid target ({{ invalidRows.length }})</h3>
                <p class="section-help">
                  Skipped because the canonical path can't be computed. Fix the underlying
                  metadata and re-run the preview.
                </p>
              </header>
              <ul class="invalid-list">
                <li v-for="row in invalidRows" :key="row.id">
                  <strong>id {{ row.id }}</strong>: {{ row.title || '(no title)' }} —
                  <em>{{ row.author || 'Unknown Author' }}</em>
                  · <span class="invalid-reason">{{ row.reason }}</span>
                </li>
              </ul>
            </section>

            <div v-if="willMoveRows.length === 0 && collisionGroups.length === 0 && invalidRows.length === 0" class="state-msg">
              Nothing to organize — every audiobook is already at its canonical folder.
            </div>
          </template>
        </div>

        <footer v-if="!results" class="modal-footer">
          <template v-if="!pendingConfirm">
            <div class="footer-summary">
              <span v-if="!loading && preview">
                {{ selectedCount }} of {{ willMoveRows.length }} ready to move
                · {{ formatBytes(selectedBytes) }}
              </span>
              <span v-if="applyError" class="error">{{ applyError }}</span>
            </div>
            <div class="footer-actions">
              <button type="button" class="btn" :disabled="applying" @click="onClose">Close</button>
              <button
                type="button"
                class="btn btn-primary"
                :disabled="applying || loading || selectedCount === 0"
                @click="pendingConfirm = true"
              >
                Apply…
              </button>
            </div>
          </template>

          <template v-else-if="results">
            <div class="results-panel">
              <div class="results-title">Move queue status</div>
              <div class="results-tally">
                <span class="pill pill-action">{{ runningCount }} in flight</span>
                <span class="pill pill-ok">{{ completedCount }} completed</span>
                <span class="pill pill-err">{{ failedCount }} failed</span>
                <span class="results-meta">{{ queuedCount }} queued · {{ skippedCount }} skipped · {{ failedToQueueCount }} failed to queue</span>
              </div>
              <ul v-if="failedJobs.length > 0" class="results-failures">
                <li v-for="job in failedJobs" :key="job.jobId">
                  <strong>{{ jobTitleFor(job.jobId) || `id ${jobAudiobookFor(job.jobId)}` }}</strong>
                  <span class="results-error">{{ jobErrorFor(job.jobId) || '(no error text)' }}</span>
                </li>
              </ul>
              <ul v-if="skippedDetails.length > 0" class="results-failures">
                <li v-for="(s, i) in skippedDetails" :key="`skip-${i}`">
                  <strong>id {{ s.audiobookId }}</strong>
                  <span class="results-error">{{ s.reason }}</span>
                </li>
              </ul>
              <div class="confirm-actions">
                <button type="button" class="btn" @click="onClose">Close</button>
              </div>
            </div>
          </template>

          <template v-else>
            <div class="confirm-panel">
              <div class="confirm-title">About to queue {{ selectedCount }} folder move{{ selectedCount === 1 ? '' : 's' }}:</div>
              <ul class="confirm-list">
                <li>
                  <strong>{{ selectedCount }}</strong> audiobook{{ selectedCount === 1 ? '' : 's' }} totaling
                  {{ formatBytes(selectedBytes) }} will be moved to <code>{Author}/{Title}</code>.
                </li>
                <li>
                  Each move runs through the existing background queue with the same safety checks
                  as the per-book Organize button.
                </li>
              </ul>
              <div class="confirm-warning">
                File moves are queued but execute on disk. If your FolderNamingPattern is
                misconfigured, books will land in the wrong place. Back up the SQLite DB before
                running a real sweep on a live library.
              </div>
              <div class="confirm-actions">
                <button type="button" class="btn" :disabled="applying" @click="pendingConfirm = false">
                  Back
                </button>
                <button type="button" class="btn btn-primary" :disabled="applying" @click="executeApply">
                  {{ applying ? 'Queuing…' : 'Confirm and queue moves' }}
                </button>
              </div>
              <div v-if="applyError" class="error confirm-error">{{ applyError }}</div>
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
import { useToast } from '@/services/toastService'
import { signalRService } from '@/services/signalr'
import { errorTracking } from '@/services/errorTracking'
import MoveQueueStatusBanner from '@/components/domain/maintenance/MoveQueueStatusBanner.vue'
import type {
  OrganizeLibraryPreview,
  OrganizeLibraryApplyResult,
  OrganizePreviewRow,
} from '@/types'

interface JobState {
  audiobookId: number
  audiobookTitle: string | null
  targetPath: string | null
  status: string
  error: string | null
}

const props = defineProps<{ visible: boolean }>()
const emit = defineEmits<{
  (e: 'close'): void
  (e: 'organized', result: OrganizeLibraryApplyResult): void
}>()

const toast = useToast()

const loading = ref(false)
const loadError = ref<string | null>(null)
const preview = ref<OrganizeLibraryPreview | null>(null)
const selected = reactive<Record<number, boolean>>({})
const applying = ref(false)
const applyError = ref<string | null>(null)
const pendingConfirm = ref(false)
const results = ref<OrganizeLibraryApplyResult | null>(null)
const jobs = reactive<Record<string, JobState>>({})
let unsubMoveJob: (() => void) | null = null

const queuedCount = computed(() => results.value?.queued ?? 0)
const skippedCount = computed(() => results.value?.skipped ?? 0)
const failedToQueueCount = computed(() => results.value?.failedToQueue ?? 0)
const skippedDetails = computed(() => results.value?.skippedDetails ?? [])

const failedJobs = computed(() => {
  if (!results.value) return []
  return results.value.queuedJobs.filter((j) => jobs[j.jobId]?.status === 'Failed')
})
const completedCount = computed(() => {
  if (!results.value) return 0
  return results.value.queuedJobs.filter((j) => jobs[j.jobId]?.status === 'Completed').length
})
const failedCount = computed(() => failedJobs.value.length)
const runningCount = computed(() => {
  if (!results.value) return 0
  return results.value.queuedJobs.length - completedCount.value - failedCount.value
})

function jobTitleFor(jobId: string): string | null {
  return jobs[jobId]?.audiobookTitle || null
}
function jobAudiobookFor(jobId: string): number | null {
  return jobs[jobId]?.audiobookId ?? null
}
function jobErrorFor(jobId: string): string | null {
  return jobs[jobId]?.error || null
}

const willMoveRows = computed(() => preview.value?.rows.filter((r) => r.status === 'will_move') ?? [])
const invalidRows = computed(() => preview.value?.rows.filter((r) => r.status === 'invalid_target') ?? [])
const collisionRows = computed(() => preview.value?.rows.filter((r) => r.status === 'collision') ?? [])

interface CollisionGroup {
  key: string
  targetPath: string
  rows: OrganizePreviewRow[]
}

const collisionGroups = computed<CollisionGroup[]>(() => {
  const byKey = new Map<string, CollisionGroup>()
  for (const r of collisionRows.value) {
    const key = r.collisionKey || r.targetPath || `id-${r.id}`
    const existing = byKey.get(key)
    if (existing) {
      existing.rows.push(r)
    } else {
      byKey.set(key, { key, targetPath: r.targetPath || '(unknown)', rows: [r] })
    }
  }
  return Array.from(byKey.values()).sort((a, b) => a.targetPath.localeCompare(b.targetPath))
})

const selectedCount = computed(() => willMoveRows.value.filter((r) => selected[r.id] === true).length)
const selectedBytes = computed(() =>
  willMoveRows.value.filter((r) => selected[r.id] === true).reduce((acc, r) => acc + (r.totalSize || 0), 0),
)

function onToggleRow(id: number, checked: boolean) {
  selected[id] = checked
}

function toggleAll(checked: boolean) {
  for (const r of willMoveRows.value) selected[r.id] = checked
}

async function load() {
  loading.value = true
  loadError.value = null
  applyError.value = null
  for (const k of Object.keys(selected)) delete selected[Number(k)]
  for (const k of Object.keys(jobs)) delete jobs[k]
  results.value = null
  unsubscribeMoveJobs()
  pendingConfirm.value = false
  try {
    const resp = await apiService.getOrganizeLibraryPreview()
    preview.value = resp
    // Default every will_move row to selected so the user starts from "move everything".
    for (const r of resp.rows) {
      if (r.status === 'will_move') selected[r.id] = true
    }
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : 'Unknown error'
    errorTracking.captureException(err as Error, {
      component: 'OrganizeLibraryModal',
      operation: 'load',
    })
  } finally {
    loading.value = false
  }
}

async function executeApply() {
  applyError.value = null
  const ids = willMoveRows.value.filter((r) => selected[r.id] === true).map((r) => r.id)
  if (ids.length === 0) {
    applyError.value = 'Nothing selected to move.'
    return
  }
  applying.value = true
  try {
    const result = await apiService.applyOrganizeLibrary(ids)
    emit('organized', result)
    // Seed jobs state from the apply response so the results panel can
    // render immediately; SignalR MoveJobUpdate then fills in error text
    // as each background move finishes (Completed or Failed).
    for (const k of Object.keys(jobs)) delete jobs[k]
    for (const j of result.queuedJobs) {
      jobs[j.jobId] = {
        audiobookId: j.audiobookId,
        audiobookTitle: j.audiobookTitle,
        targetPath: j.targetPath,
        status: 'Queued',
        error: null,
      }
    }
    subscribeMoveJobs()
    results.value = result
    const parts: string[] = [`Queued ${result.queued} move${result.queued === 1 ? '' : 's'}`]
    if (result.skipped > 0) parts.push(`${result.skipped} skipped`)
    if (result.failedToQueue > 0) parts.push(`${result.failedToQueue} failed to queue`)
    const summary = parts.join(', ')
    if (result.failedToQueue > 0 || result.warnings.length > 0) {
      toast.warning('Organize library', summary)
    } else {
      toast.success('Organize library', summary)
    }
  } catch (err) {
    applyError.value = err instanceof Error ? err.message : 'Unknown error'
    errorTracking.captureException(err as Error, {
      component: 'OrganizeLibraryModal',
      operation: 'apply',
    })
  } finally {
    applying.value = false
  }
}

function subscribeMoveJobs() {
  if (unsubMoveJob) return
  unsubMoveJob = signalRService.onMoveJobUpdate((job) => {
    if (!job || !job.jobId) return
    const existing = jobs[job.jobId]
    if (!existing) return // not one of ours
    existing.status = job.status
    existing.error = job.error || null
  })
}

function unsubscribeMoveJobs() {
  if (unsubMoveJob) {
    try { unsubMoveJob() } catch { /* ignore */ }
    unsubMoveJob = null
  }
}

function onClose() {
  if (applying.value) return
  unsubscribeMoveJobs()
  emit('close')
}

function formatBytes(bytes: number): string {
  if (!bytes || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let n = bytes
  let i = 0
  while (n >= 1024 && i < units.length - 1) {
    n /= 1024
    i++
  }
  return `${n.toFixed(n >= 100 || i === 0 ? 0 : 1)} ${units[i]}`
}

watch(
  () => props.visible,
  (v) => {
    if (v) {
      preview.value = null
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
.help-text {
  font-size: 12px;
  color: #bbb;
  margin: 0 0 14px;
  line-height: 1.5;
}
.help-text code {
  background: rgba(255, 255, 255, 0.06);
  padding: 1px 4px;
  border-radius: 3px;
  font-size: 11px;
}
.bucket-summary {
  display: flex;
  gap: 8px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}
.pill {
  display: inline-flex;
  align-items: center;
  padding: 3px 9px;
  border-radius: 999px;
  font-size: 11px;
  border: 1px solid transparent;
}
.pill-action {
  background: rgba(42, 111, 208, 0.16);
  color: #6fa8e8;
  border-color: rgba(42, 111, 208, 0.25);
}
.pill-ok {
  background: rgba(96, 192, 128, 0.12);
  color: #80c896;
  border-color: rgba(96, 192, 128, 0.2);
}
.pill-warn {
  background: rgba(240, 176, 96, 0.12);
  color: #e8b070;
  border-color: rgba(176, 128, 64, 0.3);
}
.pill-err {
  background: rgba(240, 96, 96, 0.12);
  color: #f06060;
  border-color: rgba(176, 64, 64, 0.3);
}
.results-panel {
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.results-title {
  font-size: 13px;
  color: #fff;
  font-weight: 600;
}
.results-tally {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  align-items: center;
}
.results-meta {
  font-size: 11px;
  color: #999;
}
.results-failures {
  margin: 0;
  padding-left: 16px;
  color: #ddd;
  font-size: 12px;
  max-height: 220px;
  overflow-y: auto;
}
.results-failures li {
  margin-bottom: 6px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.results-failures strong {
  color: #fff;
  font-size: 12px;
}
.results-error {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-size: 11px;
  color: #f06060;
  white-space: pre-wrap;
  word-break: break-word;
}
.section {
  margin-bottom: 18px;
  border: 1px solid rgba(255, 255, 255, 0.05);
  border-radius: 6px;
  background: rgba(255, 255, 255, 0.015);
}
.section-header {
  padding: 10px 14px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.section-header h3 {
  margin: 0;
  font-size: 13px;
  color: #fff;
}
.section-help {
  width: 100%;
  margin: 6px 0 0;
  font-size: 12px;
  color: #aaa;
  line-height: 1.45;
}
.section-tools {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 12px;
  color: #999;
}
.section-count {
  color: #ccc;
}
.link {
  background: transparent;
  border: 0;
  color: #6fa8e8;
  font-size: 12px;
  cursor: pointer;
  padding: 0;
}
.link:hover {
  text-decoration: underline;
}
.rows {
  padding: 4px 0;
}
.row {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 8px 14px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
  cursor: pointer;
}
.row:last-child {
  border-bottom: 0;
}
.row:hover {
  background: rgba(255, 255, 255, 0.02);
}
.row input[type='checkbox'] {
  margin-top: 4px;
  flex-shrink: 0;
}
.row-body {
  flex: 1;
  min-width: 0;
}
.row-title {
  display: flex;
  gap: 10px;
  align-items: baseline;
  flex-wrap: wrap;
}
.row-title strong {
  color: #fff;
  font-size: 13px;
}
.row-author {
  color: #aaa;
  font-size: 12px;
}
.row-paths {
  margin-top: 4px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.row-path {
  display: flex;
  gap: 6px;
  font-size: 11px;
  color: #999;
  overflow: hidden;
}
.path-label {
  flex-shrink: 0;
  color: #777;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.path-value {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.path-target {
  color: #80c896;
}
.row-meta {
  margin-top: 3px;
  font-size: 11px;
  color: #777;
}
.collision-group {
  padding: 10px 14px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
}
.collision-group:last-child {
  border-bottom: 0;
}
.collision-target {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-size: 12px;
  color: #e8b070;
  margin-bottom: 6px;
}
.collision-members {
  margin: 0;
  padding-left: 16px;
  color: #ccc;
  font-size: 12px;
}
.collision-members li {
  margin-bottom: 4px;
}
.invalid-list {
  margin: 0;
  padding: 10px 14px 10px 30px;
  color: #ccc;
  font-size: 12px;
}
.invalid-list li {
  margin-bottom: 4px;
}
.invalid-reason {
  color: #e8b070;
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
.confirm-list code {
  background: rgba(255, 255, 255, 0.06);
  padding: 1px 4px;
  border-radius: 3px;
}
.confirm-warning {
  font-size: 12px;
  color: #f0b060;
  padding: 6px 10px;
  background: rgba(240, 176, 96, 0.08);
  border-left: 3px solid #b08040;
  border-radius: 3px;
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
.error {
  color: #f06060;
}
.modal-fade-enter-active,
.modal-fade-leave-active {
  transition: opacity 0.18s ease;
}
.modal-fade-enter-from,
.modal-fade-leave-to {
  opacity: 0;
}
</style>
