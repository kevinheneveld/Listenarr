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
  Resolution proposals for records holding duplicate copies of their own audio,
  with graded evidence and apply controls: hash-identical proposals can be
  applied in bulk, duration-matched ones individually behind a confirmation.
  Every application is re-verified server-side against a fresh analysis before
  anything is deleted. Review-tier proposals and split-candidates never get an
  apply button here.
-->
<template>
  <div class="copy-analysis">
    <div class="copy-analysis-header">
      <button type="button" class="copy-analyze-btn" :disabled="loading || applying" @click="analyze">
        {{ loading ? 'Analyzing…' : hasLoaded ? 'Re-analyze copies' : 'Analyze copies' }}
      </button>
      <button
        v-if="identicalApplications.length > 0"
        type="button"
        class="copy-apply-all-btn"
        :disabled="applying || loading"
        @click="applyAllIdentical"
      >
        {{
          applying
            ? 'Applying…'
            : `Apply all ${identicalApplications.length} hash-identical (${formatSize(identicalBytes)})`
        }}
      </button>
      <small v-if="hasLoaded && !loading" class="copy-summary">
        {{ records.length }} record{{ records.length === 1 ? '' : 's' }} ·
        {{ formatSize(totalReclaimable) }} reclaimable
      </small>
    </div>

    <div v-if="error" class="copy-error">{{ error }}</div>

    <template v-if="hasLoaded && !loading && !error">
      <div v-if="records.length === 0" class="copy-empty">
        No resolvable duplicate copies found.
      </div>
      <div v-for="r in records" :key="r.id" class="copy-record">
        <div class="copy-record-head">
          <router-link :to="`/audiobooks/${r.id}`" class="copy-title">{{ r.title }}</router-link>
          <small class="copy-meta">{{ r.fileCount }} files · id {{ r.id }}</small>
          <span v-if="r.splitCandidate" class="copy-chip copy-chip-split"
            >looks like multiple books — use Split Collection</span
          >
        </div>
        <div v-for="(p, i) in r.proposals" :key="i" class="copy-proposal">
          <div class="copy-clusters">
            <div class="copy-cluster copy-cluster-keeper">
              <span class="copy-chip copy-chip-keep">keep</span>
              <span class="copy-cluster-name">{{ p.keeper.displayName }}</span>
              <small class="copy-meta">{{ clusterMeta(p.keeper) }}</small>
            </div>
            <div v-for="c in p.redundant" :key="c.key" class="copy-cluster">
              <span class="copy-chip copy-chip-redundant">redundant</span>
              <span class="copy-cluster-name">{{ c.displayName }}</span>
              <small class="copy-meta">{{ clusterMeta(c) }}</small>
              <small v-if="c.onDiskCount < c.fileCount" class="copy-missing">
                {{ c.fileCount - c.onDiskCount }} file(s) missing on disk
              </small>
            </div>
          </div>
          <div class="copy-verdict">
            <span class="copy-chip" :class="`copy-chip-${p.confidence}`">{{
              confidenceLabel(p.confidence)
            }}</span>
            <small class="copy-meta">{{ formatSize(p.reclaimableBytes) }} reclaimable</small>
            <button
              v-if="p.confidence === 'identical' || p.confidence === 'high'"
              type="button"
              class="copy-apply-btn"
              :disabled="applying || loading"
              @click="applyOne(r, p)"
            >
              Apply
            </button>
          </div>
          <ul class="copy-evidence">
            <li v-for="(e, j) in p.evidence" :key="j">{{ e }}</li>
          </ul>
        </div>
      </div>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { apiService } from '@/services/api'
import { useToast } from '@/services/toastService'
import { showConfirm } from '@/composables/useConfirm'
import type {
  DuplicateCopyAnalysisRecord,
  DuplicateCopyApplication,
  DuplicateCopyCluster,
  DuplicateCopyProposal,
} from '@/types'

const toast = useToast()

const loading = ref(false)
const applying = ref(false)
const hasLoaded = ref(false)
const error = ref<string | null>(null)
const records = ref<DuplicateCopyAnalysisRecord[]>([])
const totalReclaimable = ref(0)

