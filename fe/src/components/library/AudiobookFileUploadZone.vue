<script setup lang="ts">
// Manual import drop zone: drag audio files (or a libro.fm-style zip) onto a
// book's Files tab and they are uploaded into the book's library folder and
// registered — the way in for purchases the download pipeline never sees.
import { computed, ref } from 'vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'

const props = defineProps<{
  audiobookId: number
}>()

const emit = defineEmits<{
  (e: 'uploaded', filesAdded: number): void
}>()

const ACCEPTED_EXTENSIONS = [
  '.m4b',
  '.mp3',
  '.flac',
  '.ogg',
  '.opus',
  '.m4a',
  '.aac',
  '.wav',
  '.wv',
  '.wma',
  '.ape',
  '.alac',
  '.aif',
  '.aiff',
  '.zip',
]

interface UploadItem {
  name: string
  size: number
  progress: number
  status: 'pending' | 'uploading' | 'done' | 'error' | 'skipped'
  message?: string
}

const fileInput = ref<HTMLInputElement | null>(null)
const dragActive = ref(false)
const uploading = ref(false)
const queue = ref<UploadItem[]>([])

const acceptAttr = ACCEPTED_EXTENSIONS.join(',')
const activeItems = computed(() => queue.value.filter((q) => q.status !== 'done'))

function hasAcceptedExtension(name: string): boolean {
  const lower = name.toLowerCase()
  return ACCEPTED_EXTENSIONS.some((ext) => lower.endsWith(ext))
}

function onDragOver(event: DragEvent) {
  event.preventDefault()
  dragActive.value = true
}

function onDragLeave() {
  dragActive.value = false
}

function onDrop(event: DragEvent) {
  event.preventDefault()
  dragActive.value = false
  const files = Array.from(event.dataTransfer?.files ?? [])
  if (files.length) void uploadFiles(files)
}

function onBrowse() {
  fileInput.value?.click()
}

function onFileInputChange(event: Event) {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''
  if (files.length) void uploadFiles(files)
}

async function uploadFiles(files: File[]) {
  if (uploading.value) return
  uploading.value = true
  const toast = useToast()

  const accepted: Array<{ file: File; item: UploadItem }> = []
  queue.value = []
  for (const file of files) {
    const item: UploadItem = {
      name: file.name,
      size: file.size,
      progress: 0,
      status: 'pending',
    }
    if (!hasAcceptedExtension(file.name)) {
      item.status = 'skipped'
      item.message = 'Not an audio file'
    } else {
      accepted.push({ file, item })
    }
    queue.value.push(item)
  }

  let totalAdded = 0
  for (const { file, item } of accepted) {
    item.status = 'uploading'
    try {
      const res = await apiService.uploadAudiobookFile(props.audiobookId, file, (fraction) => {
        item.progress = fraction
      })
      item.progress = 1
      if (res.uploaded > 0) {
        item.status = 'done'
        totalAdded += res.uploaded
        if (res.skipped?.length) {
          item.message = res.skipped.map((s) => `${s.name}: ${s.reason}`).join('; ')
        }
      } else {
        item.status = 'skipped'
        item.message = res.skipped?.map((s) => s.reason).join('; ') || 'Nothing imported'
      }
    } catch (err) {
      item.status = 'error'
      item.message = err instanceof Error ? err.message : 'Upload failed'
    }
  }

  uploading.value = false

  if (totalAdded > 0) {
    toast.success(
      'Files imported',
      `${totalAdded} file(s) added to this audiobook's library folder.`,
    )
    emit('uploaded', totalAdded)
  } else if (accepted.length > 0) {
    const firstError = queue.value.find((q) => q.message)?.message
    toast.error('Nothing imported', firstError || 'No usable audio files in the upload.')
  } else {
    toast.error('Nothing imported', 'Only audio files and zip archives are supported.')
  }
}
</script>

<template>
  <div
    class="upload-zone"
    :class="{ 'drag-active': dragActive, uploading }"
    @dragover="onDragOver"
    @dragleave="onDragLeave"
    @drop="onDrop"
  >
    <input
      ref="fileInput"
      type="file"
      class="upload-input"
      multiple
      :accept="acceptAttr"
      @change="onFileInputChange"
    />
    <button type="button" class="upload-prompt" :disabled="uploading" @click="onBrowse">
      <span class="upload-title">
        {{ uploading ? 'Uploading…' : 'Drag & drop audio files here' }}
      </span>
      <small v-if="!uploading" class="upload-hint">
        or click to browse — audio files or a zip; they're filed into this book's folder
      </small>
    </button>

    <ul v-if="activeItems.length" class="upload-queue">
      <li v-for="item in queue" :key="item.name" class="upload-item" :class="item.status">
        <span class="upload-item-name">{{ item.name }}</span>
        <span v-if="item.status === 'uploading'" class="upload-item-progress">
          <span class="upload-bar">
            <span class="upload-bar-fill" :style="{ width: `${Math.round(item.progress * 100)}%` }" />
          </span>
          {{ Math.round(item.progress * 100) }}%
        </span>
        <span v-else class="upload-item-status">
          {{
            item.status === 'done'
              ? 'Imported'
              : item.status === 'error'
                ? (item.message ?? 'Failed')
                : item.status === 'skipped'
                  ? (item.message ?? 'Skipped')
                  : 'Waiting…'
          }}
        </span>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.upload-zone {
  border: 2px dashed var(--border-color, rgba(255, 255, 255, 0.2));
  border-radius: 8px;
  padding: 1rem;
  margin-bottom: 1rem;
  transition:
    border-color 0.15s ease,
    background-color 0.15s ease;
}

.upload-zone.drag-active {
  border-color: var(--primary-color, #3b82f6);
  background-color: rgba(59, 130, 246, 0.08);
}

.upload-input {
  display: none;
}

.upload-prompt {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 0.25rem;
  width: 100%;
  padding: 0.5rem;
  background: none;
  border: none;
  cursor: pointer;
  color: inherit;
}

.upload-prompt:disabled {
  cursor: default;
  opacity: 0.7;
}

.upload-title {
  font-weight: 600;
}

.upload-hint {
  opacity: 0.65;
}

.upload-queue {
  list-style: none;
  margin: 0.75rem 0 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.upload-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  font-size: 0.85rem;
}

.upload-item-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.upload-item.error .upload-item-status {
  color: var(--danger-color, #ef4444);
}

.upload-item.skipped .upload-item-status {
  opacity: 0.65;
}

.upload-item.done .upload-item-status {
  color: var(--success-color, #22c55e);
}

.upload-item-progress {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-shrink: 0;
}

.upload-bar {
  display: inline-block;
  width: 120px;
  height: 6px;
  border-radius: 3px;
  background: rgba(127, 127, 127, 0.25);
  overflow: hidden;
}

.upload-bar-fill {
  display: block;
  height: 100%;
  background: var(--primary-color, #3b82f6);
  transition: width 0.2s ease;
}
</style>
