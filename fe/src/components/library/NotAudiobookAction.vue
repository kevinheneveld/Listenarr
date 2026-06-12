<script setup lang="ts">
import { ref } from 'vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'

const props = defineProps<{
  audiobookId?: number
}>()

const emit = defineEmits<{
  // Fired after the wrong files are removed and a re-search has been kicked off, so the
  // parent can close its delete dialog and refresh. Passed the count of files removed.
  (e: 'done', filesRemoved: number): void
}>()

const rejecting = ref(false)

async function execute() {
  if (props.audiobookId == null) return

  rejecting.value = true
  const toast = useToast()
  try {
    const res = await apiService.rejectNotAudiobook(props.audiobookId)
    const searchMsg =
      res.searchQueued > 0
        ? `Started a search and queued ${res.searchQueued} download(s).`
        : 'Started a new search — nothing better grabbed yet, so the book is back to wanted.'
    toast.success('Content replaced', `Removed ${res.filesRemoved} file(s). ${searchMsg}`)
    if (res.warnings && res.warnings.length > 0) {
      toast.warning('Some files could not be removed', res.warnings.join(' '))
    }
    emit('done', res.filesRemoved)
  } catch {
    toast.error('Action failed', 'Could not remove the content for this book.')
  } finally {
    rejecting.value = false
  }
}
</script>

<template>
  <div class="not-audiobook-option">
    <div class="not-audiobook-text">
      <span class="not-audiobook-title">Not the right content?</span>
      <small
        >Keep the entry but remove these files — wrong book, a non-audiobook release that matched
        the title, or an incomplete copy — and search for a correct version instead of deleting
        the whole book.</small
      >
    </div>
    <button
      type="button"
      class="not-audiobook-button"
      :disabled="rejecting || audiobookId == null"
      @click="execute"
    >
      {{ rejecting ? 'Working…' : 'Wrong content — find a better copy' }}
    </button>
  </div>
</template>

<style scoped>
.not-audiobook-option {
  margin-top: 1.25rem;
  padding-top: 1rem;
  border-top: 1px solid rgba(255, 255, 255, 0.1);
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  text-align: left;
}

.not-audiobook-text {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.not-audiobook-title {
  color: #f5f7fa;
  font-weight: 600;
}

.not-audiobook-text small {
  color: #b9c0c8;
  line-height: 1.4;
}

.not-audiobook-button {
  align-self: flex-start;
  padding: 0.55rem 0.9rem;
  border-radius: 10px;
  border: 1px solid rgba(var(--brand-rgb), 0.45);
  background: rgba(var(--brand-rgb), 0.12);
  color: #f5f7fa;
  font-weight: 600;
  cursor: pointer;
  transition:
    border-color 0.2s ease,
    background-color 0.2s ease;
}

.not-audiobook-button:hover:not(:disabled) {
  border-color: rgba(var(--brand-rgb), 0.7);
  background: rgba(var(--brand-rgb), 0.2);
}

.not-audiobook-button:disabled {
  opacity: 0.6;
  cursor: default;
}
</style>
