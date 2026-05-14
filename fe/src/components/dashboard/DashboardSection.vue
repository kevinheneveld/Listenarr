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
<!-- Titled container for a dashboard section. The #actions slot holds
     section-level controls (e.g. the activity granularity toggle). -->
<script setup lang="ts">
import type { Component } from 'vue'

withDefaults(
  defineProps<{
    title: string
    icon?: Component
  }>(),
  { icon: undefined },
)
</script>

<template>
  <section class="dashboard-section">
    <div class="section-header">
      <h2>
        <component :is="icon" v-if="icon" />
        {{ title }}
      </h2>
      <div class="section-actions">
        <slot name="actions" />
      </div>
    </div>
    <div class="section-body">
      <slot />
    </div>
  </section>
</template>

<style scoped>
.dashboard-section {
  background: #232323;
  border: 1px solid #333;
  border-radius: 8px;
  padding: 1.5rem;
  box-shadow: 0 6px 18px rgba(0, 0, 0, 0.25);
}

.section-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 1rem;
  margin-bottom: 1.5rem;
  padding-bottom: 1rem;
  border-bottom: 1px solid #333;
}

.section-header h2 {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  margin: 0;
  color: #fff;
  font-size: 1.25rem;
  font-weight: 500;
}

.section-header h2 :deep(svg) {
  font-size: 1.4rem;
  color: var(--brand-focus);
}

.section-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

.section-body {
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
}
</style>
