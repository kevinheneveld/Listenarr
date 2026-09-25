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
  <div class="tab-content">
    <div class="maintenance-tab">
      <div class="section-header">
        <h3>Maintenance</h3>
        <p class="section-subtitle">
          Library-wide housekeeping: organize folders, repair broken tracking, and resolve duplicate
          records. Nothing here changes application settings — every action previews first or asks
          before it writes.
        </p>
      </div>

      <div class="settings-form">
        <div id="settings-section-maintenance"><LibraryMaintenanceSection /></div>
        <div id="settings-section-duplicates"><DuplicatesSection /></div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import LibraryMaintenanceSection from '@/components/settings/LibraryMaintenanceSection.vue'
import DuplicatesSection from '@/components/settings/DuplicatesSection.vue'
</script>

<style scoped>
.maintenance-tab .section-subtitle {
  margin: 0.25rem 0 0;
  color: var(--text-secondary, #9aa3ad);
  font-size: 0.9rem;
  max-width: 60ch;
}
</style>
