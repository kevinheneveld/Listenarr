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
    <h3><PhSparkle /> AI Assist</h3>
    <p class="section-help">
      An optional OpenAI-compatible endpoint (Ollama, LM Studio, OpenRouter, OpenAI, …) used for
      fuzzy text judgments — currently smarter destination matching in Split Collection. Everything
      keeps working without it; features quietly fall back to their built-in behavior whenever the
      endpoint is off, asleep, or unreachable.
    </p>

    <div class="setting-row">
      <label for="ai-assist-enabled">
        <strong>Enable AI assist</strong>
        <small>Master switch — nothing below is contacted while this is off.</small>
      </label>
      <input
        id="ai-assist-enabled"
        type="checkbox"
        :checked="settings.aiAssistEnabled ?? false"
        @change="patch('aiAssistEnabled', ($event.target as HTMLInputElement).checked)"
      />
    </div>

    <div class="setting-row">
      <label for="ai-assist-base-url">
        <strong>Base URL</strong>
        <small
          >The server's /v1 root, e.g. <code>http://192.168.1.20:11434/v1</code> for Ollama.
          Ollama binds localhost only by default — start it with
          <code>OLLAMA_HOST=0.0.0.0 ollama serve</code> to reach it from this machine.</small
        >
      </label>
      <input
        id="ai-assist-base-url"
        type="text"
        placeholder="http://192.168.1.20:11434/v1"
        :value="settings.aiAssistBaseUrl ?? ''"
        @change="patch('aiAssistBaseUrl', ($event.target as HTMLInputElement).value.trim())"
      />
    </div>

    <div class="setting-row">
      <label for="ai-assist-model">
        <strong>Model</strong>
        <small>Exactly as the server names it, e.g. <code>qwen2.5:7b</code>.</small>
      </label>
      <input
        id="ai-assist-model"
        type="text"
        placeholder="qwen2.5:7b"
        :value="settings.aiAssistModel ?? ''"
        @change="patch('aiAssistModel', ($event.target as HTMLInputElement).value.trim())"
      />
    </div>

    <div class="setting-row">
      <label for="ai-assist-api-key">
        <strong>API key</strong>
        <small>Only for hosted services (OpenRouter, OpenAI). Leave empty for local servers.</small>
      </label>
      <input
        id="ai-assist-api-key"
        type="password"
        autocomplete="off"
        :value="settings.aiAssistApiKey ?? ''"
        @change="patch('aiAssistApiKey', ($event.target as HTMLInputElement).value.trim())"
      />
    </div>

    <div class="setting-row">
      <label for="ai-assist-gate-searches">
        <strong>Screen automatic-search picks</strong>
        <small
          >Before grabbing, the model checks the chosen release name against the target book and
          skips obvious music albums or wrong books. Fails open — an unreachable endpoint never
          blocks a grab.</small
        >
      </label>
      <input
        id="ai-assist-gate-searches"
        type="checkbox"
        :checked="settings.aiAssistGateSearches ?? true"
        @change="patch('aiAssistGateSearches', ($event.target as HTMLInputElement).checked)"
      />
    </div>

    <div class="setting-row">
      <label>
        <strong>Connection test</strong>
        <small>Tests the values as entered above — no need to save first.</small>
      </label>
      <div class="test-cell">
        <button type="button" class="test-btn" :disabled="testing" @click="testConnection">
          {{ testing ? 'Testing…' : 'Test connection' }}
        </button>
        <small v-if="testResult" :class="testOk ? 'test-ok' : 'test-fail'">{{ testResult }}</small>
      </div>
    </div>

    <div class="setting-row sweep-row">
      <label>
        <strong>Vet library by file names</strong>
        <small
          >Reviews unsettled records ({{ sweepBatchSize }} per run) — the model flags ones whose
          files look like something else entirely (music, a different book, a collection). Flag
          only: nothing is changed; each run continues where the last left off.</small
        >
      </label>
      <div class="test-cell">
        <button type="button" class="test-btn" :disabled="sweepStopRequested" @click="runSweep">
          {{ sweeping ? 'Stop' : sweepCursor > 0 ? 'Continue sweep' : 'Sweep library' }}
        </button>
        <small v-if="sweepStatus" class="sweep-status">{{ sweepStatus }}</small>
      </div>
    </div>

    <div v-if="sweepFindings.length > 0" class="sweep-findings">
      <strong>Flagged records</strong>
      <ul>
        <li v-for="finding in sweepFindings" :key="finding.audiobookId">
          <router-link :to="`/audiobooks/${finding.audiobookId}`" target="_blank">
            {{ finding.title }}
          </router-link>
          <span class="sweep-reason"> — {{ finding.reason }}</span>
          <span v-if="finding.evidence" class="sweep-evidence"> ({{ finding.evidence }})</span>
        </li>
      </ul>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { PhSparkle } from '@phosphor-icons/vue'
import { apiService } from '@/services/api'
import type { ApplicationSettings } from '@/types'

const props = defineProps<{ settings: Partial<ApplicationSettings> }>()
const emit = defineEmits<{ (e: 'update:settings', value: Partial<ApplicationSettings>): void }>()

const testing = ref(false)
const testResult = ref<string | null>(null)
const testOk = ref(false)

