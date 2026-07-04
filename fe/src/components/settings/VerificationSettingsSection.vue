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
  <div class="settings-section">
    <h3><PhWaveform /> Audio Verification</h3>

    <div class="setting-row">
      <label for="verification-opening-seconds">
        <strong>Opening sample window (seconds)</strong>
        <small
          >How much of the book's start whisper listens to for spoken credits. Generous by default
          ({{ 90 }}s) because publisher idents and music intros often precede a cold open.</small
        >
      </label>
      <input
        id="verification-opening-seconds"
        type="number"
        min="10"
        max="600"
        :value="settings.verificationOpeningSeconds ?? 90"
        @change="patchNumber('verificationOpeningSeconds', $event, 10, 600)"
      />
    </div>

    <div class="setting-row">
      <label for="verification-closing-seconds">
        <strong>Closing sample window (seconds)</strong>
        <small>How much of the book's end is checked — closing credits are a short repeat.</small>
      </label>
      <input
        id="verification-closing-seconds"
        type="number"
        min="0"
        max="300"
        :value="settings.verificationClosingSeconds ?? 30"
        @change="patchNumber('verificationClosingSeconds', $event, 0, 300)"
      />
    </div>

    <div class="setting-row">
      <label for="verification-escalation-model">
        <strong>Escalation model</strong>
        <small
          >When the first pass can't reach a confident match, the same samples are re-read with this
          larger model (downloaded on first use, ~466&nbsp;MB for small.en). medium.en is noticeably
          slower.</small
        >
      </label>
      <select
        id="verification-escalation-model"
        :value="settings.verificationEscalationModel ?? 'small.en'"
        @change="patch('verificationEscalationModel', ($event.target as HTMLSelectElement).value)"
      >
        <option value="">Disabled</option>
        <option value="small.en">small.en</option>
        <option value="medium.en">medium.en (slow)</option>
      </select>
    </div>

    <div class="setting-row">
      <label for="verification-on-import">
        <strong>Verify new imports automatically</strong>
        <small>Queue a verification pass whenever a download finishes importing.</small>
      </label>
      <input
        id="verification-on-import"
        type="checkbox"
        :checked="settings.verificationOnImport ?? true"
        @change="patch('verificationOnImport', ($event.target as HTMLInputElement).checked)"
      />
    </div>

    <div class="setting-row">
      <label for="verification-auto-reject">
        <strong>Auto-reject confident wrong content</strong>
        <small
          >When a fresh import's audio announces a different book (heard credits) at high
          confidence, automatically purge it, blocklist the release, and re-search. Books with no
          spoken credits are never auto-rejected.</small
        >
      </label>
      <input
        id="verification-auto-reject"
        type="checkbox"
        :checked="settings.verificationAutoRejectWrongContent ?? true"
        @change="
          patch('verificationAutoRejectWrongContent', ($event.target as HTMLInputElement).checked)
        "
      />
    </div>

    <div class="setting-row">
      <label for="verification-low-cpu">
        <strong>Run verification at low CPU priority</strong>
        <small
          >Whisper/ffmpeg yield to anything else on the host instead of competing at normal priority
          during long library walks.</small
        >
      </label>
      <input
        id="verification-low-cpu"
        type="checkbox"
        :checked="settings.verificationLowCpuPriority ?? true"
        @change="patch('verificationLowCpuPriority', ($event.target as HTMLInputElement).checked)"
      />
    </div>
  </div>
</template>

<script setup lang="ts">
import { PhWaveform } from '@phosphor-icons/vue'
import type { ApplicationSettings } from '@/types'

const props = defineProps<{ settings: Partial<ApplicationSettings> }>()
const emit = defineEmits<{ (e: 'update:settings', value: Partial<ApplicationSettings>): void }>()

function patch(field: keyof ApplicationSettings, value: unknown) {
  emit('update:settings', {
    ...(props.settings || {}),
    [field]: value,
  } as Partial<ApplicationSettings>)
}

function patchNumber(field: keyof ApplicationSettings, event: Event, min: number, max: number) {
  const raw = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(raw)) return
  patch(field, Math.min(max, Math.max(min, Math.round(raw))))
}
</script>

<style scoped>
.settings-section {
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 8px;
  padding: 1.5rem;
  margin-bottom: 1.5rem;
}

.settings-section h3 {
  margin: 0 0 1rem 0;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: #fff;
  font-size: 1.1rem;
}

.setting-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
  padding: 0.6rem 0;
}

.setting-row label {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
  color: #fff;
}

.setting-row label small {
  color: #868e96;
  font-size: 0.85rem;
  line-height: 1.4;
}

.setting-row select {
  width: 170px;
  padding: 0.5rem;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  background: rgba(0, 0, 0, 0.2);
  color: #fff;
  flex-shrink: 0;
}

.setting-row input[type='number'] {
  width: 90px;
  padding: 0.5rem;
  border: 1px solid rgba(255, 255, 255, 0.1);
  border-radius: 6px;
  background: rgba(0, 0, 0, 0.2);
  color: #fff;
  flex-shrink: 0;
}

.setting-row input[type='checkbox'] {
  flex-shrink: 0;
}
</style>
