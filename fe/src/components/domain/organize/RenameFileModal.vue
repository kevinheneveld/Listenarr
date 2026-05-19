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
  <Modal :visible="visible" size="md" :title="title" @close="onClose">
    <template #header>
      <ModalHeader :title="title" @close="onClose">
        <template #icon><PhPencil /></template>
      </ModalHeader>
    </template>

    <ModalBody>
      <p class="rename-file-intro">
        Rename this file on disk and update the library record. Other files in this audiobook are
        not affected.
      </p>
      <p v-if="loading" class="rename-file-loading">Loading current path…</p>
      <p v-else-if="loadError" class="rename-file-error" role="alert">{{ loadError }}</p>
      <template v-else>
        <div class="rename-file-field">
          <label class="field-label" for="rename-file-input">Filename</label>
          <input
            id="rename-file-input"
            ref="inputRef"
            v-model="newName"
            type="text"
            class="form-control"
            :disabled="submitting"
            spellcheck="false"
            autocomplete="off"
            @keydown.enter.prevent="onSubmit"
          />
          <small v-if="validationError" class="rename-file-error">{{ validationError }}</small>
          <small v-else class="rename-file-hint"
            >Extension is preserved. Path separators are not allowed.</small
          >
        </div>
        <dl class="rename-file-preview" v-if="newPath">
          <dt>Current</dt>
          <dd class="rename-path-value--current">{{ currentPath || '—' }}</dd>
          <dt>New</dt>
          <dd class="rename-path-value--new">{{ newPath }}</dd>
        </dl>
        <p v-if="submitError" class="rename-file-error" role="alert">{{ submitError }}</p>
      </template>
    </ModalBody>

    <template #footer>
      <button class="cancel-button btn" :disabled="submitting" @click="onClose">Cancel</button>
      <button class="btn btn-primary" :disabled="!canSubmit" @click="onSubmit">
        <PhSpinner v-if="submitting" class="spinner" /> Rename
      </button>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { Modal, ModalHeader, ModalBody } from '@/components/feedback'
import { PhPencil, PhSpinner } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import type { RenameResult } from '@/types'

interface FileLike {
  id: number
}

const props = defineProps<{
  visible: boolean
  audiobookId: number | null
  file: FileLike | null
}>()

const emit = defineEmits<{
  (e: 'close'): void
  (e: 'done', result: RenameResult): void
}>()

const title = 'Rename file'

const inputRef = ref<HTMLInputElement | null>(null)
const newName = ref('')
const submitting = ref(false)
const submitError = ref<string | null>(null)
const loading = ref(false)
const loadError = ref<string | null>(null)
const currentPath = ref('')

const directory = computed(() => splitPath(currentPath.value).directory)
const currentFilename = computed(() => splitPath(currentPath.value).filename)
const currentExtension = computed(() => extractExtension(currentFilename.value))
const currentStem = computed(() => stripExtension(currentFilename.value))

const newFilename = computed(() => {
  const raw = newName.value.trim()
  if (!raw) return ''
  const stripped = stripExtension(raw, currentExtension.value)
  return currentExtension.value ? `${stripped}${currentExtension.value}` : raw
})

const newPath = computed(() => {
  if (!newFilename.value) return ''
  return joinPathSegment(directory.value, newFilename.value)
})

