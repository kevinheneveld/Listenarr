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
<!-- A single KPI tile: big value, label, optional sub-label and icon. -->
<script setup lang="ts">
import type { Component } from 'vue'

withDefaults(
  defineProps<{
    label: string
    value: string | number
    sublabel?: string
    icon?: Component
    tone?: 'default' | 'success' | 'warning' | 'danger'
  }>(),
  {
    sublabel: undefined,
    icon: undefined,
    tone: 'default',
  },
)
</script>

<template>
  <div class="kpi-card" :class="`tone-${tone}`">
    <div class="kpi-icon" v-if="icon">
      <component :is="icon" />
    </div>
    <div class="kpi-body">
      <div class="kpi-value">{{ value }}</div>
      <div class="kpi-label">{{ label }}</div>
      <div class="kpi-sublabel" v-if="sublabel">{{ sublabel }}</div>
    </div>
  </div>
</template>

<style scoped>
.kpi-card {
  display: flex;
  align-items: center;
  gap: 1rem;
  background: #232323;
  border: 1px solid #333;
  border-left: 3px solid var(--brand-focus);
  border-radius: 8px;
  padding: 1.25rem;
  box-shadow: 0 6px 18px rgba(0, 0, 0, 0.25);
  transition:
    transform 0.2s,
    border-color 0.2s;
}

.kpi-card:hover {
  transform: translateY(-2px);
  border-color: #444;
}

.kpi-card.tone-success {
  border-left-color: var(--success-600);
}

.kpi-card.tone-warning {
  border-left-color: var(--warning-500);
}

.kpi-card.tone-danger {
  border-left-color: var(--danger-600);
}

.kpi-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  width: 2.75rem;
  height: 2.75rem;
  border-radius: 8px;
  background: #2a2a2a;
}

.kpi-icon :deep(svg) {
  font-size: 1.5rem;
  color: var(--brand-focus);
}

.tone-success .kpi-icon :deep(svg) {
  color: var(--success-600);
}

.tone-warning .kpi-icon :deep(svg) {
  color: var(--warning-500);
}

.tone-danger .kpi-icon :deep(svg) {
  color: var(--danger-600);
}

.kpi-body {
  min-width: 0;
}

.kpi-value {
  color: #fff;
  font-size: 1.75rem;
  font-weight: 600;
  line-height: 1.1;
}

.kpi-label {
  color: #bbb;
  font-size: 0.9rem;
  margin-top: 0.15rem;
}

.kpi-sublabel {
  color: #777;
  font-size: 0.8rem;
  margin-top: 0.25rem;
}
</style>