const identicalProposals = computed(() =>
  records.value.flatMap((r) =>
    r.proposals
      .filter((p) => p.confidence === 'identical')
      .map((p) => ({ record: r, proposal: p })),
  ),
)

const identicalApplications = computed<DuplicateCopyApplication[]>(() =>
  identicalProposals.value.map(({ record, proposal }) => ({
    audiobookId: record.id,
    redundantFileIds: proposal.redundant.flatMap((c) => c.fileIds),
  })),
)

const identicalBytes = computed(() =>
  identicalProposals.value.reduce((n, { proposal }) => n + proposal.reclaimableBytes, 0),
)

async function analyze() {
  loading.value = true
  error.value = null
  try {
    const resp = await apiService.getDuplicateCopyAnalysis()
    records.value = resp.records
    totalReclaimable.value = resp.totalReclaimableBytes
    hasLoaded.value = true
  } catch (err) {
    error.value = err instanceof Error ? err.message : 'Copy analysis failed.'
  } finally {
    loading.value = false
  }
}

async function runApply(applications: DuplicateCopyApplication[]) {
  applying.value = true
  try {
    const result = await apiService.applyDuplicateCopies(applications)
    const refused = result.results.filter((x) => !x.applied)
    const summary = `Deleted ${result.totalFilesDeleted} file(s), freed ${formatSize(result.totalFreedBytes)}.`
    if (refused.length > 0) {
      toast.warning(
        'Applied with refusals',
        `${summary} ${refused.length} proposal(s) refused (re-verify failed) — re-analyze.`,
      )
    } else {
      toast.success('Duplicate copies removed', summary)
    }
    await analyze()
  } catch (err) {
    toast.error('Apply failed', err instanceof Error ? err.message : 'unknown error')
  } finally {
    applying.value = false
  }
}

async function applyAllIdentical() {
  const apps = identicalApplications.value
  const ok = await showConfirm(
    `Delete the redundant copies from ${apps.length} record(s), freeing ${formatSize(identicalBytes.value)}?\n\n` +
      `Every proposal in this batch is hash-confirmed byte-identical to the copy being kept, ` +
      `and each is re-verified server-side before deletion. This cannot be undone.`,
    'Apply all hash-identical proposals',
    { danger: true, confirmText: 'Delete redundant copies', cancelText: 'Cancel' },
  )
  if (!ok) return
  await runApply(apps)
}

async function applyOne(record: DuplicateCopyAnalysisRecord, proposal: DuplicateCopyProposal) {
  const strong = proposal.confidence === 'identical'
  const ok = await showConfirm(
    `Delete ${proposal.redundant.reduce((n, c) => n + c.fileCount, 0)} redundant file(s) from ` +
      `"${record.title}", freeing ${formatSize(proposal.reclaimableBytes)}?\n\n` +
      (strong
        ? `The copies are hash-confirmed byte-identical to the keeper.`
        : `The copies are a DIFFERENT encode of the same audio (runtimes agree within 2%) — ` +
          `the keeper is the higher-bitrate one. Double-check the evidence below if unsure.`) +
      `\nRe-verified server-side before deletion. This cannot be undone.`,
    'Apply duplicate-copy proposal',
    { danger: true, confirmText: 'Delete redundant copy', cancelText: 'Cancel' },
  )
  if (!ok) return
  await runApply([
    {
      audiobookId: record.id,
      redundantFileIds: proposal.redundant.flatMap((c) => c.fileIds),
    },
  ])
}

function clusterMeta(c: DuplicateCopyCluster): string {
  const parts = [`${c.fileCount} file${c.fileCount === 1 ? '' : 's'}`, formatSize(c.totalBytes)]
  if (c.totalDurationSeconds > 0) parts.push(`${(c.totalDurationSeconds / 3600).toFixed(1)}h`)
  return parts.join(' · ')
}

function confidenceLabel(confidence: string): string {
  if (confidence === 'identical') return 'hash-identical'
  if (confidence === 'high') return 'same audio (duration match)'
  return 'needs review'
}