// One model call per request — a multi-batch request outlives reverse-proxy
// timeouts (a 25-record run 504'd behind openresty). The loop below chains
// small requests instead, so each stays well under any proxy limit.
const sweepBatchSize = 10
const sweeping = ref(false)
const sweepStopRequested = ref(false)
const sweepStatus = ref<string | null>(null)
const sweepCursor = ref(0)
const sweepChecked = ref(0)
const sweepFindings = ref<
  { audiobookId: number; title: string; reason: string; evidence?: string }[]
>([])

function patch(field: keyof ApplicationSettings, value: unknown) {
  emit('update:settings', {
    ...(props.settings || {}),
    [field]: value,
  } as Partial<ApplicationSettings>)
}

async function runSweep() {
  if (sweeping.value) {
    // The button doubles as Stop while a sweep is looping; the current
    // in-flight batch finishes, then the loop exits.
    sweepStopRequested.value = true
    sweepStatus.value = 'Stopping after the current batch…'
    return
  }

  sweeping.value = true
  sweepStopRequested.value = false
  sweepStatus.value = 'Sweeping…'
  try {
    // Chain small requests until the backlog is exhausted (or Stop): each
    // request is a single model call, short enough for any reverse proxy.
    for (;;) {
      const result = await apiService.runAiLibrarySweep(sweepBatchSize, sweepCursor.value)
      sweepChecked.value += result.checkedCount
      // Accumulate across batches; dedupe on re-runs of the same slice.
      const known = new Set(sweepFindings.value.map((f) => f.audiobookId))
      sweepFindings.value = [
        ...sweepFindings.value,
        ...result.suspicious.filter((s) => !known.has(s.audiobookId)),
      ]
      if (result.exhausted) {
        sweepCursor.value = 0
        sweepStatus.value = `Backlog swept — ${sweepChecked.value} record(s) checked, ${sweepFindings.value.length} flagged.`
        break
      }
      sweepCursor.value = result.lastId ?? 0
      sweepStatus.value = `${sweepChecked.value} checked, ${sweepFindings.value.length} flagged…`
      if (sweepStopRequested.value) {
        sweepStatus.value = `Paused — ${sweepChecked.value} checked, ${sweepFindings.value.length} flagged. Sweep again to continue.`
        break
      }
    }
  } catch (err) {
    sweepStatus.value = err instanceof Error ? err.message : 'Sweep failed.'
  } finally {
    sweeping.value = false
    sweepStopRequested.value = false
  }
}

async function testConnection() {
  testing.value = true
  testResult.value = null
  try {
    // Send the form's CURRENT values — what you see is what gets tested,
    // saved or not.
    const result = await apiService.testAiAssist({
      baseUrl: props.settings.aiAssistBaseUrl ?? '',
      model: props.settings.aiAssistModel ?? '',
      apiKey: props.settings.aiAssistApiKey ?? '',
    })
    testOk.value = result.ok
    testResult.value = result.detail
  } catch (err) {
    testOk.value = false
    testResult.value = err instanceof Error ? err.message : 'Test failed.'
  } finally {
    testing.value = false
  }
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

.section-help {
  margin: 0 0 1rem;
  color: #868e96;
  font-size: 0.85rem;
  line-height: 1.45;
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
  flex: 1;
}

.setting-row label small {
  color: #868e96;
  font-size: 0.8rem;
  line-height: 1.4;
}

.setting-row label small code {
  color: #a5c9e8;
}

.setting-row input[type='text'],
.setting-row input[type='password'] {
  width: 18rem;
  max-width: 45%;
  padding: 0.45rem 0.6rem;
  background: rgba(0, 0, 0, 0.3);
  border: 1px solid rgba(255, 255, 255, 0.15);
  border-radius: 4px;
  color: #fff;
}

.test-cell {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 0.35rem;
  max-width: 45%;
}

.test-btn {
  padding: 0.45rem 0.9rem;
  background: rgba(255, 255, 255, 0.06);
  border: 1px solid rgba(255, 255, 255, 0.18);
  border-radius: 4px;
  color: #fff;
  cursor: pointer;
}
.test-btn:hover:not(:disabled) {
  background: rgba(255, 255, 255, 0.12);
}
.test-btn:disabled {
  opacity: 0.6;
  cursor: default;
}

.test-ok {
  color: #69db7c;
}
.test-fail {
  color: #ff8a8a;
}

.sweep-status {
  color: #868e96;
  text-align: right;
}

.sweep-findings {
  margin-top: 0.75rem;
  padding: 0.75rem 1rem;
  background: rgba(220, 90, 74, 0.08);
  border: 1px solid rgba(220, 90, 74, 0.3);
  border-radius: 6px;
  font-size: 0.85rem;
}
.sweep-findings strong {
  color: #fff;
}
.sweep-findings ul {
  margin: 0.5rem 0 0;
  padding-left: 1.2rem;
}
.sweep-findings li {
  margin: 0.25rem 0;
}
.sweep-findings a {
  color: #7cb5ec;
  text-decoration: none;
}
.sweep-findings a:hover {
  text-decoration: underline;
}
.sweep-reason {
  color: #f0b3ab;
}
.sweep-evidence {
  color: #868e96;
  font-family: monospace;
  font-size: 0.8rem;
}
</style>
