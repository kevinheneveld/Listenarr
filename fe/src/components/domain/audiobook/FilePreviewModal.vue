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
  In-browser preview of a single audiobook file. The browser's native
  <audio controls> element handles play/pause/seek/volume; the modal just
  provides a focused surface so the user can identify the narrator (or
  language, or audio quality) without leaving the library context.

  Auth for the stream URL flows through SessionAuthenticationMiddleware's
  cookie fallback so no header plumbing is needed on this side.
-->
<template>
  <Modal :visible="visible" size="md" :overlay-z-index="overlayZIndex" @close="onClose">
    <template #header>
      <ModalHeader :title="title" @close="onClose" />
    </template>

    <template #default>
      <ModalBody>
        <div class="preview-meta">
          <div class="preview-meta-line">
            <PhFileAudio />
            <span class="preview-filename" :title="file?.path || ''">{{ displayFilename }}</span>
          </div>
          <div v-if="metaLine" class="preview-meta-sub">{{ metaLine }}</div>
        </div>

        <audio
          v-if="visible && streamUrl"
          ref="audioEl"
          class="preview-audio"
          :src="streamUrl"
          controls
          preload="metadata"
          @error="onAudioError"
        ></audio>

        <p v-if="errorMessage" class="preview-error">
          <PhWarning />
          <span>{{ errorMessage }}</span>
        </p>
        <p v-else class="preview-help">
          Use the scrubber to skip into the file — the narrator's voice is usually
          identifiable a minute or two in, past any intro music.
        </p>
      </ModalBody>
    </template>

    <template #footer>
      <button class="btn btn-secondary" @click="onClose">Close</button>
    </template>
  </Modal>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { PhFileAudio, PhWarning } from '@phosphor-icons/vue'
import { Modal, ModalBody, ModalHeader } from '@/components/feedback'
import { apiService } from '@/services/api'

interface FileLike {
  id: number
  path?: string | null
  format?: string | null
  durationSeconds?: number | null
  size?: number | null
}

interface Props {
  visible: boolean
  audiobookId: number | null
  file: FileLike | null
  audiobookTitle?: string | null
  // Optional z-index override for the overlay. Set higher than the parent
  // modal's overlay z-index when opening this preview from inside another
  // modal (e.g., MetadataBackfillModal uses 3100, so pass 3200).
  overlayZIndex?: number
}

interface Emits {
  (e: 'close'): void
}

const props = defineProps<Props>()
const emit = defineEmits<Emits>()

const audioEl = ref<HTMLAudioElement | null>(null)
const errorMessage = ref<string | null>(null)

const streamUrl = computed(() => {
  if (!props.audiobookId || !props.file?.id) return ''
  return apiService.getFileStreamUrl(props.audiobookId, props.file.id)
})

const displayFilename = computed(() => {
  const p = props.file?.path || ''
  // Trim down to just the filename for display; full path lives in tooltip.
  const parts = p.split(/[\\/]/)
  return parts[parts.length - 1] || p || '(unknown file)'
})

const title = computed(() => {
  const base = props.audiobookTitle?.trim()
  return base ? `Preview — ${base}` : 'Preview audio'
})

const metaLine = computed(() => {
  if (!props.file) return ''
  const bits: string[] = []
  if (props.file.format) bits.push(props.file.format.toUpperCase())
  if (props.file.durationSeconds && Number.isFinite(props.file.durationSeconds)) {
    bits.push(formatDuration(props.file.durationSeconds))
  }
  return bits.join(' · ')
})

function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const ss = s % 60
  if (h > 0) return `${h}h ${m}m`
  if (m > 0) return `${m}m ${ss}s`
  return `${ss}s`
}

function onAudioError() {
  // The most common cause is an unsupported format on this browser
  // (e.g., a .flac on Safari) or DRM-protected .aax. Generic message keeps
  // the failure honest without trying to enumerate codecs.
  errorMessage.value =
    'This browser couldn\'t play the file. The format may be unsupported, the file may be DRM-protected, or the file may have moved on disk.'
}

function onClose() {
  // Stop playback before unmounting so we don't leave audio playing in the
  // background after the user closes the modal.
  const el = audioEl.value
  if (el) {
    try {
      el.pause()
      el.currentTime = 0
    } catch {
      /* defensive — non-fatal */
    }
  }
  errorMessage.value = null
  emit('close')
}

// Reset error state whenever a different file is loaded into the modal so
// stale "couldn't play" warnings don't leak across previews.
watch(
  () => [props.visible, props.file?.id, props.audiobookId],
  () => {
    errorMessage.value = null
  },
)
</script>

<style scoped>
.preview-meta {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  margin-bottom: 1rem;
}
.preview-meta-line {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #ddd;
  font-size: 0.95rem;
}
.preview-filename {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  min-width: 0;
}
.preview-meta-sub {
  color: #888;
  font-size: 0.8rem;
  padding-left: 1.5rem;
}
.preview-audio {
  width: 100%;
  margin-bottom: 0.75rem;
}
.preview-help {
  color: #888;
  font-size: 0.85rem;
  margin: 0;
}
.preview-error {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  color: #ff8b8b;
  font-size: 0.9rem;
  margin: 0.5rem 0 0;
}
</style>