function formatSize(bytes: number): string {
  if (bytes >= 1024 ** 3) return `${(bytes / 1024 ** 3).toFixed(1)} GB`
  if (bytes >= 1024 ** 2) return `${(bytes / 1024 ** 2).toFixed(0)} MB`
  return `${Math.max(1, Math.round(bytes / 1024))} KB`
}
</script>

<style scoped>
.copy-analysis {
  margin-top: 0.6rem;
}

.copy-analysis-header {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.copy-analyze-btn {
  padding: 4px 12px;
  background-color: rgba(var(--brand-rgb), 0.1);
  border: 1px solid var(--brand-500);
  border-radius: 4px;
  color: var(--brand-500);
  font-size: 13px;
  cursor: pointer;
}

.copy-apply-all-btn {
  padding: 4px 12px;
  background: rgba(46, 204, 113, 0.1);
  border: 1px solid rgba(46, 204, 113, 0.4);
  border-radius: 4px;
  color: #2ecc71;
  font-size: 13px;
  cursor: pointer;
}

.copy-apply-btn {
  margin-left: auto;
  padding: 2px 10px;
  font-size: 0.78rem;
  background-color: rgba(255, 107, 107, 0.1);
  color: #ff6b6b;
  border: 1px solid rgba(255, 107, 107, 0.3);
  border-radius: 4px;
  cursor: pointer;
}

.copy-analyze-btn:disabled,
.copy-apply-all-btn:disabled,
.copy-apply-btn:disabled {
  opacity: 0.6;
  cursor: default;
}

.copy-summary,
.copy-meta {
  color: #8a93a0;
  font-size: 0.78rem;
}

.copy-error {
  margin-top: 0.5rem;
  color: #e74c3c;
  font-size: 0.9rem;
}

.copy-empty {
  color: #8a93a0;
  font-size: 0.85rem;
  padding: 0.4rem 0;
}

.copy-record {
  margin-top: 0.6rem;
  padding: 0.5rem 0.7rem;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
}

.copy-record-head {
  display: flex;
  align-items: baseline;
  gap: 0.6rem;
  flex-wrap: wrap;
}

.copy-title {
  color: #4dabf7;
  text-decoration: none;
}

.copy-title:hover {
  text-decoration: underline;
}

.copy-proposal {
  margin-top: 0.4rem;
  padding-left: 0.3rem;
  border-left: 2px solid rgba(255, 255, 255, 0.08);
}

.copy-clusters {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
}

.copy-cluster {
  display: flex;
  align-items: baseline;
  gap: 0.5rem;
  flex-wrap: wrap;
  padding-left: 0.4rem;
}

.copy-cluster-name {
  color: #d5dbe1;
  font-size: 0.85rem;
}

.copy-chip {
  font-size: 0.68rem;
  padding: 1px 8px;
  border-radius: 999px;
  white-space: nowrap;
}

.copy-chip-keep,
.copy-chip-identical {
  background: rgba(46, 204, 113, 0.12);
  color: #2ecc71;
  border: 1px solid rgba(46, 204, 113, 0.3);
}

.copy-chip-redundant {
  background: rgba(255, 107, 107, 0.1);
  color: #ff6b6b;
  border: 1px solid rgba(255, 107, 107, 0.3);
}

.copy-chip-high {
  background: rgba(77, 171, 247, 0.12);
  color: #4dabf7;
  border: 1px solid rgba(77, 171, 247, 0.3);
}

.copy-chip-review,
.copy-chip-split {
  background: rgba(243, 156, 18, 0.12);
  color: #f39c12;
  border: 1px solid rgba(243, 156, 18, 0.3);
}

.copy-verdict {
  display: flex;
  align-items: baseline;
  gap: 0.5rem;
  margin-top: 0.3rem;
  padding-left: 0.4rem;
}

.copy-evidence {
  margin: 0.25rem 0 0.2rem;
  padding-left: 1.4rem;
  color: #8a93a0;
  font-size: 0.75rem;
}

.copy-missing {
  color: #f39c12;
  font-size: 0.75rem;
}
</style>