const validationError = computed(() => {
  const value = newName.value.trim()
  if (!value) return null
  if (/[\\/]/.test(value)) return 'Filename cannot contain path separators.'
  if (/[<>:"|?*]/.test(value)) return 'Filename contains characters that are not allowed.'
  return null
})

const canSubmit = computed(() => {
  if (submitting.value || loading.value) return false
  if (!props.audiobookId || !props.file) return false
  if (!currentPath.value || !newPath.value) return false
  if (validationError.value) return false
  return !pathsEqual(currentPath.value, newPath.value)
})

watch(
  () => [props.visible, props.audiobookId, props.file?.id] as const,
  ([visible, audiobookId, fileId]) => {
    if (!visible) return
    void hydrate(audiobookId, fileId ?? null)
  },
  { immediate: true },
)

async function hydrate(audiobookId: number | null, fileId: number | null) {
  newName.value = ''
  currentPath.value = ''
  submitError.value = null
  loadError.value = null
  if (!audiobookId || !fileId) return
  loading.value = true
  try {
    const preview = await apiService.previewRenameAudiobook(audiobookId)
    const entry = preview.fileRenames?.find((f) => f.fileId === fileId)
    const path = entry?.currentPath ?? ''
    if (!path) {
      loadError.value = 'Could not resolve the current file path.'
      return
    }
    currentPath.value = path
    newName.value = stripExtension(splitPath(path).filename)
    void nextTick(() => {
      inputRef.value?.focus()
      inputRef.value?.select()
    })
  } catch (err) {
    loadError.value = err instanceof Error ? err.message : 'Failed to load current path.'
  } finally {
    loading.value = false
  }
}

function onClose() {
  if (submitting.value) return
  emit('close')
}

async function onSubmit() {
  if (!canSubmit.value || !props.audiobookId || !props.file) return
  submitting.value = true
  submitError.value = null
  try {
    const result = await apiService.executeRenameAudiobook(props.audiobookId, {
      audiobookId: props.audiobookId,
      fileRenames: [
        {
          fileId: props.file.id,
          currentPath: currentPath.value,
          newPath: newPath.value,
        },
      ],
    })
    const fileResult = result.renamedFiles?.find((r) => r.fileId === props.file?.id)
    if (result.success && fileResult?.success !== false) {
      emit('done', result)
      return
    }
    submitError.value = fileResult?.error || result.error || 'Failed to rename file.'
  } catch (err) {
    submitError.value = err instanceof Error ? err.message : 'Failed to rename file.'
  } finally {
    submitting.value = false
  }
}

function splitPath(p: string): { directory: string; filename: string } {
  if (!p) return { directory: '', filename: '' }
  const normalized = p.replace(/\\/g, '/')
  const idx = normalized.lastIndexOf('/')
  if (idx < 0) return { directory: '', filename: p }
  const separator = p.includes('\\') ? '\\' : '/'
  return {
    directory: p.slice(0, idx).replace(/\//g, separator),
    filename: p.slice(idx + 1),
  }
}

function joinPathSegment(directory: string, filename: string): string {
  if (!directory) return filename
  const useBackslash = directory.includes('\\') && !directory.includes('/')
  const sep = useBackslash ? '\\' : '/'
  return directory.endsWith('/') || directory.endsWith('\\')
    ? `${directory}${filename}`
    : `${directory}${sep}${filename}`
}

function extractExtension(filename: string): string {
  const idx = filename.lastIndexOf('.')
  if (idx <= 0) return ''
  return filename.slice(idx)
}

function stripExtension(filename: string, expected?: string): string {
  if (expected && filename.toLowerCase().endsWith(expected.toLowerCase())) {
    return filename.slice(0, -expected.length)
  }
  const ext = extractExtension(filename)
  return ext ? filename.slice(0, -ext.length) : filename
}

function pathsEqual(a: string, b: string): boolean {
  return a.replace(/\\/g, '/').toLowerCase() === b.replace(/\\/g, '/').toLowerCase()
}
</script>

<style scoped>
.rename-file-intro {
  margin: 0 0 1rem 0;
}

.rename-file-loading {
  margin: 0 0 1rem 0;
  color: var(--text-muted, #888);
}

.rename-file-field {
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
  margin-bottom: 1rem;
}

.field-label {
  font-weight: 600;
}

.rename-file-hint {
  color: var(--text-muted, #888);
}

.rename-file-error {
  color: var(--error-color, #c53030);
}

.rename-file-preview {
  display: grid;
  grid-template-columns: max-content 1fr;
  column-gap: 0.75rem;
  row-gap: 0.35rem;
  margin: 0;
}

.rename-file-preview dt {
  font-weight: 600;
  color: var(--text-muted, #888);
}

.rename-file-preview dd {
  margin: 0;
  word-break: break-all;
}

.rename-path-value--new {
  color: var(--text-primary, inherit);
}

.spinner {
  animation: rename-file-spin 1s linear infinite;
  margin-right: 0.35rem;
}

@keyframes rename-file-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
