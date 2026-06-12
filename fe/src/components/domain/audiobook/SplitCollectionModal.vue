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
  Split a multi-book record: server-side clustering (subdirectory / filename
  stem) with a suggested existing library record per group; applying runs one
  file transfer per confirmed group, so files move to their canonical folders
  and each destination re-verifies automatically. Born from a live 773-file,
  34-book collection dump.
-->
<template>
  <Modal :visible="visible" size="lg" title="Split collection" @close="onClose">
    <ModalBody>
      <div v-if="loading" class="split-empty">Analyzing files…</div>
      <div v-else-if="error" class="split-error">{{ error }}</div>
      <template v-else>
        <p class="split-intro">
          {{ clusters.length }} group{{ clusters.length === 1 ? '' : 's' }} detected. Each
          confirmed group's files move to the chosen record (and into its folder), and the
          destination is re-verified automatically. Groups without a destination stay put.
        </p>
        <div class="split-clusters">
          <div v-for="c in clusters" :key="c.key" class="split-cluster">
            <label class="split-cluster-head">
              <input type="checkbox" v-model="c.include" :disabled="!c.targetId" />
              <span class="split-cluster-name">
                <strong>{{ c.displayName }}</strong>
                <small>{{ c.fileIds.length }} file{{ c.fileIds.length === 1 ? '' : 's' }}</small>
              </span>
            </label>
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
                  </button>
                  <div v-if="candidatesFor(c).length === 0" class="split-empty">No matches.</div>
                </div>
              </template>
              <template v-else>
                <span v-if="c.targetId" class="split-dest-label">
                  → {{ c.targetTitle }}
                  <small>id {{ c.targetId }}</small>
                </span>
                <span v-else class="split-dest-label split-dest-none">no destination</span>
                <button type="button" class="split-change-btn" @click="c.editing = true">
                  {{ c.targetId ? 'Change…' : 'Choose…' }}
                </button>
              </template>
            </div>
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
        :disabled="applying || includedCount === 0"
        @click="apply"
      >
        {{ applying ? 'Moving…' : `Move ${includedCount} group${includedCount === 1 ? '' : 's'}` }}
      </button>
    </div>
  </Modal>
</template>

<script setup lang="ts">
import { ref, computed, watch, reactive } from 'vue'
import { Modal, ModalBody } from '@/components/feedback'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { useLibraryStore } from '@/stores/library'
import type { Audiobook } from '@/types'

interface ClusterRow {
  key: string
  displayName: string
  fileIds: number[]
  fileNames: string[]
  targetId: number | null
  targetTitle: string | null
  include: boolean
  editing: boolean
  query: string
}

const props = defineProps<{
  visible: boolean
  audiobook: Audiobook | null
}>()

const emit = defineEmits<{
  (e: 'close'): void
  (e: 'done', result: { groupsMoved: number; filesMoved: number }): void
}>()

const toast = useToast()
const libraryStore = useLibraryStore()

const loading = ref(false)
const error = ref<string | null>(null)
const clusters = ref<ClusterRow[]>([])
const applying = ref(false)
const progressText = ref('')

const includedCount = computed(
  () => clusters.value.filter((c) => c.include && c.targetId).length,
)

watch(
  () => props.visible,
  async (visible) => {
    if (!visible || !props.audiobook) return
    loading.value = true
    error.value = null
    clusters.value = []
    applying.value = false
    if (libraryStore.audiobooks.length === 0) {
      void libraryStore.fetchLibrary().catch(() => {})
    }
    try {
      const preview = await apiService.getSplitPreview(props.audiobook.id)
      clusters.value = preview.clusters.map((c) =>
        reactive({
          key: c.key,
          displayName: c.displayName,
          fileIds: c.fileIds,
          fileNames: c.fileNames,
          targetId: c.suggestedTargetId ?? null,
          targetTitle: c.suggestedTargetTitle ?? null,
          include: !!c.suggestedTargetId,
          editing: false,
          query: c.displayName,
        }),
      )
    } catch (err) {
      error.value = err instanceof Error ? err.message : 'Could not analyze this record.'
    } finally {
      loading.value = false
    }
  },
)

function tokenize(text: string): string[] {
  return (text || '')
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter((t) => t.length > 1)
}

function candidatesFor(c: ClusterRow) {
  const sourceId = props.audiobook?.id
  const tokens = tokenize(c.query)
  if (tokens.length === 0) return []
  return libraryStore.audiobooks
    .filter((b) => b.id !== sourceId)
    .map((b) => {
      const titleTokens = new Set(tokenize(b.title || ''))
      const score = tokens.reduce((s, t) => s + (titleTokens.has(t) ? 1 : 0), 0)
      return { id: b.id, title: b.title || '', score }
    })
    .filter((x) => x.score > 0)
    .sort((a, b) => b.score - a.score || a.title.localeCompare(b.title))
    .slice(0, 8)
}

function pickTarget(c: ClusterRow, cand: { id: number; title: string }) {
  c.targetId = cand.id
  c.targetTitle = cand.title
  c.include = true
  c.editing = false
}

async function apply() {
  const book = props.audiobook
  if (!book || applying.value) return
  const todo = clusters.value.filter((c) => c.include && c.targetId)
  if (todo.length === 0) return

  applying.value = true
  let groupsMoved = 0
  let filesMoved = 0
  const failures: string[] = []
  for (const [index, c] of todo.entries()) {
    progressText.value = `Moving "${c.displayName}" (${index + 1}/${todo.length})…`
    try {
      const result = await apiService.transferAudiobookFiles(book.id, c.targetId!, c.fileIds)
      groupsMoved++
      filesMoved += result.transferred
      if (result.warnings?.length) {
        failures.push(`${c.displayName}: ${result.warnings.join(' ')}`)
      }
    } catch (err) {
      failures.push(`${c.displayName}: ${err instanceof Error ? err.message : 'failed'}`)
    }
  }
  applying.value = false

  if (failures.length > 0) {
    toast.warning(
      `Moved ${groupsMoved}/${todo.length} groups (${filesMoved} files)`,
      failures.slice(0, 3).join(' · '),
    )
  } else {
    toast.success(
      'Collection split',
      `Moved ${filesMoved} file${filesMoved === 1 ? '' : 's'} across ${groupsMoved} book${groupsMoved === 1 ? '' : 's'}. Each destination is re-verifying.`,
    )
  }
  emit('done', { groupsMoved, filesMoved })
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
  gap: 0.6rem;
  cursor: pointer;
}

.split-cluster-name {
  display: flex;
  align-items: baseline;
  gap: 0.6rem;
}

.split-cluster-name small {
  color: #8a93a0;
}

.split-cluster-dest {
  margin: 0.4rem 0 0 1.6rem;
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
  margin: 0.45rem 0 0 1.6rem;
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
