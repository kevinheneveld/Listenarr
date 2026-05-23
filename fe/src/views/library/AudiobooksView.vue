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
  <div class="audiobooks-view" :class="{ 'details-enabled': showItemDetails }">
    <!-- Top Toolbar -->
    <div class="toolbar">
      <div class="toolbar-left">
        <button class="toolbar-btn" @click="toggleViewMode" title="Toggle view">
          <PhGridFour v-if="viewMode === 'list'" />
          <PhList v-else />
        </button>
        <button
          class="toolbar-btn"
          :class="{ active: showItemDetails }"
          @click="toggleItemDetails"
          :aria-pressed="showItemDetails"
          title="Toggle item details"
        >
          <PhInfo />
        </button>
        <span
          v-if="
            (groupBy === 'books'
              ? audiobooks.length
              : groupedCollections
                ? groupedCollections.length
                : 0) > 0
          "
          class="count-badge"
        >
          {{
            groupBy === 'books'
              ? audiobooks.length
              : groupedCollections
                ? groupedCollections.length
                : 0
          }}
          {{ groupBy === 'books' ? 'Book' : groupBy === 'authors' ? 'Author' : 'Series'
          }}{{
            (groupBy === 'books'
              ? audiobooks.length
              : groupedCollections
                ? groupedCollections.length
                : 0) !== 1 && groupBy !== 'series'
              ? 's'
              : ''
          }}
        </span>
        <div class="group-dropdown" v-if="audiobooks.length > 0">
          <button class="toolbar-btn group-btn" @click="showGroupMenu = !showGroupMenu">
            <PhBook v-if="groupBy === 'books'" />
            <PhUser v-else-if="groupBy === 'authors'" />
            <PhBooks v-else />
            {{ groupBy === 'books' ? 'Books' : groupBy === 'authors' ? 'Authors' : 'Series' }}
            <PhCaretDown />
          </button>
          <div v-if="showGroupMenu" class="group-menu">
            <button
              class="menu-item"
              :class="{ active: groupBy === 'books' }"
              @click="setGroupBy('books')"
            >
              <PhBook />
              Books
            </button>
            <button
              class="menu-item"
              :class="{ active: groupBy === 'authors' }"
              @click="setGroupBy('authors')"
            >
              <PhUser />
              Authors
            </button>
            <button
              class="menu-item"
              :class="{ active: groupBy === 'series' }"
              @click="setGroupBy('series')"
            >
              <PhBooks />
              Series
            </button>
          </div>
        </div>
        <button class="toolbar-btn" @click="refreshLibrary">
          <PhArrowClockwise />
          Refresh
        </button>
        <button v-if="selectedCount > 0" class="toolbar-btn" @click="libraryStore.clearSelection()">
          <PhX />
          Clear Selection
        </button>
        <button
          v-if="audiobooks.length > 0 && selectedCount === 0"
          class="toolbar-btn"
          @click="libraryStore.selectAll()"
        >
          <PhCheckSquare />
          Select All
        </button>
        <button v-if="selectedCount > 0" class="toolbar-btn edit-btn" @click="showBulkEdit">
          <PhPencil />
          Edit Selected
        </button>
        <button v-if="selectedCount > 0" class="toolbar-btn" @click="showOrganize">
          <PhFolderOpen />
          Organize Selected
        </button>
        <button v-if="selectedCount > 0" class="toolbar-btn delete-btn" @click="confirmBulkDelete">
          <PhTrash />
          Delete Selected ({{ selectedCount }})
        </button>
      </div>
      <div class="toolbar-right">
        <!-- Sort / Filter controls -->
        <div class="toolbar-filters">
          <FiltersDropdown
            :customFilters="customFilters"
            v-model="selectedFilterId"
            @create="handleCreateCustomFilter"
            @edit="handleEditCustomFilter"
            @delete="handleDeleteCustomFilter"
            :active="!!selectedFilterId"
            class="toolbar-filter-dropdown"
          />
          <CustomSelect
            v-model="sortKeyProxy"
            :options="sortOptions"
            :sort-order="sortOrder"
            :current-value="sortKey"
            :active="sortKey !== (sortOptions[0]?.value || 'title') || sortOrder !== 'asc'"
            class="toolbar-custom-select"
            aria-label="Sort by"
          />
        </div>
      </div>
    </div>

    <!-- Drill-down chip: shown when arriving from a dashboard link.
         Composable — author/narrator/genre/language/missing can stack. -->
    <div v-if="hasDrilldown" class="missing-filter-chip" role="status">
      <PhFunnel />
      <span class="drilldown-summary">
        <span class="drilldown-prefix">Filtered to:</span>
        <span v-for="(item, i) in activeDrilldownItems" :key="i" class="drilldown-item">
          <span class="drilldown-item-label">{{ item.label }}:</span>
          <strong>{{ item.value }}</strong>
        </span>
        <span v-if="missingIdsLoading" class="drilldown-count">
          <PhSpinner class="ph-spin" /> loading…
        </span>
        <span v-else-if="missingIdsError" class="drilldown-count drilldown-error">
          ({{ missingIdsError }})
        </span>
        <span v-else class="drilldown-count">({{ audiobooks.length }} books)</span>
      </span>
      <button
        class="missing-filter-clear"
        @click="clearDrilldownFilters"
        title="Clear drill-down filters"
      >
        <PhX />
      </button>
    </div>

    <!-- Audiobooks Grid -->
    <div v-if="loading" class="loading-state">
      <PhSpinner class="ph-spin" />
      <p>Loading audiobooks...</p>
    </div>

    <div v-else-if="error" class="error-state">
      <div class="error-icon">
        <PhWarningCircle />
      </div>
      <h2>Error Loading Library</h2>
      <p>{{ error }}</p>
      <button @click="refreshLibrary" class="retry-button btn">
        <PhArrowClockwise />
        Retry
      </button>
    </div>

    <EmptyState
      v-else-if="rawAudiobooksLength === 0"
      :title="!hasRootFolderConfigured ? 'Root Folder Not Configured' : 'No Audiobooks Yet'"
      :message="
        !hasRootFolderConfigured
          ? 'Please configure a root folder for your audiobook library in settings before adding audiobooks.'
          : 'Your library is empty. Add audiobooks to get started!'
      "
    >
      <template #icon>
        <PhBookOpen :size="48" />
      </template>
      <template #action>
        <router-link
          :to="!hasRootFolderConfigured ? '/settings' : '/add-new'"
          class="btn btn-primary"
        >
          <PhGear v-if="!hasRootFolderConfigured" />
          <PhPlus v-else />
          {{ !hasRootFolderConfigured ? 'Go to Settings' : 'Add Audiobooks' }}
        </router-link>
      </template>
    </EmptyState>

    <!-- No results after applying filters/search -->
    <EmptyState
      v-else-if="audiobooks.length === 0"
      title="No audiobooks match your filters"
      message="Try clearing your search or filters to see results."
    >
      <template #icon>
        <PhBookOpen :size="48" />
      </template>
      <template #action>
        <div class="flex gap-sm">
          <button class="btn btn-primary" @click="clearFilters">Clear Filters</button>
          <button class="btn btn-primary" @click="refreshLibrary">Refresh Library</button>
        </div>
      </template>
    </EmptyState>

    <!-- Grouped View -->
    <div v-else-if="groupBy !== 'books'" class="grouped-view">
      <!-- List rendering for grouped collections (authors / series) -->
      <div v-if="viewMode === 'list'" class="audiobooks-list collections-list">
        <div
          v-if="groupedCollections && groupedCollections.length > 0"
          class="list-header collections-list-header"
        >
          <div class="col-cover">Cover</div>
          <div class="col-title">{{ groupBy === 'authors' ? 'Author' : 'Series' }}</div>
          <div class="col-count">Books</div>
        </div>
        <div
          v-for="collection in groupedCollections || []"
          :key="`collection-list-${collection.name}`"
          class="audiobook-list-item collection-list-item"
          :class="{
            'author-collection': groupBy === 'authors',
            'series-collection': groupBy === 'series',
          }"
          tabindex="0"
          role="button"
          :aria-label="`Open ${collection.name}`"
          @click="navigateToCollection(collection)"
          @keydown.enter.prevent="navigateToCollection(collection)"
          @keydown.space.prevent="navigateToCollection(collection)"
        >
          <img
            class="list-thumb"
            :src="
              getProtectedImageSrc(
                groupBy === 'authors'
                  ? getAuthorImageUrl(collection)
                  : collection.coverUrls && collection.coverUrls[0],
                `${groupBy}-list:${collection.name}`,
              ) || getPlaceholderUrl()
            "
            :alt="collection.name"
            loading="lazy"
            decoding="async"
            @error="handleImageError"
          />
          <div class="list-details">
            <div class="audiobook-title">{{ collection.name }}</div>
          </div>
          <div class="collection-count">
            <span
              class="collection-have-count"
              :class="{
                'count-all-good':
                  collection.readyCount > 0 && collection.qualityMismatchCount === 0,
                'count-has-mismatch':
                  collection.readyCount > 0 && collection.qualityMismatchCount > 0,
              }"
              >{{ collection.readyCount }}</span
            >
            / {{ collection.count }} book{{ collection.count !== 1 ? 's' : '' }}
          </div>
        </div>
      </div>
      <div v-else class="grouped-grid">
        <div
          v-for="collection in groupedCollections || []"
          :key="collection.name"
          :class="[
            'collection-card',
            {
              'author-collection': groupBy === 'authors',
              'series-collection': groupBy === 'series',
            },
          ]"
          @click="navigateToCollection(collection)"
        >
          <div class="collection-cover">
            <template v-if="groupBy === 'authors'">
              <div
                class="audiobook-poster-container author-poster"
                :data-author-name="collection.name"
                :data-author-has-cover="authorHasSpecificCoverMap[collection.name] ? '1' : ''"
              >
                <div class="series-count-badge">{{ collection.count }}</div>
                <div
                  class="author-placeholder"
                  :class="{ loaded: authorImageLoaded[collection.name] }"
                ></div>
                <img
                  class="audiobook-poster author-cover lazy-img"
                  :class="{ loaded: authorImageLoaded[collection.name] }"
                  :src="
                    getProtectedImageSrc(
                      getAuthorImageUrl(collection),
                      `author:${collection.name}:${getAuthorImageUrl(collection) || ''}`,
                    ) || getPlaceholderUrl()
                  "
                  :alt="collection.name"
                  loading="lazy"
                  decoding="async"
                  @error="handleAuthorImageError(collection.name, $event)"
                  @load="onAuthorImageLoad(collection.name)"
                />
                <div
                  class="author-placeholder-icon"
                  :class="{ loaded: authorImageLoaded[collection.name] }"
                >
                  <PhUser />
                </div>

                <div class="status-overlay hover-overlay">
                  <div class="audiobook-title">{{ collection.name }}</div>
                </div>

                <div class="action-buttons">
                  <button
                    class="action-btn edit-btn-small"
                    @click.stop="navigateToCollection(collection)"
                    title="Open collection"
                  >
                    <PhEye />
                  </button>
                </div>
              </div>
            </template>
            <template v-else-if="groupBy === 'series'">
              <div
                v-if="collection.coverUrls && collection.coverUrls.length > 0"
                class="series-covers-container"
              >
                <div class="series-covers">
                  <!-- Single cover: blurred background + centered cover -->
                  <template v-if="collection.coverUrls.length === 1">
                    <div
                      class="series-single-bg"
                      :style="{
                        backgroundImage: `url(${
                          getProtectedImageSrc(
                            collection.coverUrls[0],
                            `series-bg:${collection.name}:${collection.coverUrls[0] || ''}`,
                          ) || getPlaceholderUrl()
                        })`,
                      }"
                    />
                    <div
                      class="series-cover-item"
                      :style="getCoverStyle(0, collection.coverUrls.length)"
                    >
                      <img
                        :src="
                          getProtectedImageSrc(
                            collection.coverUrls[0],
                            `series:${collection.name}:0:${collection.coverUrls[0] || ''}`,
                          ) || getPlaceholderUrl()
                        "
                        :alt="`${collection.name} Cover`"
                        class="series-cover-image centered"
                        loading="lazy"
                        decoding="async"
                        @error="handleImageError"
                      />
                      <div v-if="imagesLoading" class="image-loading-overlay">
                        <PhSpinner class="ph-spin small" />
                      </div>
                    </div>
                  </template>
                  <!-- Multiple covers: distribute across container using computed offset -->
                  <template v-else>
                    <div
                      v-for="(coverUrl, index) in collection.coverUrls.slice(0, 8)"
                      :key="index"
                      class="series-cover-item"
                      :style="getCoverStyle(index, collection.coverUrls.length)"
                    >
                      <img
                        :src="
                          getProtectedImageSrc(
                            coverUrl,
                            `series:${collection.name}:${index}:${coverUrl || ''}`,
                          ) || getPlaceholderUrl()
                        "
                        :alt="`${collection.name} Cover`"
                        class="series-cover-image"
                        loading="lazy"
                        decoding="async"
                        @error="handleImageError"
                      />
                      <div v-if="imagesLoading" class="image-loading-overlay">
                        <PhSpinner class="ph-spin small" />
                      </div>
                    </div>
                  </template>
                </div>
                <!-- Book count counter -->
                <div class="series-count-badge">
                  {{ collection.count }}
                </div>
                <!-- Hover overlay (use same status-overlay as books; show only series name) -->
                <div class="status-overlay hover-overlay">
                  <div class="audiobook-title">{{ collection.name }}</div>
                </div>
              </div>
              <div v-else class="no-cover">
                <PhBooks />
              </div>
            </template>
          </div>
          <!-- Collection content for authors (match book grid bottom details) -->
          <div v-if="groupBy === 'authors'" :class="{ 'collection-content': true }">
            <div v-if="showItemDetails" class="grid-bottom-details">
              <div class="detail-line title">{{ collection.name }}</div>
              <div class="detail-line small">
                <span
                  class="collection-have-count"
                  :class="{
                    'count-all-good':
                      collection.readyCount > 0 && collection.qualityMismatchCount === 0,
                    'count-has-mismatch':
                      collection.readyCount > 0 && collection.qualityMismatchCount > 0,
                  }"
                  >{{ collection.readyCount }}</span
                >
                / {{ collection.count }} book{{ collection.count !== 1 ? 's' : '' }}
              </div>
            </div>
          </div>
          <!-- Bottom placard for series (only show when item details are enabled) -->
          <div v-if="groupBy === 'series' && showItemDetails" class="series-bottom-placard">
            <div class="series-bottom-content">
              <p class="series-bottom-title">{{ collection.name }}</p>
              <p class="series-bottom-count">
                <span
                  class="collection-have-count"
                  :class="{
                    'count-all-good':
                      collection.readyCount > 0 && collection.qualityMismatchCount === 0,
                    'count-has-mismatch':
                      collection.readyCount > 0 && collection.qualityMismatchCount > 0,
                  }"
                  >{{ collection.readyCount }}</span
                >
                / {{ collection.count }} book{{ collection.count !== 1 ? 's' : '' }}
              </p>
            </div>
          </div>
        </div>
      </div>
    </div>

    <div
      v-else
      ref="scrollContainer"
      :class="['audiobooks-scroll-container', { 'has-selection': selectedCount > 0 }]"
      @scroll="updateVisibleRange"
    >
      <div class="audiobooks-scroll-spacer" :style="{ height: totalHeight + 'px' }">
        <div
          v-if="viewMode === 'grid'"
          class="audiobooks-grid"
          :style="{ transform: `translateY(${topPadding}px)` }"
        >
          <div v-for="audiobook in visibleAudiobooks" :key="audiobook.id" class="audiobook-wrapper">
            <div
              tabindex="0"
              @keydown.enter="navigateToDetail(audiobook.id)"
              class="audiobook-item"
              :class="{
                selected: libraryStore.isSelected(audiobook.id),
                'status-no-file': getAudiobookStatus(audiobook) === 'no-file',
                'status-downloading': getAudiobookStatus(audiobook) === 'downloading',
                'status-quality-mismatch': getAudiobookStatus(audiobook) === 'quality-mismatch',
                'status-quality-match': getAudiobookStatus(audiobook) === 'quality-match',
              }"
              @click="navigateToDetail(audiobook.id)"
            >
              <div class="row-click-target" @click="navigateToDetail(audiobook.id)" />
              <div
                class="selection-checkbox"
                @click.stop="handleCheckboxClick(audiobook, $event)"
                @mousedown.prevent
              >
                <input
                  type="checkbox"
                  :checked="libraryStore.isSelected(audiobook.id)"
                  @change="onCheckboxChange(audiobook, $event)"
                  @keydown.space.prevent="handleCheckboxKeydown(audiobook, $event)"
                />
              </div>
              <div class="audiobook-poster-container" :class="{ 'show-details': showItemDetails }">
                <img
                  :src="
                    getProtectedImageSrc(
                      getBookImageUrl(audiobook),
                      `book:${audiobook.id}:${getBookImageUrl(audiobook) || ''}`,
                    ) || getPlaceholderUrl()
                  "
                  :alt="audiobook.title"
                  class="audiobook-poster"
                  loading="lazy"
                  decoding="async"
                  @error="handleImageError"
                />
                <div v-if="imagesLoading" class="image-loading-overlay">
                  <PhSpinner class="ph-spin small" />
                </div>
                <div class="status-overlay">
                  <div v-if="!showItemDetails" class="audiobook-title">
                    {{ safeText(audiobook.title) }}
                  </div>
                  <div v-if="!showItemDetails" class="audiobook-author">
                    {{
                      audiobook.authors?.map((author) => safeText(author)).join(', ') ||
                      'Unknown Author'
                    }}
                  </div>
                  <div
                    v-if="getQualityProfileName(audiobook.qualityProfileId)"
                    class="quality-profile-badge"
                  >
                    <PhStar />
                    {{ getQualityProfileName(audiobook.qualityProfileId) }}
                  </div>
                  <div class="monitored-badge" :class="{ unmonitored: !audiobook.monitored }">
                    <component :is="audiobook.monitored ? PhEye : PhEyeSlash" />
                    {{ audiobook.monitored ? 'Monitored' : 'Unmonitored' }}
                  </div>
                </div>
                <div class="action-buttons">
                  <button
                    class="action-btn edit-btn-small"
                    @click.stop="openEditModal(audiobook)"
                    title="Edit"
                  >
                    <PhPencil />
                  </button>
                  <button
                    class="action-btn delete-btn-small"
                    @click.stop="confirmDelete(audiobook)"
                    title="Delete"
                  >
                    <PhTrash />
                  </button>
                </div>
              </div>
              <!-- Extra details shown physically under poster when toggle is enabled -->
              <div v-if="showItemDetails" class="grid-bottom-details">
                <div class="detail-line title">{{ safeText(audiobook.title) }}</div>
                <div class="detail-line small">
                  {{
                    (audiobook.authors || [])
                      .slice(0, 2)
                      .map((a) => safeText(a))
                      .join(', ') || 'Unknown Author'
                  }}
                  <div v-if="(audiobook.narrators || []).length">
                    {{
                      (audiobook.narrators || [])
                        .slice(0, 1)
                        .map((n) => safeText(n))
                        .join(', ')
                    }}
                  </div>
                </div>
                <div v-if="audiobook.series" class="detail-line small">
                  Series: {{ safeText(audiobook.series)
                  }}<span v-if="audiobook.seriesNumber"> #{{ audiobook.seriesNumber }}</span>
                </div>
                <div class="detail-line small">
                  {{ safeText(audiobook.publisher)
                  }}<span v-if="audiobook.publishYear">
                    • {{ safeText(audiobook.publishYear?.toString?.() ?? '') }}</span
                  >
                </div>
                <div class="detail-line small">{{ statusText(getAudiobookStatus(audiobook)) }}</div>
              </div>
            </div>
          </div>
        </div>
        <div v-else class="audiobooks-list" :style="{ transform: `translateY(${topPadding}px)` }">
          <div v-if="audiobooks.length > 0" class="list-header">
            <div class="col-select"></div>
            <div class="col-cover">Cover</div>
            <div class="col-title">Title / Author</div>
            <div
              class="col-series sortable"
              :class="{ 'sort-active': isSeriesSortActive }"
              role="button"
              tabindex="0"
              :aria-sort="
                isSeriesSortActive ? (sortOrder === 'asc' ? 'ascending' : 'descending') : 'none'
              "
              @click="toggleListHeaderSort('series')"
              @keydown.enter.prevent="toggleListHeaderSort('series')"
              @keydown.space.prevent="toggleListHeaderSort('series')"
            >
              <span>Series</span>
              <component
                v-if="isSeriesSortActive"
                :is="sortOrder === 'asc' ? PhCaretUp : PhCaretDown"
                class="sort-caret"
              />
            </div>
            <div
              class="col-narrator sortable"
              :class="{ 'sort-active': isNarratorSortActive }"
              role="button"
              tabindex="0"
              :aria-sort="
                isNarratorSortActive
                  ? sortOrder === 'asc'
                    ? 'ascending'
                    : 'descending'
                  : 'none'
              "
              @click="toggleListHeaderSort('narrator')"
              @keydown.enter.prevent="toggleListHeaderSort('narrator')"
              @keydown.space.prevent="toggleListHeaderSort('narrator')"
            >
              <span>Narrator</span>
              <component
                v-if="isNarratorSortActive"
                :is="sortOrder === 'asc' ? PhCaretUp : PhCaretDown"
                class="sort-caret"
              />
            </div>
            <div class="col-status">Status</div>
            <div class="col-actions">Actions</div>
          </div>
          <div
            v-for="audiobook in visibleAudiobooks"
            :key="`list-${audiobook.id}`"
            tabindex="0"
            @keydown.enter="navigateToDetail(audiobook.id)"
            class="audiobook-list-item"
            :class="{
              selected: libraryStore.isSelected(audiobook.id),
              'status-no-file': getAudiobookStatus(audiobook) === 'no-file',
              'status-quality-mismatch': getAudiobookStatus(audiobook) === 'quality-mismatch',
              'status-quality-match': getAudiobookStatus(audiobook) === 'quality-match',
              'status-downloading': getAudiobookStatus(audiobook) === 'downloading',
            }"
            @click="navigateToDetail(audiobook.id)"
          >
            <div
              class="selection-checkbox"
              @click.stop="handleCheckboxClick(audiobook, $event)"
              @mousedown.prevent
            >
              <input
                type="checkbox"
                :checked="libraryStore.isSelected(audiobook.id)"
                @change="onCheckboxChange(audiobook, $event)"
                @keydown.space.prevent="handleCheckboxKeydown(audiobook, $event)"
              />
            </div>
            <img
              class="list-thumb"
              :src="
                getProtectedImageSrc(
                  getBookImageUrl(audiobook),
                  `book:${audiobook.id}:${getBookImageUrl(audiobook) || ''}`,
                ) || getPlaceholderUrl()
              "
              :alt="audiobook.title"
              loading="lazy"
              decoding="async"
              @error="handleImageError"
            />
            <div class="list-details">
              <div class="audiobook-title">{{ safeText(audiobook.title) }}</div>
              <div class="audiobook-author">
                {{
                  audiobook.authors?.map((author) => safeText(author)).join(', ') ||
                  'Unknown Author'
                }}
              </div>
              <div v-if="showItemDetails" class="list-extra-details">
                <div v-if="audiobook.series" class="detail-line small list-narrow-only-block">
                  Series: {{ safeText(audiobook.series)
                  }}<span v-if="audiobook.seriesNumber"> #{{ audiobook.seriesNumber }}</span>
                </div>
                <div class="detail-line small">
                  <span class="list-narrow-only-inline">
                    {{
                      (audiobook.narrators || [])
                        .slice(0, 1)
                        .map((n) => safeText(n))
                        .join(', ') || ''
                    }}
                    <span
                      v-if="
                        audiobook.narrators &&
                        audiobook.narrators.length &&
                        (audiobook.publisher || audiobook.publishYear)
                      "
                    >
                      •
                    </span>
                  </span>
                  {{ safeText(audiobook.publisher)
                  }}<span v-if="audiobook.publishYear">
                    • {{ safeText(audiobook.publishYear?.toString?.() ?? '') }}</span
                  >
                </div>
              </div>
            </div>
            <div class="col-series-cell">
              <template v-if="getPrimarySeries(audiobook)">
                <span class="series-name" :title="formatAllSeriesTooltip(getPrimarySeries(audiobook))">
                  {{ formatSeriesDisplay(getPrimarySeries(audiobook)) }}
                </span>
                <span
                  v-if="(getPrimarySeries(audiobook)?.extraCount ?? 0) > 0"
                  class="extra-count"
                  :title="formatAllSeriesTooltip(getPrimarySeries(audiobook))"
                >
                  +{{ getPrimarySeries(audiobook)?.extraCount }}
                </span>
              </template>
              <span v-else class="muted">—</span>
            </div>
            <div class="col-narrator-cell">
              <template v-if="audiobook.narrators && audiobook.narrators.length">
                <span
                  class="narrator-name"
                  :title="audiobook.narrators.length > 1 ? audiobook.narrators.join('\n') : ''"
                >
                  {{ safeText(audiobook.narrators[0]) }}
                </span>
                <span
                  v-if="audiobook.narrators.length > 1"
                  class="extra-count"
                  :title="audiobook.narrators.join('\n')"
                >
                  +{{ audiobook.narrators.length - 1 }}
                </span>
              </template>
              <span v-else class="muted">—</span>
            </div>
            <div class="list-badges">
              <div
                class="status-badge"
                :class="getAudiobookStatus(audiobook)"
                role="button"
                tabindex="0"
                @click.stop="openStatusDetails(audiobook)"
                @keydown.enter.prevent="openStatusDetails(audiobook)"
                @keydown.space.prevent="openStatusDetails(audiobook)"
                :aria-label="`Show details for ${audiobook.title}`"
              >
                {{ statusText(getAudiobookStatus(audiobook)) }}
              </div>
              <div
                v-if="getQualityProfileName(audiobook.qualityProfileId)"
                class="quality-profile-badge"
              >
                <PhStar />
                {{ getQualityProfileName(audiobook.qualityProfileId) }}
              </div>
              <div class="monitored-badge" :class="{ unmonitored: !audiobook.monitored }">
                <component :is="audiobook.monitored ? PhEye : PhEyeSlash" />
                {{ audiobook.monitored ? 'Monitored' : 'Unmonitored' }}
              </div>
            </div>
            <div class="list-actions">
              <button
                class="action-btn edit-btn-small"
                @click.stop="openEditModal(audiobook)"
                title="Edit"
              >
                <PhPencil />
              </button>
              <button
                class="action-btn delete-btn-small"
                @click.stop="confirmDelete(audiobook)"
                title="Delete"
              >
                <PhTrash />
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
    <div v-if="groupBy === 'books'" class="audiobook-status-legend">
      <span class="legend-title"></span>
      <span class="legend-item">
        <span class="legend-dot status-downloading"></span>
        Downloading
      </span>
      <span class="legend-item">
        <span class="legend-dot status-no-file"></span>
        Missing
      </span>
      <span class="legend-item">
        <span class="legend-dot status-quality-mismatch"></span>
        Below Cutoff
      </span>
      <span class="legend-item">
        <span class="legend-dot status-quality-match"></span>
        Downloaded
      </span>
    </div>

    <!-- Delete confirmation handled via global ConfirmDialog (showConfirm) -->

    <!-- Bulk Edit Modal -->
    <BulkEditModal
      :is-open="showBulkEditModal"
      :selected-count="selectedCount"
      :selected-ids="libraryStore.selectedIds"
      @close="closeBulkEdit"
      @saved="handleBulkEditSaved"
    />

    <!-- Edit Audiobook Modal -->
    <EditAudiobookModal
      :is-open="showEditModal"
      :audiobook="editAudiobook"
      @close="closeEditModal"
      @saved="handleEditSaved"
    />

    <RenamePreviewModal
      :visible="showOrganizeModal"
      :audiobook-ids="organizeAudiobookIds"
      @close="closeOrganize"
      @done="handleOrganizeDone"
    />

    <!-- Custom Filter Modal -->
    <CustomFilterModal
      :isOpen="showCustomFilterModal"
      :filter="editingFilter"
      :qualityProfiles="qualityProfiles"
      :languages="availableLanguages"
      :years="availableYears"
      @save="handleSaveCustomFilterFromModal"
      @close="
        () => {
          showCustomFilterModal = false
        }
      "
    />

    <DeleteConfirmationModal
      :visible="showDeleteDialog"
      title="Delete Audiobook"
      :confirmText="deleting ? 'Deleting...' : 'Delete'"
      @close="cancelDelete"
      @confirm="executeDelete"
    >
      <template #default>
        <p>
          Are you sure you want to delete
          <strong>{{ deleteTarget?.title || 'this audiobook' }}</strong
          >?
        </p>
        <p class="warning-text">
          This action cannot be undone. The audiobook data and cached images will be permanently
          removed.
        </p>
        <div class="delete-options">
          <div class="checkbox-row">
            <label class="checkbox-wrapper checkbox-label">
              <input
                v-model="deleteFilesOnDisk"
                type="checkbox"
                class="checkbox-input"
                aria-label="Remove all files in the audiobook folder from disk"
              />
              <div class="checkbox-content">
                <span class="checkbox-title"
                  >Remove all files in the audiobook folder from disk</span
                >
                <small
                  >Deletes every file inside the audiobook folder when it can be identified safely.
                  Leave the folder itself unless you also choose the option below.</small
                >
              </div>
            </label>
          </div>

          <div class="checkbox-row">
            <label class="checkbox-wrapper checkbox-label">
              <input
                v-model="deleteFolderOnDisk"
                type="checkbox"
                class="checkbox-input"
                aria-label="Remove audiobook folder from disk"
              />
              <div class="checkbox-content">
                <span class="checkbox-title">Also remove the audiobook folder</span>
                <small
                  >Deletes the audiobook folder itself when it is safe to do so. This also removes
                  everything inside it.</small
                >
              </div>
            </label>
          </div>
        </div>
      </template>
    </DeleteConfirmationModal>

    <!-- Confirm delete custom filter handled via global showConfirm() -->
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted, watch, nextTick, reactive } from 'vue'
import {
  PhGridFour,
  PhList,
  PhArrowClockwise,
  PhPencil,
  PhTrash,
  PhCheckSquare,
  PhBook,
  PhGear,
  PhPlus,
  PhStar,
  PhEye,
  PhEyeSlash,
  PhSpinner,
  PhWarningCircle,
  PhInfo,
  PhCaretDown,
  PhCaretUp,
  PhBookOpen,
  PhX,
  PhUser,
  PhBooks,
  PhFolderOpen,
  PhFunnel,
} from '@phosphor-icons/vue'
import { useRouter, useRoute } from 'vue-router'
import { useLibraryStore } from '@/stores/library'
import { useConfigurationStore } from '@/stores/configuration'
import { useRootFoldersStore } from '@/stores/rootFolders'
import { useDownloadsStore } from '@/stores/downloads'
import { apiService } from '@/services/api'
import { buildApiPath } from '@/services/apiBase'
import { logger } from '@/utils/logger'
import BulkEditModal from '@/components/domain/collection/BulkEditModal.vue'
import EditAudiobookModal from '@/components/domain/audiobook/EditAudiobookModal.vue'
import RenamePreviewModal from '@/components/domain/organize/RenamePreviewModal.vue'
import DeleteConfirmationModal from '@/components/feedback/DeleteConfirmationModal.vue'
import CustomSelect from '@/components/form/CustomSelect.vue'
import FiltersDropdown from '@/components/ui/FiltersDropdown.vue'
import CustomFilterModal from '@/components/domain/collection/CustomFilterModal.vue'
import { EmptyState } from '@/components/base'
import { showConfirm } from '@/composables/useConfirm'
import type { Audiobook, AudiobookStatus, QualityProfile } from '@/types'
import { evaluateRules } from '@/utils/customFilterEvaluator'
import type { RuleLike } from '@/utils/customFilterEvaluator'
import { computeAudiobookStatus, formatAudiobookStatus } from '@/utils/audiobookStatus'
import { safeText } from '@/utils/textUtils'
import { getPlaceholderUrl } from '@/utils/placeholder'
import { observeLazyImages } from '@/utils/lazyLoad'
import { errorTracking } from '@/services/errorTracking'
import { isLikelyBackendImageUrl, useProtectedImages } from '@/composables/useProtectedImages'
import {
  getPrimarySeries,
  formatSeriesDisplay,
  formatAllSeriesTooltip,
  getSeriesSortKey,
} from '@/utils/seriesDisplay'

function getAuthorSortKey(author: string): string {
  const parts = author.trim().split(/\s+/)
  if (parts.length === 0) return ''
  if (parts.length === 1) return (parts[0] || '').toLowerCase()
  const lastName = parts[parts.length - 1] || ''
  const firstName = parts[0] || ''
  return (lastName + ' ' + firstName).toLowerCase()
}

function getAuthorFirstNameSortKey(author: string): string {
  const parts = author.trim().split(/\s+/)
  if (parts.length === 0) return ''
  return (parts[0] || '').toLowerCase()
}

function getNarratorSortKey(narrator: string): string {
  const parts = narrator.trim().split(/\s+/)
  if (parts.length === 0) return ''
  if (parts.length === 1) return (parts[0] || '').toLowerCase()
  const lastName = parts[parts.length - 1] || ''
  const firstName = parts[0] || ''
  return (lastName + ' ' + firstName).toLowerCase()
}

function getNarratorFirstNameSortKey(narrator: string): string {
  const parts = narrator.trim().split(/\s+/)
  if (parts.length === 0) return ''
  return (parts[0] || '').toLowerCase()
}

const router = useRouter()
const route = useRoute()
const libraryStore = useLibraryStore()
const configStore = useConfigurationStore()
const rootFoldersStore = useRootFoldersStore()
const downloadsStore = useDownloadsStore()
const { getProtectedImageSrc, clearProtectedImages } = useProtectedImages()

// Computed list after applying search, filters and sorting
const searchQuery = ref('')

// Local storage key for persisting search query
const SEARCH_QUERY_KEY = 'listenarr.searchQuery'

// Initialize search query from localStorage
try {
  const stored = localStorage.getItem(SEARCH_QUERY_KEY)
  if (stored !== null) searchQuery.value = stored
} catch {}

// Watch search query changes and persist to localStorage
watch(searchQuery, (v) => {
  try {
    localStorage.setItem(SEARCH_QUERY_KEY, v)
  } catch {}
})
// Store sortKey and sortOrder per group
const DEFAULT_SORTS = {
  books: { key: 'title', order: 'asc' },
  authors: { key: 'author-last', order: 'asc' },
  series: { key: 'title', order: 'asc' },
} as const

const sortState = reactive({
  books: { key: 'title', order: 'asc' as 'asc' | 'desc' },
  authors: { key: 'author-last', order: 'asc' as 'asc' | 'desc' },
  series: { key: 'title', order: 'asc' as 'asc' | 'desc' },
})

const sortKey = computed({
  get: () => sortState[groupBy.value].key,
  set: (val: string) => {
    sortState[groupBy.value].key = val
  },
})
const sortOrder = computed({
  get: () => sortState[groupBy.value].order,
  set: (val: 'asc' | 'desc') => {
    sortState[groupBy.value].order = val
  },
})
// Toolbar quick-filters. Each axis is mirrored into the URL (see the unified
// query writer below) so the filter selection survives reload, bookmarks, and
// the round-trip into AudiobookDetailView and back.
function readEnumQuery<T extends string>(value: unknown, allowed: readonly T[]): T | null {
  return typeof value === 'string' && (allowed as readonly string[]).includes(value)
    ? (value as T)
    : null
}

const filterMonitored = ref<'all' | 'monitored' | 'unmonitored'>(
  readEnumQuery(route.query.monitored, ['monitored', 'unmonitored'] as const) ?? 'all',
)
const filterStatus = ref<'all' | 'downloaded' | 'missing' | 'mismatch' | 'downloading'>(
  readEnumQuery(route.query.status, [
    'downloaded',
    'missing',
    'mismatch',
    'downloading',
  ] as const) ?? 'all',
)
const filterQualityProfile = ref<string>(
  typeof route.query.qp === 'string' && route.query.qp ? route.query.qp : 'all',
)
const filterLanguage = ref<string>(
  typeof route.query.language === 'string' && route.query.language ? route.query.language : 'all',
)
const filterYear = ref<string>(
  typeof route.query.year === 'string' && route.query.year ? route.query.year : 'all',
)

// Drill-down filter: shows books missing a specific metadata field. Driven by
// the ?missing= URL param so dashboard "missing X" counts can link straight here.
type MissingField =
  | 'files'
  | 'coverArt'
  | 'asin'
  | 'isbn'
  | 'genres'
  | 'narrators'
  | 'description'
  | 'publisher'
  | 'language'
  | 'publishDate'
  | 'runtime'
  | 'seriesPosition'

const MISSING_FIELD_LABELS: Record<MissingField, string> = {
  files: 'an audio file',
  coverArt: 'Cover art',
  asin: 'ASIN',
  isbn: 'ISBN',
  genres: 'Genres',
  narrators: 'Narrators',
  description: 'Description',
  publisher: 'Publisher',
  language: 'Language',
  publishDate: 'Publish date',
  runtime: 'Runtime',
  seriesPosition: 'Series position',
}

const MISSING_FIELD_KEYS = Object.keys(MISSING_FIELD_LABELS) as MissingField[]

function isMissingFieldKey(value: unknown): value is MissingField {
  return typeof value === 'string' && (MISSING_FIELD_KEYS as string[]).includes(value)
}

// Drill-down filters carry the URL-driven scope from the dashboard. They
// compose with each other, so a click on Patterson's Missing segment can land
// on /audiobooks?author=Patterson&missing=files and the chip summarises both.
const filterMissing = ref<MissingField | null>(
  isMissingFieldKey(route.query.missing) ? (route.query.missing as MissingField) : null,
)
const filterAuthor = ref<string | null>(
  typeof route.query.author === 'string' ? route.query.author : null,
)
const filterNarrator = ref<string | null>(
  typeof route.query.narrator === 'string' ? route.query.narrator : null,
)
const filterGenre = ref<string | null>(
  typeof route.query.genre === 'string' ? route.query.genre : null,
)

// When the URL carries ?language=, mirror it into filterLanguage (which the
// existing toolbar quick-filter already owns) and remember it was URL-driven
// so the chip's clear button can reset it.
const filterLanguageFromUrl = ref(false)

// The library list endpoint returns a slim DTO that doesn't carry every
// metadata field (Description, Isbn, etc.), so client-side predicates can't
// reliably tell "missing X" for those fields. Defer to the dashboard's
// authoritative ID list — fetched once per filterMissing change and used to
// gate the filter so its results agree with the dashboard's headline counts.
const missingIdSet = ref<Set<number> | null>(null)
const missingIdsLoading = ref(false)
const missingIdsError = ref<string | null>(null)

// Translate the URL-style camelCase missing-field key into the backend's
// PascalCase MissingField enum name.
function backendMissingFieldName(field: MissingField): string {
  return field.charAt(0).toUpperCase() + field.slice(1)
}

interface DrilldownChipItem {
  label: string
  value: string
}

const activeDrilldownItems = computed(() => {
  const items: DrilldownChipItem[] = []
  if (filterAuthor.value) items.push({ label: 'Author', value: filterAuthor.value })
  if (filterNarrator.value) items.push({ label: 'Narrator', value: filterNarrator.value })
  if (filterGenre.value) items.push({ label: 'Genre', value: filterGenre.value })
  if (filterLanguageFromUrl.value && filterLanguage.value !== 'all') {
    items.push({ label: 'Language', value: filterLanguage.value })
  }
  if (filterMissing.value) {
    items.push({ label: 'Missing', value: MISSING_FIELD_LABELS[filterMissing.value] })
  }
  return items
})

const hasDrilldown = computed(() => activeDrilldownItems.value.length > 0)

function clearDrilldownFilters() {
  filterMissing.value = null
  filterAuthor.value = null
  filterNarrator.value = null
  filterGenre.value = null
  if (filterLanguageFromUrl.value) {
    filterLanguage.value = 'all'
    filterLanguageFromUrl.value = false
  }
  const { missing: _m, author: _a, narrator: _n, genre: _g, language: _l, ...rest } =
    route.query as Record<string, unknown>
  router.replace({ path: '/audiobooks', query: rest as Record<string, string> })
}

const availableLanguages = computed(() => {
  const langs = new Set<string>()
  for (const b of libraryStore.audiobooks || []) {
    if (b.language) langs.add(b.language)
  }
  return Array.from(langs).sort()
})

const availableYears = computed(() => {
  const years = new Set<string>()
  for (const b of libraryStore.audiobooks || []) {
    if (b.publishYear) years.add(b.publishYear)
  }
  // sort descending numeric where possible
  return Array.from(years).sort((a, b) => Number(b) - Number(a))
})

// Custom filters stored in localStorage
const CUSTOM_FILTERS_KEY = 'listenarr.customFilters'
interface CustomFilterRule {
  field: string
  operator: string
  value: string
}
interface CustomFilter {
  id: string
  label: string
  rules: CustomFilterRule[]
}

const customFilters = ref<CustomFilter[]>([])
// Built-in filter IDs (monitored / unmonitored / missing / recent) are stable
// across installs; custom filter IDs are localStorage-scoped UUIDs, so a URL
// referencing a custom filter only resolves on the device that created it. The
// filter-application code falls through harmlessly if the ID isn't found.
const selectedFilterId = ref<string | null>(
  typeof route.query.filter === 'string' && route.query.filter ? route.query.filter : null,
)
const showCustomFilterModal = ref(false)
const editingFilter = ref<CustomFilter | null>(null)

function loadCustomFilters() {
  try {
    const raw = localStorage.getItem(CUSTOM_FILTERS_KEY)
    if (raw) customFilters.value = JSON.parse(raw)
  } catch {
    customFilters.value = []
  }
}

function saveCustomFilters() {
  try {
    localStorage.setItem(CUSTOM_FILTERS_KEY, JSON.stringify(customFilters.value || []))
  } catch {}
}

function handleCreateCustomFilter() {
  // New filter
  editingFilter.value = null
  showCustomFilterModal.value = true
}

function handleEditCustomFilter(f: CustomFilter) {
  // Load a copy into the modal for editing
  editingFilter.value = JSON.parse(JSON.stringify(f))
  showCustomFilterModal.value = true
}

async function handleDeleteCustomFilter(f: CustomFilter) {
  const ok = await showConfirm(
    `Delete custom filter "${f.label}"? This cannot be undone.`,
    'Delete Custom Filter',
    { danger: true, confirmText: 'Delete', cancelText: 'Cancel' },
  )
  if (!ok) return
  const idx = customFilters.value.findIndex((x) => x.id === f.id)
  if (idx >= 0) {
    customFilters.value.splice(idx, 1)
    saveCustomFilters()
    if (selectedFilterId.value === f.id) selectedFilterId.value = null
  }
}

function handleSaveCustomFilter(f: CustomFilter) {
  const idx = customFilters.value.findIndex((cf) => cf.id === f.id)
  if (idx >= 0) customFilters.value[idx] = f
  else customFilters.value.push(f)
  saveCustomFilters()
}

// when saving from modal, close modal and select the new filter
function handleSaveCustomFilterFromModal(f: CustomFilter) {
  handleSaveCustomFilter(f)
  selectedFilterId.value = f.id
  showCustomFilterModal.value = false
}

// load on mount
try {
  loadCustomFilters()
} catch {}

// sortOrder toggled via sortKeyProxy when selecting same key; explicit toggle removed

const filteredAndSortedAudiobooks = computed(() => {
  const list = (libraryStore.audiobooks || []).slice()

  // Apply search
  const q = (searchQuery.value || '').trim().toLowerCase()
  let filtered = list.filter((b) => {
    if (!q) return true
    const title = (b.title || '').toString().toLowerCase()
    const authors = (b.authors || []).map((a) => (a || '').toString().toLowerCase()).join(' ')
    const narrators = (b.narrators || []).map((n) => (n || '').toString().toLowerCase()).join(' ')
    const publisher = (b.publisher || '').toString().toLowerCase()
    const year = (b.publishYear || '').toString().toLowerCase()
    return (
      title.includes(q) ||
      authors.includes(q) ||
      narrators.includes(q) ||
      publisher.includes(q) ||
      year.includes(q)
    )
  })

  // Apply selected filter (built-in or custom)
  if (selectedFilterId.value) {
    const sid = selectedFilterId.value
    if (sid === 'monitored') {
      filtered = filtered.filter((b) => !!b.monitored)
    } else if (sid === 'unmonitored') {
      filtered = filtered.filter((b) => !b.monitored)
    } else if (sid === 'missing') {
      filtered = filtered.filter((b) => getAudiobookStatus(b) === 'no-file')
    } else if (sid === 'recent') {
      // For now: approximate by publishYear being this year or last year
      const thisYear = new Date().getFullYear()
      filtered = filtered.filter((b) => {
        const y = Number(b.publishYear || 0)
        return !isNaN(y) && (y === thisYear || y === thisYear - 1)
      })
    } else {
      // custom filter (supports grouping/parentheses)
      const cf = customFilters.value.find((x) => x.id === sid)
      if (cf) {
        filtered = filtered.filter((b) => {
          // cf.rules is expected to include optional groupStart/groupEnd flags
          // evaluateRules handles conjunction precedence and parentheses
          return evaluateRules(b as Audiobook, cf.rules as RuleLike[])
        })
      }
    }
  }

  // Filter monitored
  if (filterMonitored.value === 'monitored') {
    filtered = filtered.filter((b) => !!b.monitored)
  } else if (filterMonitored.value === 'unmonitored') {
    filtered = filtered.filter((b) => !b.monitored)
  }

  // Filter by quality profile
  if (filterQualityProfile.value !== 'all') {
    const qid = Number(filterQualityProfile.value)
    filtered = filtered.filter((b) => (b.qualityProfileId ?? null) === qid)
  }

  // Filter by language
  if (filterLanguage.value !== 'all') {
    const target = filterLanguage.value.toLowerCase()
    filtered = filtered.filter((b) => (b.language || '').toString().toLowerCase() === target)
  }

  // Filter by publish year
  if (filterYear.value !== 'all') {
    filtered = filtered.filter((b) => (b.publishYear || '') === filterYear.value)
  }

  // Filter by status
  if (filterStatus.value !== 'all') {
    filtered = filtered.filter((b) => {
      const s = getAudiobookStatus(b)
      switch (filterStatus.value) {
        case 'downloaded':
          return s === 'quality-match'
        case 'missing':
          return s === 'no-file'
        case 'mismatch':
          return s === 'quality-mismatch'
        case 'downloading':
          return s === 'downloading'
      }
      return true
    })
  }

  // Drill-down: books missing a specific metadata field (from dashboard links).
  // Authoritative ID list comes from the backend so the result matches the
  // dashboard's headline counts even when the library list payload is slim
  // (Description, Isbn, etc. aren't in the slim DTO). While the IDs are
  // loading or after an error we filter to an empty set rather than show
  // a misleading "everything passes" list.
  if (filterMissing.value) {
    if (missingIdSet.value) {
      const ids = missingIdSet.value
      filtered = filtered.filter((b) => ids.has(b.id))
    } else {
      filtered = []
    }
  }

  // Drill-down: author, narrator, genre — matched case-insensitively to the
  // canonical (trimmed) name the dashboard groups on.
  if (filterAuthor.value) {
    const target = filterAuthor.value.toLowerCase()
    filtered = filtered.filter((b) =>
      (b.authors || []).some((a) => (a || '').trim().toLowerCase() === target),
    )
  }
  if (filterNarrator.value) {
    const target = filterNarrator.value.toLowerCase()
    filtered = filtered.filter((b) =>
      (b.narrators || []).some((n) => (n || '').trim().toLowerCase() === target),
    )
  }
  if (filterGenre.value) {
    const target = filterGenre.value.toLowerCase()
    filtered = filtered.filter((b) =>
      (b.genres || []).some((g) => (g || '').trim().toLowerCase() === target),
    )
  }

  // Sorting
  filtered.sort((a, b) => {
    let av: string | boolean = ''
    let bv: string | boolean = ''
    switch (sortKey.value) {
      case 'title':
        av = (a.title || '').toString().toLowerCase()
        bv = (b.title || '').toString().toLowerCase()
        break
      case 'author-last':
        const aAuthorLast = a.authors && a.authors[0] ? a.authors[0] : ''
        const bAuthorLast = b.authors && b.authors[0] ? b.authors[0] : ''
        av = getAuthorSortKey(aAuthorLast)
        bv = getAuthorSortKey(bAuthorLast)
        break
      case 'author-first':
        const aAuthorFirst = a.authors && a.authors[0] ? a.authors[0] : ''
        const bAuthorFirst = b.authors && b.authors[0] ? b.authors[0] : ''
        av = getAuthorFirstNameSortKey(aAuthorFirst)
        bv = getAuthorFirstNameSortKey(bAuthorFirst)
        break
      case 'narrator-last':
        const aNarratorLast = a.narrators && a.narrators[0] ? a.narrators[0] : ''
        const bNarratorLast = b.narrators && b.narrators[0] ? b.narrators[0] : ''
        av = getNarratorSortKey(aNarratorLast)
        bv = getNarratorSortKey(bNarratorLast)
        break
      case 'series':
        av = getSeriesSortKey(a)
        bv = getSeriesSortKey(b)
        break
      case 'narrator-first':
        const aNarratorFirst = a.narrators && a.narrators[0] ? a.narrators[0] : ''
        const bNarratorFirst = b.narrators && b.narrators[0] ? b.narrators[0] : ''
        av = getNarratorFirstNameSortKey(aNarratorFirst)
        bv = getNarratorFirstNameSortKey(bNarratorFirst)
        break
      case 'publisher':
        av = (a.publisher || '').toString().toLowerCase()
        bv = (b.publisher || '').toString().toLowerCase()
        break
      case 'year':
        const ay = Number(a.publishYear || NaN)
        const by = Number(b.publishYear || NaN)
        if (!isNaN(ay) || !isNaN(by)) {
          return (
            ((isNaN(ay) ? 0 : ay) - (isNaN(by) ? 0 : by)) * (sortOrder.value === 'asc' ? 1 : -1)
          )
        }
        av = (a.publishYear || '').toString().toLowerCase()
        bv = (b.publishYear || '').toString().toLowerCase()
        break
      case 'monitored':
        av = !!a.monitored
        bv = !!b.monitored
        break
      case 'status':
        av = getAudiobookStatus(a)
        bv = getAudiobookStatus(b)
        break
    }

    if (typeof av === 'boolean' && typeof bv === 'boolean') {
      return av === bv ? 0 : (av ? -1 : 1) * (sortOrder.value === 'asc' ? 1 : -1)
    }

    return (
      ((av as string) < (bv as string) ? -1 : (av as string) > (bv as string) ? 1 : 0) *
      (sortOrder.value === 'asc' ? 1 : -1)
    )
  })

  return filtered
})

const audiobooks = computed(() => filteredAndSortedAudiobooks.value)

// Reactive map of fetched author cover overrides (keyed by author name)
const authorCoverOverrides = reactive<Record<string, string>>({})
const authorCoverLoading = reactive<Record<string, boolean>>({})
const authorCoverNotFound = new Set<string>()
const authorImageLoaded = reactive<Record<string, boolean>>({})

function isPlaceholderCoverUrl(url: string | undefined): boolean {
  const v = (url || '').trim()
  if (!v) return true
  return (
    v === '/placeholder.svg' ||
    v === 'placeholder.svg' ||
    v.endsWith('/placeholder.svg') ||
    v.includes('/placeholder.svg?')
  )
}

function getBookImageUrl(
  book: Pick<Audiobook, 'imageUrl' | 'asin'> | null | undefined,
): string | undefined {
  if (!book) return undefined
  const raw = (book.imageUrl || '').trim()
  if (raw && !isPlaceholderCoverUrl(raw)) return raw
  const asin = (book.asin || '').trim()
  if (asin) return buildApiPath(`/images/${encodeURIComponent(asin)}`)
  return raw || undefined
}

function getAuthorImageUrl(collection: { name: string; coverUrl?: string }) {
  const override = authorCoverOverrides[collection.name]
  if (override) return override
  const cover = collection.coverUrl?.trim()
  if (!cover || isPlaceholderCoverUrl(cover)) return undefined
  if (isLikelyBackendImageUrl(cover)) return cover
  if (cover.startsWith('http://') || cover.startsWith('https://')) return cover
  if (cover.startsWith('/')) return cover
  return undefined
}

function onAuthorImageLoad(authorName: string) {
  authorImageLoaded[authorName] = true
}

function handleAuthorImageError(authorName: string, event: Event) {
  authorImageLoaded[authorName] = false
  const img = event.target as HTMLImageElement | null
  if (!img) return
  try {
    img.removeAttribute('data-src')
  } catch {}
  try {
    img.style.opacity = '0'
  } catch {}
  try {
    ;(img as unknown as { onerror?: null }).onerror = null
  } catch {}
  if (!authorCoverOverrides[authorName] && !authorCoverLoading[authorName]) {
    void ensureAuthorCover(authorName)
  }
}

async function ensureAuthorCover(authorName: string) {
  if (!authorName) return
  if (authorCoverOverrides[authorName]) return
  if (authorCoverNotFound.has(authorName)) return
  if (authorCoverLoading[authorName]) return
  authorCoverLoading[authorName] = true
  try {
    if (typeof apiService.getAuthorLookup !== 'function') return
    const info = await apiService.getAuthorLookup(authorName)
    if (!info) {
      authorCoverNotFound.add(authorName)
      return
    }
    if (info.cachedPath) {
      authorCoverOverrides[authorName] = info.cachedPath
    } else if (info.asin) {
      authorCoverOverrides[authorName] = buildApiPath(`/images/${encodeURIComponent(info.asin)}`)
    } else if (info.image) {
      authorCoverOverrides[authorName] = info.image
    } else {
      authorCoverNotFound.add(authorName)
    }
    try {
      await nextTick()
      observeLazyImages()
    } catch {}
  } catch (e: unknown) {
    authorCoverNotFound.add(authorName)
    errorTracking.captureException(e as Error, {
      component: 'AudiobooksView',
      operation: 'ensureAuthorCover',
      metadata: { authorName },
    })
    // ignore network/lookup failures
  } finally {
    authorCoverLoading[authorName] = false
  }
}

// Quality profiles + active downloads are referenced from groupedCollections
// below to count per-collection ready / quality-mismatch books, so they need
// to be declared before groupedCollections (the original declarations live
// further down the file).
const qualityProfiles = ref<QualityProfile[]>([])

const activeDownloadAudiobookIds = computed(() => {
  const ids = new Set<number>()
  for (const download of downloadsStore.activeDownloads || []) {
    if (typeof download?.audiobookId === 'number') {
      ids.add(download.audiobookId)
    }
  }
  return ids
})

// Grouping mode
const GROUP_BY_KEY = 'listenarr.groupBy'
const groupBy = ref<'books' | 'authors' | 'series'>('books')
const imagesLoading = ref(false) // show loading overlay while images rerender when grouping changes
const showGroupMenu = ref(false)

// --- GroupBy and SortKey Initialization ---
// URL wins over localStorage so a shared link reproduces the sender's view.
// The unified URL writer (set up later) keeps the URL in sync from here on —
// no router.replace at init: it would wipe drill-down params (?missing=,
// ?author=, etc.) carried in by dashboard links.
try {
  const stored = localStorage.getItem(GROUP_BY_KEY)
  if (stored && ['books', 'authors', 'series'].includes(stored)) {
    groupBy.value = stored as 'books' | 'authors' | 'series'
  }
  const initialQ = route.query.group as string | undefined
  if (initialQ && ['books', 'authors', 'series'].includes(initialQ) && initialQ !== groupBy.value) {
    groupBy.value = initialQ as 'books' | 'authors' | 'series'
  }
} catch {}

// Hydrate sort for the active group from the URL. We only seed the current
// group's slot — other groups keep their defaults until the user switches.
try {
  const allowedKeys = (() => {
    if (groupBy.value === 'books') {
      return [
        'title',
        'author-last',
        'author-first',
        'narrator-last',
        'narrator-first',
        'publisher',
        'year',
        'monitored',
        'status',
      ]
    }
    if (groupBy.value === 'authors') return ['author-last', 'author-first', 'count']
    return ['title', 'count']
  })()
  const qSort = route.query.sort
  if (typeof qSort === 'string' && allowedKeys.includes(qSort)) {
    sortState[groupBy.value].key = qSort
  }
  const qDir = route.query.dir
  if (qDir === 'asc' || qDir === 'desc') {
    sortState[groupBy.value].order = qDir
  }
} catch {}

watch(groupBy, (v) => {
  try {
    localStorage.setItem(GROUP_BY_KEY, v)
  } catch {}

  // Restore the view mode the user last left this grouping in.
  viewMode.value = loadViewModeFor(v)

  // Ensure selected sort key is valid for the new grouping; reset to sensible defaults if needed
  const allowed = sortOptions.value.map((o) => o.value)
  const groupSort = sortState[v]
  if (!allowed.includes(groupSort.key)) {
    // Pick a default for this group
    groupSort.key = DEFAULT_SORTS[v].key
    groupSort.order = DEFAULT_SORTS[v].order
  }
})

// (grouping sync handled earlier in file)

const groupedCollections = computed(() => {
  if (groupBy.value === 'books') return []

  const books = filteredAndSortedAudiobooks.value
  const groups = new Map<
    string,
    {
      name: string
      count: number
      readyCount: number
      qualityMismatchCount: number
      coverUrl?: string
      coverUrls?: string[]
    }
  >()
  const activeIds = activeDownloadAudiobookIds.value
  const profiles = qualityProfiles.value

  books.forEach((book) => {
    const key = groupBy.value === 'authors' ? book.authors?.[0] : book.series
    if (key) {
      if (!groups.has(key)) {
        if (groupBy.value === 'authors') {
          // Prefer override (fetched author image) first, then author ASIN, then book cover
          let cover: string | undefined = undefined
          try {
            // Use override if we've already fetched author image for this name
            // `authorCoverOverrides` is a reactive map populated asynchronously below
            // (declared further down in this file via `reactive`).
            // Access via (global) variable — will be undefined initially.
            // eslint-disable-next-line @typescript-eslint/ban-ts-comment
            // @ts-ignore
            if (authorCoverOverrides && authorCoverOverrides[key]) {
              // eslint-disable-next-line @typescript-eslint/ban-ts-comment
              // @ts-ignore
              cover = authorCoverOverrides[key]
            }
          } catch {}

          if (!cover) {
            try {
              const asin = (book as unknown as { authorAsins?: string[] })?.authorAsins?.[0]
              if (asin) cover = buildApiPath(`/images/${encodeURIComponent(asin)}`)
            } catch {}
          }

          groups.set(key, {
            name: key,
            count: 0,
            readyCount: 0,
            qualityMismatchCount: 0,
            coverUrl: cover,
          })
        } else {
          groups.set(key, {
            name: key,
            count: 0,
            readyCount: 0,
            qualityMismatchCount: 0,
            coverUrls: [],
          })
        }
      }
      const group = groups.get(key)!
      group.count++
      const status = computeAudiobookStatus(book, activeIds, profiles)
      if (status === 'quality-match' || status === 'quality-mismatch') {
        group.readyCount++
        if (status === 'quality-mismatch') group.qualityMismatchCount++
      }
      const bookCover = getBookImageUrl(book)
      if (groupBy.value === 'authors') {
        try {
          const authorAsin = (book as unknown as { authorAsins?: string[] })?.authorAsins?.[0]
          if (authorAsin) group.coverUrl = buildApiPath(`/images/${encodeURIComponent(authorAsin)}`)
        } catch {}
      }
      if (groupBy.value === 'series' && group.coverUrls && group.coverUrls.length < 8) {
        if (bookCover && !group.coverUrls.includes(bookCover)) {
          group.coverUrls.push(bookCover)
        }
      }
    }
  })

  const vals = Array.from(groups.values())

  // For grouped views (authors/series), respect toolbar sortKey for collection sorting
  const order = sortOrder.value === 'asc' ? 1 : -1
  switch (sortKey.value) {
    case 'count':
      vals.sort((a, b) => (a.count - b.count) * order)
      break
    case 'author-last':
      vals.sort((a, b) => {
        const av = getAuthorSortKey(a.name)
        const bv = getAuthorSortKey(b.name)
        return av === bv ? 0 : (av < bv ? -1 : 1) * order
      })
      break
    case 'author-first':
      vals.sort((a, b) => {
        const av = getAuthorFirstNameSortKey(a.name)
        const bv = getAuthorFirstNameSortKey(b.name)
        return av === bv ? 0 : (av < bv ? -1 : 1) * order
      })
      break
    // default: sort by name (collection title)
    default:
      vals.sort((a, b) => a.name.localeCompare(b.name) * order)
      break
  }
  return vals
})

const authorHasSpecificCoverMap = computed<Record<string, boolean>>(() => {
  const map: Record<string, boolean> = {}
  for (const [name, cover] of Object.entries(authorCoverOverrides)) {
    if (name && cover) map[name] = true
  }
  for (const book of filteredAndSortedAudiobooks.value) {
    const name = book.authors?.[0]
    if (!name) continue
    const authorAsin = (book as unknown as { authorAsins?: string[] })?.authorAsins?.[0]
    if (authorAsin) map[name] = true
  }
  return map
})

let authorCardObserver: IntersectionObserver | null = null

function observeAuthorCards() {
  if (groupBy.value !== 'authors') return
  const cards = Array.from(
    document.querySelectorAll<HTMLElement>(
      '.author-collection .audiobook-poster-container[data-author-name]',
    ),
  )
  if (cards.length === 0) return

  if (!('IntersectionObserver' in window)) {
    for (const card of cards) {
      const name = card.dataset.authorName
      if (name) void ensureAuthorCover(name)
    }
    return
  }

  if (!authorCardObserver) {
    authorCardObserver = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue
          const target = entry.target as HTMLElement
          const name = target.dataset.authorName
          const hasCover = target.dataset.authorHasCover === '1'
          if (name && !hasCover) void ensureAuthorCover(name)
          authorCardObserver?.unobserve(target)
        }
      },
      { rootMargin: '400px', threshold: 0.01 },
    )
  }

  for (const card of cards) {
    const name = card.dataset.authorName
    if (name && (authorCoverOverrides[name] || authorCoverNotFound.has(name))) continue
    authorCardObserver.observe(card)
  }
}

// When grouped collections change (or grouping set to authors), observe visible author cards
// so we only fetch cover data for items near the viewport.
watch(
  () => groupedCollections.value.map((g) => g.name),
  async () => {
    if (groupBy.value !== 'authors') return
    await nextTick()
    observeAuthorCards()
  },
  { immediate: true },
)

// Options for sort dropdown in toolbar (change depending on grouping)
const sortOptions = computed(() => {
  if (groupBy.value === 'books') {
    return [
      { value: 'title', label: 'Title' },
      { value: 'author-last', label: 'Author Last Name' },
      { value: 'author-first', label: 'Author First Name' },
      { value: 'narrator-last', label: 'Narrator Last Name' },
      { value: 'narrator-first', label: 'Narrator First Name' },
      { value: 'series', label: 'Series' },
      { value: 'publisher', label: 'Publisher' },
      { value: 'year', label: 'Release Year' },
      { value: 'monitored', label: 'Monitored' },
      { value: 'status', label: 'Status' },
    ]
  }

  // When grouped by authors or series, expose collection-relevant sort keys
  if (groupBy.value === 'authors') {
    return [
      { value: 'author-last', label: 'Author Last Name' },
      { value: 'author-first', label: 'Author First Name' },
      { value: 'count', label: 'Books' }, // number of books in the collection
    ]
  }

  return [
    { value: 'title', label: 'Series' }, // sort by series name
    { value: 'count', label: 'Books' }, // number of books in the collection
  ]
})

// Proxy so selecting the same key toggles sort order, selecting a new key sets ascending
const sortKeyProxy = computed<string>({
  get: () => sortKey.value,
  set: (val: string) => {
    if (val === sortKey.value) {
      // toggle
      sortOrder.value = sortOrder.value === 'asc' ? 'desc' : 'asc'
    } else {
      sortKey.value = val
      sortOrder.value = 'asc'
    }
  },
})

// Narrator column maps to two toolbar options (last- and first-name); treat both
// as "narrator is active" so clicking the column toggles whichever is selected.
const isNarratorSortActive = computed(
  () => sortKey.value === 'narrator-last' || sortKey.value === 'narrator-first',
)
const isSeriesSortActive = computed(() => sortKey.value === 'series')

function toggleListHeaderSort(key: 'series' | 'narrator') {
  if (key === 'series') {
    if (isSeriesSortActive.value) {
      sortOrder.value = sortOrder.value === 'asc' ? 'desc' : 'asc'
    } else {
      sortKey.value = 'series'
      sortOrder.value = 'asc'
    }
    return
  }
  if (isNarratorSortActive.value) {
    sortOrder.value = sortOrder.value === 'asc' ? 'desc' : 'asc'
  } else {
    sortKey.value = 'narrator-last'
    sortOrder.value = 'asc'
  }
}

// toolbar select option helpers were removed in favor of CustomSelect usage per control

// Raw library length (unfiltered) so we can show appropriate empty-state vs no-results
const rawAudiobooksLength = computed(() => (libraryStore.audiobooks || []).length)

function clearFilters() {
  // Reset toolbar sort for current group
  sortState[groupBy.value].key = DEFAULT_SORTS[groupBy.value].key
  sortState[groupBy.value].order = DEFAULT_SORTS[groupBy.value].order

  // Reset builtin filters
  filterMonitored.value = 'all'
  filterStatus.value = 'all'
  filterQualityProfile.value = 'all'
  filterLanguage.value = 'all'
  filterYear.value = 'all'

  // Clear drill-down filters (missing/author/narrator/genre/language) and
  // strip them from the URL.
  if (hasDrilldown.value) {
    clearDrilldownFilters()
  }

  // Clear search text and any custom filter selection (also remove persisted search value)
  try {
    searchQuery.value = ''
    localStorage.removeItem(SEARCH_QUERY_KEY)
  } catch {}
  selectedFilterId.value = null

  // Reset author image caches so images reload after clearing filters
  Object.keys(authorCoverOverrides).forEach((k) => delete authorCoverOverrides[k])
  Object.keys(authorImageLoaded).forEach((k) => delete authorImageLoaded[k])
  authorCoverNotFound.clear()
  clearProtectedImages()
  nextTick(() => typeof observeLazyImages === 'function' && observeLazyImages())
}
const loading = computed(() => libraryStore.loading)
const error = computed(() => libraryStore.error)
const selectedCount = computed(() => libraryStore.selectedIds.size)
const hasRootFolderConfigured = computed(() => {
  return (
    rootFoldersStore.folders.length > 0 ||
    (configStore.applicationSettings?.outputPath &&
      configStore.applicationSettings.outputPath.trim().length > 0)
  )
})

// Virtual scrolling supporting grid and list layouts
const scrollContainer = ref<HTMLElement | null>(null)
const ITEMS_PER_ROW = ref(4) // Will be recalculated for grid; list uses 1
const LIST_ROW_HEIGHT = 80
const GRID_ROW_HEIGHT_FALLBACK = 220
const GRID_DETAILS_EXTRA_HEIGHT = 64 // extra height for showing details under poster
const GRID_GAP = 20
const BUFFER_ROWS = 2 // Extra rows to render above and below viewport
const measuredRowHeight = ref<number | null>(null)

// View mode is persisted per grouping so users can have, e.g., grid for books
// but list for authors. The legacy single key is consulted once for migration —
// when a per-grouping key is unset, we seed it from the legacy value.
const VIEWMODE_KEY_LEGACY = 'listenarr.viewMode'
const VIEWMODE_KEYS = {
  books: 'listenarr.viewMode.books',
  authors: 'listenarr.viewMode.authors',
  series: 'listenarr.viewMode.series',
} as const

function loadViewModeFor(g: 'books' | 'authors' | 'series'): 'grid' | 'list' {
  try {
    const legacy = localStorage.getItem(VIEWMODE_KEY_LEGACY)
    if (legacy === 'grid' || legacy === 'list') {
      for (const k of Object.values(VIEWMODE_KEYS)) {
        if (localStorage.getItem(k) === null) localStorage.setItem(k, legacy)
      }
    }
    const stored = localStorage.getItem(VIEWMODE_KEYS[g])
    if (stored === 'grid' || stored === 'list') return stored
  } catch {
    /* ignore localStorage errors (e.g., privacy mode) */
  }
  return 'grid'
}

const viewMode = ref<'grid' | 'list'>(loadViewModeFor(groupBy.value))

// Persist view mode under the per-grouping key whenever it changes. Set up at
// top level so it always fires — the previous setup was gated by
// scrollContainer being mounted, which only happens for groupBy === 'books'.
watch(viewMode, (v) => {
  try {
    localStorage.setItem(VIEWMODE_KEYS[groupBy.value], v)
  } catch {
    /* ignore */
  }
})

const visibleRange = ref({ start: 0, end: 20 }) // Initially show first 20 items

const visibleAudiobooks = computed(() => {
  return audiobooks.value.slice(visibleRange.value.start, visibleRange.value.end)
})

// Option: show extra details under each audiobook poster in grid view
const SHOW_ITEM_DETAILS_KEY = 'listenarr.showItemDetails'
const showItemDetails = ref<boolean>(false)

try {
  const stored = localStorage.getItem(SHOW_ITEM_DETAILS_KEY)
  if (stored !== null) showItemDetails.value = stored === 'true'
} catch {}

watch(showItemDetails, (v) => {
  try {
    localStorage.setItem(SHOW_ITEM_DETAILS_KEY, v ? 'true' : 'false')
  } catch {}
})

watch(showItemDetails, async () => {
  measuredRowHeight.value = null
  await nextTick()
  syncMeasuredRowHeight()
  updateVisibleRange()
})

watch(
  () => route.query.missing,
  (m) => {
    filterMissing.value = isMissingFieldKey(m) ? (m as MissingField) : null
  },
  { immediate: true },
)

// Whenever the drill-down field changes, fetch the authoritative ID list from
// the dashboard endpoint. The filter pipeline waits on it (so it doesn't show
// stale or incorrect results) and the chip shows a loading state while it
// loads.
watch(
  filterMissing,
  async (field) => {
    if (!field) {
      missingIdSet.value = null
      missingIdsLoading.value = false
      missingIdsError.value = null
      return
    }
    missingIdsLoading.value = true
    missingIdsError.value = null
    missingIdSet.value = null
    try {
      const resp = await apiService.getDashboardMissingIds(backendMissingFieldName(field))
      missingIdSet.value = new Set(resp.ids)
    } catch (err) {
      missingIdsError.value = err instanceof Error ? err.message : 'Failed to load drill-down IDs'
      logger.error('AudiobooksView: missing-IDs fetch failed', err)
    } finally {
      missingIdsLoading.value = false
    }
  },
  { immediate: true },
)

watch(
  () => route.query.author,
  (a) => {
    filterAuthor.value = typeof a === 'string' && a.trim() ? a : null
  },
  { immediate: true },
)

watch(
  () => route.query.narrator,
  (n) => {
    filterNarrator.value = typeof n === 'string' && n.trim() ? n : null
  },
  { immediate: true },
)

watch(
  () => route.query.genre,
  (g) => {
    filterGenre.value = typeof g === 'string' && g.trim() ? g : null
  },
  { immediate: true },
)

// ?language=<name> sets the existing toolbar language filter and remembers it
// was URL-driven so the drill-down chip's clear button resets it cleanly.
watch(
  () => route.query.language,
  (l) => {
    if (typeof l === 'string' && l.trim()) {
      filterLanguage.value = l
      filterLanguageFromUrl.value = true
    } else if (filterLanguageFromUrl.value) {
      // URL no longer carries language — leave filterLanguage where it is
      // (the user may have changed it via the dropdown) but stop counting it
      // as drill-down state.
      filterLanguageFromUrl.value = false
    }
  },
  { immediate: true },
)

watch(
  () => route.query.group,
  (g) => {
    try {
      const q = g as string | undefined
      const mode =
        q && ['books', 'authors', 'series'].includes(q)
          ? (q as 'books' | 'authors' | 'series')
          : 'books'
      // If the route changed the group, use setGroupBy so we run the same DOM/update/observer logic
      if (mode !== groupBy.value) {
        // fire-and-forget async to avoid blocking the router navigation
        void setGroupBy(mode)
      }
    } catch {}
  },
)

// --- URL <-> filter-state sync -----------------------------------------------
// Filter selection (the dropdown + toolbar quick-filters + sort) used to live
// only in component state, so navigating into a book and back lost the user's
// view. Everything below mirrors that state into `route.query` via
// `router.replace`, and hydrates the refs back when the URL changes (e.g. when
// the user uses the browser back button or follows a shared link).
//
// `router.replace` (not push) intentionally: one history entry per real
// navigation, not per filter tweak.

// Keys this view owns. Unknown keys (e.g. drill-down params like ?missing= or
// ?author= introduced by other PRs) are passed through untouched.
const MANAGED_QUERY_KEYS = [
  'group',
  'filter',
  'monitored',
  'status',
  'qp',
  'year',
  'sort',
  'dir',
] as const

function buildManagedQuery(): Record<string, string> {
  const out: Record<string, string> = {}
  if (groupBy.value !== 'books') out.group = groupBy.value
  if (selectedFilterId.value) out.filter = selectedFilterId.value
  if (filterMonitored.value !== 'all') out.monitored = filterMonitored.value
  if (filterStatus.value !== 'all') out.status = filterStatus.value
  if (filterQualityProfile.value !== 'all') out.qp = filterQualityProfile.value
  if (filterYear.value !== 'all') out.year = filterYear.value
  const def = DEFAULT_SORTS[groupBy.value]
  // Only emit sort if it differs from the group's default — keeps the URL
  // tidy in the common case.
  if (sortState[groupBy.value].key !== def.key) out.sort = sortState[groupBy.value].key
  if (sortState[groupBy.value].order !== def.order) out.dir = sortState[groupBy.value].order
  return out
}

function mergeManagedIntoRouteQuery(): Record<string, string> {
  const next: Record<string, string> = {}
  for (const [k, v] of Object.entries(route.query)) {
    if ((MANAGED_QUERY_KEYS as readonly string[]).includes(k)) continue
    if (typeof v === 'string') next[k] = v
  }
  return { ...next, ...buildManagedQuery() }
}

function queriesEqual(a: Record<string, string>, b: Record<string, string>): boolean {
  const ak = Object.keys(a)
  const bk = Object.keys(b)
  if (ak.length !== bk.length) return false
  for (const k of ak) if (a[k] !== b[k]) return false
  return true
}

function currentRouteQueryAsStringMap(): Record<string, string> {
  const out: Record<string, string> = {}
  for (const [k, v] of Object.entries(route.query)) {
    if (typeof v === 'string') out[k] = v
  }
  return out
}

watch(
  () => [
    groupBy.value,
    selectedFilterId.value,
    filterMonitored.value,
    filterStatus.value,
    filterQualityProfile.value,
    filterYear.value,
    sortState.books.key,
    sortState.books.order,
    sortState.authors.key,
    sortState.authors.order,
    sortState.series.key,
    sortState.series.order,
  ],
  () => {
    try {
      const desired = mergeManagedIntoRouteQuery()
      if (queriesEqual(desired, currentRouteQueryAsStringMap())) return
      router.replace({ path: '/audiobooks', query: desired })
    } catch (err) {
      logger.debug('Failed to write filter state to URL:', err)
    }
  },
  { flush: 'post' },
)

// URL -> ref: when the route changes externally (back/forward, shared link,
// dashboard drill-down click), re-hydrate the corresponding refs. Same-value
// ref assignment is a no-op for Vue's reactivity, so this won't loop with the
// writer above.
watch(
  () => route.query.filter,
  (v) => {
    selectedFilterId.value = typeof v === 'string' && v ? v : null
  },
)
watch(
  () => route.query.monitored,
  (v) => {
    filterMonitored.value =
      readEnumQuery(v, ['monitored', 'unmonitored'] as const) ?? 'all'
  },
)
watch(
  () => route.query.status,
  (v) => {
    filterStatus.value =
      readEnumQuery(v, ['downloaded', 'missing', 'mismatch', 'downloading'] as const) ?? 'all'
  },
)
watch(
  () => route.query.qp,
  (v) => {
    filterQualityProfile.value = typeof v === 'string' && v ? v : 'all'
  },
)
watch(
  () => route.query.year,
  (v) => {
    filterYear.value = typeof v === 'string' && v ? v : 'all'
  },
)
watch(
  () => [route.query.sort, route.query.dir, groupBy.value] as const,
  ([s, d]) => {
    const slot = sortState[groupBy.value]
    const def = DEFAULT_SORTS[groupBy.value]
    const allowed = sortOptions.value.map((o) => o.value)
    if (typeof s === 'string' && allowed.includes(s)) slot.key = s
    else slot.key = def.key
    if (d === 'asc' || d === 'desc') slot.order = d
    else slot.order = def.order
  },
)

function toggleItemDetails() {
  showItemDetails.value = !showItemDetails.value
}

function getRowHeight() {
  if (measuredRowHeight.value && measuredRowHeight.value > 0) {
    return measuredRowHeight.value
  }

  if (viewMode.value === 'grid') {
    if (scrollContainer.value && ITEMS_PER_ROW.value > 0) {
      const contentWidth = Math.max(0, scrollContainer.value.clientWidth - 40)
      const cardWidth = Math.max(
        0,
        Math.floor((contentWidth - GRID_GAP * (ITEMS_PER_ROW.value - 1)) / ITEMS_PER_ROW.value),
      )

      if (cardWidth > 0) {
        return cardWidth + GRID_GAP + (showItemDetails.value ? GRID_DETAILS_EXTRA_HEIGHT : 0)
      }
    }

    return GRID_ROW_HEIGHT_FALLBACK + (showItemDetails.value ? GRID_DETAILS_EXTRA_HEIGHT : 0)
  }
  return LIST_ROW_HEIGHT
}

function syncMeasuredRowHeight() {
  if (!scrollContainer.value) return false

  const itemSelector = viewMode.value === 'grid' ? '.audiobook-wrapper' : '.audiobook-list-item'
  const item = scrollContainer.value.querySelector<HTMLElement>(itemSelector)

  if (!item) {
    if (measuredRowHeight.value !== null) {
      measuredRowHeight.value = null
      return true
    }
    return false
  }

  let nextRowHeight = Math.ceil(item.getBoundingClientRect().height)
  if (viewMode.value === 'grid') {
    const grid = scrollContainer.value.querySelector<HTMLElement>('.audiobooks-grid')
    const rowGap = grid ? Number.parseFloat(getComputedStyle(grid).rowGap || '0') : GRID_GAP
    nextRowHeight += Number.isFinite(rowGap) ? rowGap : GRID_GAP
  }

  if (nextRowHeight <= 0) return false

  if (measuredRowHeight.value === null || Math.abs(measuredRowHeight.value - nextRowHeight) > 1) {
    measuredRowHeight.value = nextRowHeight
    return true
  }

  return false
}

// Update visible range based on scroll position
const updateVisibleRange = () => {
  if (!scrollContainer.value) return

  const scrollTop = scrollContainer.value.scrollTop
  const viewportHeight = scrollContainer.value.clientHeight

  const rowHeight = getRowHeight()

  // Calculate which rows are visible
  const firstVisibleRow = Math.floor(scrollTop / rowHeight)
  const visibleRowCount = Math.ceil(viewportHeight / rowHeight)

  // Items per row already set (1 for list, >1 for grid)
  const totalRows = Math.ceil(audiobooks.value.length / ITEMS_PER_ROW.value)
  const startRow = Math.max(0, firstVisibleRow - BUFFER_ROWS)
  const endRow = Math.min(firstVisibleRow + visibleRowCount + BUFFER_ROWS, totalRows)

  // Convert to item indices
  const startIndex = startRow * ITEMS_PER_ROW.value
  const endIndex = Math.min(endRow * ITEMS_PER_ROW.value, audiobooks.value.length)

  visibleRange.value = { start: startIndex, end: endIndex }
}

// Padding for offset positioning
const topPadding = computed(() => {
  const firstVisibleRow = Math.floor(visibleRange.value.start / ITEMS_PER_ROW.value)
  return firstVisibleRow * getRowHeight()
})

// Total scroll height so the container scrollbar reflects the full list
const totalHeight = computed(() => {
  const totalRows = Math.ceil(audiobooks.value.length / ITEMS_PER_ROW.value)
  return totalRows * getRowHeight()
})

const deleting = ref(false)
const showDeleteDialog = ref(false)
const deleteTarget = ref<Audiobook | null>(null)
const deleteFilesOnDisk = ref(false)
const deleteFolderOnDisk = ref(false)
const showBulkEditModal = ref(false)
const showOrganizeModal = ref(false)
const organizeAudiobookIds = ref<number[]>([])
const showEditModal = ref(false)
const editAudiobook = ref<Audiobook | null>(null)
const lastClickedIndex = ref<number | null>(null)

function computeAudiobookStatusRaw(audiobook: Audiobook): AudiobookStatus {
  return computeAudiobookStatus(audiobook, activeDownloadAudiobookIds.value, qualityProfiles.value)
}

const audiobookStatusById = computed(() => {
  const map = new Map<number, AudiobookStatus>()
  for (const book of libraryStore.audiobooks || []) {
    if (!book?.id) continue
    map.set(book.id, computeAudiobookStatusRaw(book))
  }
  return map
})

function getAudiobookStatus(audiobook: Audiobook): AudiobookStatus {
  return audiobookStatusById.value.get(audiobook.id) ?? computeAudiobookStatusRaw(audiobook)
}

// Native loading="lazy" handles all image loading automatically - no custom code needed

function handleClickOutside(event: Event) {
  const target = event.target as HTMLElement
  if (!target.closest('.group-dropdown')) {
    showGroupMenu.value = false
  }
}

let resizeObserver: ResizeObserver | null = null
let stopVisibleRangeWatch: (() => void) | null = null
let stopViewModeWatch: (() => void) | null = null

onMounted(async () => {
  document.addEventListener('click', handleClickOutside)
  await Promise.all([
    libraryStore.fetchLibrary(),
    configStore.loadApplicationSettings(),
    loadQualityProfiles(),
  ])

  // Calculate items per row based on container width
  if (scrollContainer.value) {
    const minItemWidth = 180
    const gap = 20

    const recalcItemsPerRow = () => {
      if (!scrollContainer.value) return
      const containerWidth = scrollContainer.value.clientWidth - 40 // Subtract padding
      const newItems =
        viewMode.value === 'list'
          ? 1
          : Math.floor((containerWidth + gap) / (minItemWidth + gap)) || 1
      if (newItems !== ITEMS_PER_ROW.value) {
        ITEMS_PER_ROW.value = newItems
      }
    }

    // Initial calculation (view mode persistence is handled at top level —
    // see VIEWMODE_KEYS / loadViewModeFor)
    recalcItemsPerRow()
    // Initialize visible range
    updateVisibleRange()

    // Wait for DOM update then attach lazy observer so posters start with placeholder and load when visible
    await nextTick()
    try {
      observeLazyImages()
    } catch (e: unknown) {
      errorTracking.captureException(e as Error, {
        component: 'AudiobooksView',
        operation: 'observeLazyImages',
      })
    }

    if (groupBy.value === 'authors') {
      try {
        await nextTick()
        observeAuthorCards()
      } catch {}
    }

    await nextTick()
    if (syncMeasuredRowHeight()) {
      updateVisibleRange()
      await nextTick()
    }

    // Re-run observer when visible range changes (virtual scrolling)
    stopVisibleRangeWatch = watch(
      () => visibleRange.value,
      async () => {
        await nextTick()
        if (syncMeasuredRowHeight()) {
          updateVisibleRange()
          await nextTick()
        }
        try {
          observeLazyImages()
        } catch (e: unknown) {
          errorTracking.captureException(e as Error, {
            component: 'AudiobooksView',
            operation: 'observeLazyImages',
          })
        }
      },
    )

    // Add resize observer to recalculate on window resize
    resizeObserver = new ResizeObserver(() => {
      // Guard against null - element may be unmounted during navigation
      if (!scrollContainer.value) return
      measuredRowHeight.value = null
      recalcItemsPerRow()
      updateVisibleRange()
    })
    resizeObserver.observe(scrollContainer.value)

    // Watch for view mode changes to recalc item layout
    stopViewModeWatch = watch(viewMode, async () => {
      measuredRowHeight.value = null
      recalcItemsPerRow()
      // wait a tick for layout to update then recalc range
      await nextTick()
      syncMeasuredRowHeight()
      updateVisibleRange()
    })

  }
})

onUnmounted(() => {
  try {
    resizeObserver?.disconnect()
  } catch {}
  resizeObserver = null

  try {
    stopVisibleRangeWatch?.()
  } catch {}
  stopVisibleRangeWatch = null

  try {
    stopViewModeWatch?.()
  } catch {}
  stopViewModeWatch = null

  try {
    authorCardObserver?.disconnect()
  } catch {}
  try {
    clearProtectedImages()
  } catch {}
  document.removeEventListener('click', handleClickOutside)
})

async function loadQualityProfiles() {
  try {
    qualityProfiles.value = await apiService.getQualityProfiles()
  } catch (error) {
    logger.warn('Failed to load quality profiles:', error)
  }
}

function getQualityProfileName(profileId?: number): string | null {
  if (!profileId) return null
  const profile = qualityProfiles.value.find((p) => p.id === profileId)
  return profile?.name ?? null
}

function statusText(status: AudiobookStatus): string {
  return formatAudiobookStatus(status)
}

function openStatusDetails(audiobook: Audiobook) {
  try {
    // Navigate to audiobook detail page and open the history tab (legacy "downloads" intent)
    void router.push({
      path: `/audiobooks/${audiobook.id}`,
      query: { tab: 'history' },
      hash: '#history',
    })
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'AudiobooksView',
      operation: 'openStatusDetails',
      metadata: { audiobookId: audiobook.id },
    })
  }
}

function navigateToDetail(id: number) {
  router.push(`/audiobooks/${id}`)
}

function toggleViewMode() {
  viewMode.value = viewMode.value === 'grid' ? 'list' : 'grid'
}

async function setGroupBy(mode: 'books' | 'authors' | 'series') {
  groupBy.value = mode
  showGroupMenu.value = false
  if (mode !== 'authors') {
    try {
      authorCardObserver?.disconnect()
    } catch {}
  }
  // Clear any active selection when switching grouping mode
  try {
    libraryStore.clearSelection()
  } catch (e: unknown) {
    errorTracking.captureException(e as Error, {
      component: 'AudiobooksView',
      operation: 'setGroupBy.clearSelection',
      metadata: { mode },
    })
  }
  // URL sync happens via the unified query writer watching groupBy.

  // Show image loading overlay and ensure DOM settles and recalc visible range for the virtual scroller
  imagesLoading.value = mode !== 'authors'
  try {
    await nextTick()
    try {
      updateVisibleRange()
    } catch (e: unknown) {
      errorTracking.captureException(e as Error, {
        component: 'AudiobooksView',
        operation: 'setGroupBy.updateVisibleRange',
        metadata: { mode },
      })
    }

    if (mode === 'authors') {
      await nextTick()
      observeAuthorCards()
      try {
        observeLazyImages()
      } catch {}
      return
    }

    // Wait for visible images to finish loading (or timeout)
    try {
      await waitForImagesToLoad(5000)
    } catch {
      // ignore, we'll clear loading overlay regardless
    }
  } catch (e) {
    errorTracking.captureException(e as Error, {
      component: 'AudiobooksView',
      operation: 'setGroupBy.scheduleObservation',
      metadata: { mode },
    })
  } finally {
    imagesLoading.value = false
  }
}

function navigateToCollection(collection: { name: string }) {
  const type = groupBy.value === 'authors' ? 'author' : 'series'
  router.push(`/collection/${type}/${encodeURIComponent(collection.name)}`)
}

async function waitForImagesToLoad(timeoutMs = 5000) {
  // Collect images within the relevant container
  const imgs: HTMLImageElement[] = []
  if (groupBy.value === 'books') {
    if (scrollContainer.value) {
      imgs.push(
        ...Array.from(
          scrollContainer.value.querySelectorAll<HTMLImageElement>(
            'img.audiobook-poster, img.series-cover-image',
          ),
        ),
      )
    }
  } else {
    const grouped = document.querySelector('.grouped-grid')
    if (grouped) imgs.push(...Array.from(grouped.querySelectorAll<HTMLImageElement>('img')))
  }

  const containerRect = scrollContainer.value?.getBoundingClientRect()
  const visibleImgs = containerRect
    ? imgs.filter((img) => {
        const rect = img.getBoundingClientRect()
        const margin = 100
        return (
          rect.bottom >= containerRect.top - margin &&
          rect.top <= containerRect.bottom + margin &&
          rect.right >= containerRect.left - margin &&
          rect.left <= containerRect.right + margin
        )
      })
    : imgs

  if (visibleImgs.length === 0) return

  await new Promise<void>((resolve) => {
    let remaining = visibleImgs.length
    const onDone = () => {
      remaining -= 1
      if (remaining <= 0) resolve()
    }

    setTimeout(() => resolve(), timeoutMs)

    visibleImgs.forEach((img) => {
      if (img.complete && img.naturalWidth && img.naturalWidth > 0) {
        onDone()
      } else {
        const onLoad = () => {
          cleanup()
          onDone()
        }
        const onError = () => {
          cleanup()
          onDone()
        }
        const cleanup = () => {
          img.removeEventListener('load', onLoad)
          img.removeEventListener('error', onError)
        }
        img.addEventListener('load', onLoad)
        img.addEventListener('error', onError)
      }
    })
  })
}

function handleImageError(event: Event) {
  try {
    const img = event.target as HTMLImageElement
    if (!img) return
    try {
      if ((img as unknown as { __imageFallbackDone?: boolean }).__imageFallbackDone) return
      ;(img as unknown as { __imageFallbackDone?: boolean }).__imageFallbackDone = true
    } catch (e: unknown) {
      errorTracking.captureException(e as Error, {
        component: 'AudiobooksView',
        operation: 'handleImageError.flagCheck',
      })
    }
    try {
      img.src = getPlaceholderUrl()
    } catch {}
    try {
      img.removeAttribute('data-src')
    } catch {}
    try {
      img.removeAttribute('data-original-src')
    } catch {}
    try {
      ;(img as unknown as { onerror?: null }).onerror = null
    } catch (e: unknown) {
      errorTracking.captureException(e as Error, {
        component: 'AudiobooksView',
        operation: 'handleImageError.clearHandler',
      })
    }
  } catch {}
}

// Series cover layout constants and helper
const SERIES_CONTAINER_WIDTH = 384
const COVER_SIZE = 192

function getCoverStyle(index: number, count: number) {
  const spacing = count <= 1 ? 0 : (SERIES_CONTAINER_WIDTH - COVER_SIZE) / Math.max(1, count - 1)
  const left = count === 1 ? (SERIES_CONTAINER_WIDTH - COVER_SIZE) / 2 : index * spacing
  const z = count === 1 ? 1 : Math.max(1, 100 - index)
  return {
    height: `${COVER_SIZE}px`,
    width: `${COVER_SIZE}px`,
    top: '0px',
    left: `${left}px`,
    zIndex: z,
    boxShadow: 'rgba(17, 17, 17, 0.4) 4px 0px 4px',
    borderRadius: '6px',
  }
}

async function refreshLibrary() {
  await libraryStore.fetchLibrary()
}

async function confirmDelete(audiobook: Audiobook) {
  deleteTarget.value = audiobook
  resetDeleteOptions()
  showDeleteDialog.value = true
}

function cancelDelete() {
  resetDeleteOptions()
  deleteTarget.value = null
  showDeleteDialog.value = false
}

async function executeDelete() {
  if (deleting.value || !deleteTarget.value) return

  deleting.value = true
  try {
    const shouldDeleteFolder = deleteFolderOnDisk.value
    const shouldDeleteFiles = deleteFilesOnDisk.value || shouldDeleteFolder
    await libraryStore.removeFromLibrary(deleteTarget.value.id, {
      deleteFiles: shouldDeleteFiles,
      deleteFolder: shouldDeleteFolder,
    })
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'AudiobooksView',
      operation: 'executeDelete',
      metadata: { audiobookId: deleteTarget.value?.id },
    })
  } finally {
    deleting.value = false
    resetDeleteOptions()
    deleteTarget.value = null
    showDeleteDialog.value = false
  }
}

async function confirmBulkDelete() {
  const count = libraryStore.selectedIds.size
  if (count === 0) return
  const message = `Are you sure you want to delete ${count} audiobook${count !== 1 ? 's' : ''}? This action cannot be undone.`
  const ok = await showConfirm(message, 'Confirm Deletion', {
    danger: true,
    confirmText: 'Delete',
    cancelText: 'Cancel',
  })
  if (!ok) return

  deleting.value = true
  try {
    const idsToDelete = Array.from(libraryStore.selectedIds)
    await libraryStore.bulkRemoveFromLibrary(idsToDelete)
  } catch (err) {
    errorTracking.captureException(err as Error, {
      component: 'AudiobooksView',
      operation: 'confirmBulkDelete',
      metadata: { count: libraryStore.selectedIds.size },
    })
  } finally {
    deleting.value = false
  }
}

function resetDeleteOptions() {
  deleteFilesOnDisk.value = false
  deleteFolderOnDisk.value = false
}

watch(deleteFolderOnDisk, (checked) => {
  if (checked && !deleteFilesOnDisk.value) {
    deleteFilesOnDisk.value = true
  }
})

watch(deleteFilesOnDisk, (checked) => {
  if (!checked && deleteFolderOnDisk.value) {
    deleteFolderOnDisk.value = false
  }
})

function showBulkEdit() {
  showBulkEditModal.value = true
}

function closeBulkEdit() {
  showBulkEditModal.value = false
}

function showOrganize() {
  organizeAudiobookIds.value = Array.from(libraryStore.selectedIds)
  showOrganizeModal.value = true
}

function closeOrganize() {
  showOrganizeModal.value = false
}

async function handleOrganizeDone() {
  showOrganizeModal.value = false
  await libraryStore.fetchLibrary()
  libraryStore.clearSelection()
}

async function handleBulkEditSaved() {
  // Refresh library to show updated data
  await libraryStore.fetchLibrary()

  // Clear selection after successful bulk edit
  libraryStore.clearSelection()
}

function openEditModal(audiobook: Audiobook) {
  // Always get the latest audiobook from the store to ensure we have the most recent data
  // This is important after edits that update the audiobook (like quality profile changes)
  const freshAudiobook = libraryStore.audiobooks.find((book) => book.id === audiobook.id)
  editAudiobook.value = freshAudiobook || audiobook
  showEditModal.value = true
}

function closeEditModal() {
  showEditModal.value = false
  editAudiobook.value = null
}

async function handleEditSaved() {
  // Refresh library to show updated data
  await libraryStore.fetchLibrary()

  // Update the editAudiobook reference with the fresh data
  if (editAudiobook.value) {
    const updated = libraryStore.audiobooks.find((book) => book.id === editAudiobook.value!.id)
    if (updated) {
      editAudiobook.value = updated
    }
  }
}

function applyCheckboxSelection(audiobook: Audiobook, shiftKey: boolean) {
  const currentIndex = audiobooks.value.findIndex((book) => book.id === audiobook.id)
  if (currentIndex < 0) return

  if (shiftKey && lastClickedIndex.value !== null) {
    const startIndex = Math.min(lastClickedIndex.value, currentIndex)
    const endIndex = Math.max(lastClickedIndex.value, currentIndex)
    libraryStore.clearSelection()
    for (let i = startIndex; i <= endIndex; i++) {
      const book = audiobooks.value[i]
      if (!book) continue
      libraryStore.toggleSelection(book.id)
    }
  } else {
    libraryStore.toggleSelection(audiobook.id)
  }

  lastClickedIndex.value = currentIndex
}

function handleCheckboxClick(audiobook: Audiobook, event: MouseEvent) {
  event.preventDefault() // Prevent browser text selection
  applyCheckboxSelection(audiobook, event.shiftKey)
}

function onCheckboxChange(audiobook: Audiobook, event: Event) {
  applyCheckboxSelection(audiobook, Boolean((event as MouseEvent | KeyboardEvent).shiftKey))
}

function handleCheckboxKeydown(audiobook: Audiobook, event: KeyboardEvent) {
  applyCheckboxSelection(audiobook, event.shiftKey)
}

// Expose for testing
defineExpose({
  setGroupBy,
  groupedCollections,
  showItemDetails,
  viewMode,
  toggleViewMode,
})
</script>

<style scoped>
.missing-filter-chip {
  display: flex;
  align-items: center;
  gap: 0.65rem;
  padding: 0.6rem 1rem;
  margin: 0.75rem 1rem;
  background: rgba(var(--brand-rgb), 0.12);
  border: 1px solid rgba(var(--brand-rgb), 0.35);
  border-radius: 6px;
  color: #ddd;
  font-size: 0.9rem;
}

.missing-filter-chip :deep(svg) {
  color: var(--brand-500);
}

.missing-filter-chip strong {
  color: #fff;
  font-weight: 600;
}

.drilldown-summary {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 0.4rem 0.65rem;
}

.drilldown-prefix {
  color: #ccc;
}

.drilldown-item {
  display: inline-flex;
  align-items: baseline;
  gap: 0.3rem;
  padding: 0.1rem 0.5rem;
  background: rgba(var(--brand-rgb), 0.15);
  border: 1px solid rgba(var(--brand-rgb), 0.3);
  border-radius: 4px;
}

.drilldown-item-label {
  color: #999;
  font-size: 0.8rem;
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.drilldown-count {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  color: #aaa;
  font-size: 0.85rem;
}

.drilldown-error {
  color: #e74c3c;
}

.missing-filter-clear {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 0.3rem;
  background: transparent;
  border: none;
  border-radius: 4px;
  color: #999;
  cursor: pointer;
  transition: background 0.15s;
}

.missing-filter-clear:hover {
  background: rgba(255, 255, 255, 0.08);
  color: #fff;
}

.audiobooks-view {
  --audiobooks-toolbar-height: 60px;
  --audiobooks-toolbar-offset: var(--audiobooks-toolbar-height);
  background-color: #1a1a1a;
  margin-top: var(--audiobooks-toolbar-offset); /* Space for fixed local toolbar */
  min-height: calc(100dvh - var(--app-top-offset, 60px) - var(--audiobooks-toolbar-offset));
  --legend-height: 44px;
  position: relative;
}

@media (max-width: 1024px) {
  .audiobooks-view {
    margin-top: var(--audiobooks-toolbar-offset);
  }
}

@media (max-width: 768px) {
  .audiobooks-view {
    margin-top: var(--audiobooks-toolbar-offset);
  }
}

.toolbar {
  position: fixed;
  top: var(--app-top-offset, 60px); /* Account for global header + optional banner */
  left: 200px; /* Account for sidebar width */
  right: 0;
  z-index: 500; /* Below global nav (1000) but above content overlays */
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: center;
  padding: 12px 20px;
  background-color: #2a2a2a;
  border-bottom: 1px solid #333;
  margin-bottom: 20px;
}

@media (max-width: 1200px) {
  .toolbar {
    padding: 10px 16px;
    gap: 6px;
  }
}

@media (max-width: 768px) {
  .toolbar {
    left: 0; /* Full width on mobile */
    padding: 8px 12px;
    gap: 4px;
  }
}

@media (max-width: 480px) {
  .toolbar {
    padding: 6px 8px;
    gap: 2px;
  }
}

.toolbar-left,
.toolbar-right {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

@media (max-width: 1200px) {
  .toolbar-left,
  .toolbar-right {
    gap: 6px;
  }
}

@media (max-width: 768px) {
  .toolbar-left,
  .toolbar-right {
    gap: 4px;
  }
}

@media (max-width: 480px) {
  .toolbar-left,
  .toolbar-right {
    gap: 2px;
  }
}

.toolbar-btn {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 8px 14px;
  background-color: transparent;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  color: #e6eef8;
  font-size: 12px;
  cursor: pointer;
  white-space: nowrap;
  transition:
    background-color 0.12s ease,
    transform 0.08s ease,
    box-shadow 0.12s ease;
}

.toolbar-btn:hover {
  background-color: rgba(255, 255, 255, 0.03);
  transform: translateY(-1px);
  box-shadow: 0 6px 18px rgba(0, 0, 0, 0.45);
}

.toolbar-btn.active {
  background-color: var(--brand-500);
  border-color: var(--brand-500);
  color: #fff;
}

.toolbar-btn.edit-btn {
  background-color: #2196f3;
  border-color: #1976d2;
  color: #fff;
}

.toolbar-btn.edit-btn:hover {
  background-color: #1976d2;
}

.toolbar-btn.delete-btn {
  background-color: #e74c3c;
  border-color: #c0392b;
  color: #fff;
}

.toolbar-btn.delete-btn:hover {
  background-color: #c0392b;
}

/* Accessibility: strong focus ring for keyboard users */
.toolbar-btn:focus-visible {
  outline: 3px solid rgba(33, 150, 243, 0.18);
  outline-offset: 2px;
}

@media (max-width: 1200px) {
  .toolbar-btn {
    padding: 7px 10px;
    font-size: 11px;
  }
}

@media (max-width: 1024px) {
  .toolbar-btn {
    padding: 8px;
    min-width: 36px;
    justify-content: center;
    font-size: 0;
    gap: unset;
  }

  .toolbar-btn svg {
    font-size: 16px;
    width: 16px;
    height: 16px;
  }

  .count-badge {
    display: none;
  }

  .toolbar-search {
    min-width: 120px;
  }

  .select-trigger {
    width: fit-content;
  }

  .select-dropdown {
    min-width: 120px;
    max-width: 160px;
  }
}

@media (max-width: 480px) {
  .toolbar-btn {
    padding: 6px;
    min-width: 32px;
  }
}

.toolbar-filters {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-left: 8px;
}

@media (max-width: 1200px) {
  .toolbar-filters {
    gap: 6px;
    margin-left: 4px;
  }
}

@media (max-width: 768px) {
  .toolbar-filters {
    gap: 4px;
    margin-left: 0;
  }
}

@media (max-width: 480px) {
  .toolbar-filters {
    gap: 2px;
  }
}
.toolbar-search {
  background: rgba(255, 255, 255, 0.02);
  border: 1px solid rgba(255, 255, 255, 0.04);
  color: #e6eef8;
  padding: 8px 8px;
  border-radius: 6px;
  min-width: 180px;
}

@media (max-width: 1200px) {
  .toolbar-search {
    min-width: 140px;
    padding: 7px 6px;
    font-size: 11px;
  }
}

@media (max-width: 768px) {
  .toolbar-search {
    min-width: 100px;
    padding: 6px 6px;
    font-size: 10px;
  }
}

@media (max-width: 480px) {
  .toolbar-search {
    min-width: 80px;
    padding: 4px 4px;
    font-size: 9px;
  }
}
.toolbar-select {
  background-color: #2a2a2a; /* match CustomSelect trigger */
  border: 1px solid rgba(255, 255, 255, 0.08);
  color: #e6eef8;
  padding: 8px 10px;
  border-radius: 6px;
  min-height: 36px;
  -webkit-appearance: none;
  -moz-appearance: none;
  appearance: none;
  background-image:
    linear-gradient(45deg, transparent 50%, rgba(255, 255, 255, 0.12) 50%),
    linear-gradient(135deg, rgba(255, 255, 255, 0.12) 50%, transparent 50%);
  background-position:
    calc(100% - 14px) calc(1em + 2px),
    calc(100% - 10px) calc(1em + 2px);
  background-size:
    6px 6px,
    6px 6px;
  background-repeat: no-repeat;
}

.toolbar-custom-select {
  width: auto;
  display: inline-block;
}
.toolbar-select option {
  background: #2a2a2a;
  color: #e6eef8;
}

.count-badge {
  display: inline-flex;
  align-items: center;
  padding: 8px 12px;
  background-color: var(--brand-500);
  border-radius: 6px;
  color: #fff;
  font-size: 12px;
  transition: background-color 0.12s ease;
}

.count-badge:hover,
.count-badge:focus {
  background-color: var(--brand-700);
}

@media (max-width: 1200px) {
  .count-badge {
    padding: 7px 10px;
    font-size: 11px;
  }
}

@media (max-width: 1024px) {
  .count-badge {
    display: none;
  }
}

@media (max-width: 480px) {
  .count-badge {
    padding: 6px 8px;
    font-size: 10px;
  }
}

.group-dropdown {
  position: relative;
}

.group-btn {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}

@media (max-width: 768px) {
  .group-btn {
    gap: 0.2rem;
  }
}

.group-menu {
  position: absolute;
  top: calc(100% + 6px);
  left: 0;
  background: #2a2a2a;
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.6);
  z-index: 1100;
  min-width: 180px;
  max-height: 60vh;
  overflow-y: auto;
}

.menu-item {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.75rem 1rem;
  color: var(--text-color);
  cursor: pointer;
  font-size: 12px;
  transition: background-color 0.15s;
  width: 100%;
  border: none;
  background: transparent;
  text-align: left;
}

.menu-item:hover {
  background-color: rgba(255, 255, 255, 0.18);
  color: #fff;
}

.menu-item.active {
  background-color: rgba(33, 150, 243, 0.1);
  color: #fff;
}

.menu-item:first-child {
  border-radius: 6px;
}

.menu-item:last-child {
  border-radius: 6px;
}

.grouped-view {
  padding: 1rem;
}

.grouped-grid {
  display: grid;
  /* Match audiobook grid item minimum size so collection covers align visually */
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: 1rem;
}

.collection-card {
  overflow: visible;
  cursor: pointer;
  transition: all 0.2s;
  position: relative;
}

.collection-card:hover {
  border-color: var(--primary-color);
  transform: translateY(-2px);
}

.collection-cover {
  aspect-ratio: 1/1;
  overflow: hidden;
  position: relative;
}

/* Special styling for series cards */
.collection-card:has(.series-covers-container) {
  margin-bottom: 2rem; /* Extra space for bottom placard */
  /* Make series cards span two columns so they're visually larger than single-item collections */
  grid-column: span 2;
}

.collection-card:has(.series-covers-container) .collection-cover {
  aspect-ratio: 2/1;
  height: 192px;
  background: #2a2a2a;
  border-radius: 6px;
}

.collection-cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  border-radius: 6px;
}

.series-covers-container {
  position: relative;
  /* fixed width to allow stacked covers (two visible columns) */
  width: 384px;
  height: 192px;
  overflow: hidden;
}

.series-count-badge {
  position: absolute;
  top: 0.375em;
  right: 0.375em;
  background-color: var(--brand-500);
  color: white;
  padding: 0.1em 0.25em;
  border-radius: 0.5rem; /* rounded-lg like sample */
  font-size: 0.8rem;
  font-weight: 500;
  z-index: 20;
  min-width: 1.5rem;
  text-align: center;
  transition:
    background-color 0.12s ease,
    box-shadow 0.12s ease;
}

.series-count-badge:hover,
.series-count-badge:focus {
  background-color: #005fa3;
  box-shadow: 0 4px 12px rgba(var(--brand-rgb), 0.15);
}

.series-count-badge:focus {
  outline: 2px solid rgba(var(--brand-rgb), 0.2);
  outline-offset: 2px;
}

.series-covers {
  position: relative;
  width: 100%;
  height: 100%;
}

.series-cover-item {
  position: absolute;
  top: 0;
  transition: transform 0.2s ease;
}

.series-cover-image {
  width: 100%;
  height: 100%;
  object-fit: cover;
  border-radius: 6px;
}

/* legacy .series-hover-overlay removed — series now use the shared .status-overlay hover styling */

/* Blurred background used when a series has a single cover */
.series-single-bg {
  position: absolute;
  inset: 0;
  background-size: cover;
  background-position: center;
  filter: blur(10px) contrast(0.9) brightness(0.7);
  transform: scale(1.05);
  z-index: 1;
}

.series-cover-item .series-cover-image.centered {
  /* place the single cover centered above the blurred background */
  width: 192px;
  height: 192px;
  object-fit: cover;
  border-radius: 6px;
  position: relative;
  z-index: 1;
  left: 0;
  top: 0;
  border-radius: 6px;
}

/* legacy .series-hover-overlay rules removed; use .status-overlay for hover */

/* Show the same bottom status-overlay for collection covers on hover */
.collection-cover:hover .status-overlay,
.series-covers-container:hover .status-overlay {
  padding: 80px 8px 8px;
  opacity: 1;
}

/* Reveal title/author text inside collection/series/author covers on hover (match book poster behavior) */
.collection-cover:hover .audiobook-title,
.collection-cover:hover .audiobook-author,
.series-covers-container:hover .audiobook-title,
.series-covers-container:hover .audiobook-author,
.author-poster:hover .audiobook-title,
.author-poster:hover .audiobook-author {
  opacity: 1;
}

.series-bottom-placard {
  margin-top: 0.5rem;
  display: flex;
  justify-content: center;
  z-index: 10;
}

.series-bottom-content {
  width: 100%;
  max-width: 384px; /* match series-covers-container width */
  height: 100%;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 0 0.5rem;
  margin: 0 auto;
}

/* Use same typography as grid-bottom-details to keep consistency */
.series-bottom-title {
  font-size: 12px; /* match grid-bottom-details .detail-line.title */
  color: #fff;
  margin: 0 0 4px 0;
  font-weight: 500;
  text-align: center;
}

.series-bottom-count {
  font-size: 12px; /* match grid-bottom-details .detail-line */
  color: #bfcad6;
  margin: 0;
  text-align: center;
}

.collection-title {
  font-weight: 500;
  margin-bottom: 0.5rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.collection-count {
  color: var(--text-muted);
  font-size: 0.875rem;
}

.audiobooks-scroll-container {
  height: calc(
    100dvh - var(--app-top-offset, 60px) - var(--audiobooks-toolbar-offset) - var(--legend-height) -
      1px
  );
  overflow-y: auto;
  overflow-x: hidden;
  padding: 0 20px;
}

.audiobook-status-legend {
  position: fixed;
  left: 200px;
  right: 0;
  bottom: 0;
  min-height: var(--legend-height);
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 10px 16px;
  padding: 10px 20px;
  background: linear-gradient(180deg, rgba(26, 26, 26, 0.6) 0%, #1a1a1a 40%);
  border-top: 1px solid rgba(255, 255, 255, 0.06);
  z-index: 200;
  color: #bfcad6;
  font-size: 12px;
}

.legend-title {
  color: #e6eef8;
  font-weight: 500;
}

.legend-item {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.legend-dot {
  width: 10px;
  height: 10px;
  border-radius: 999px;
  display: inline-block;
}

.legend-dot.status-downloading {
  background-color: #3498db;
}

.legend-dot.status-no-file {
  background-color: #e74c3c;
}

.legend-dot.status-quality-mismatch {
  background-color: #f39c12;
}

.legend-dot.status-quality-match {
  background-color: #2ecc71;
}

@media (max-width: 768px) {
  .audiobooks-view {
    --legend-height: 36px;
  }

  .audiobook-status-legend {
    left: 0;
    padding: 8px 12px 12px;
    gap: 8px;
    flex-wrap: nowrap;
    overflow-x: hidden;
  }

  .legend-title {
    flex: 0 0 auto;
    font-size: 11px;
  }

  .legend-item {
    flex: 0 0 auto;
    white-space: nowrap;
    font-size: 11px;
  }

  .legend-dot {
    width: 8px;
    height: 8px;
  }
}

@media (max-width: 480px) {
  .audiobooks-view {
    --legend-height: 32px;
  }

  .legend-title {
    display: none;
  }
}

.audiobooks-scroll-spacer {
  position: relative;
  width: 100%;
}

.audiobooks-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
  gap: 20px;
  padding: 10px 0;
  user-select: none;
  -webkit-user-select: none;
  -moz-user-select: none;
  -ms-user-select: none;
  will-change: transform;
}

.audiobook-item {
  cursor: pointer;
  transition: transform 0.2s ease;
  position: relative;
}

.audiobook-item:hover {
  transform: scale(1.05);
}

.row-click-target {
  position: absolute;
  inset: 0;
  z-index: 10; /* sits below action buttons and checkboxes */
  /* allow pointer events to pass through so hover/clicks on adjacent rows still work
     clicks will fall through to the parent row's @click handler; controls remain interactive
     because they have higher z-index and default pointer-events:auto */
  pointer-events: none;
}

.audiobook-item.selected .audiobook-poster-container {
  outline: 3px solid var(--brand-focus);
  outline-offset: 2px;
}

.audiobook-item.status-no-file .audiobook-poster-container {
  border-bottom: 3px solid #e74c3c;
}

.audiobook-item.status-downloading .audiobook-poster-container {
  border-bottom: 3px solid #3498db;
  animation: pulse 2s ease-in-out infinite;
}

@keyframes pulse {
  0%,
  100% {
    border-bottom-color: #3498db;
  }
  50% {
    border-bottom-color: #5dade2;
  }
}

.audiobook-item.status-quality-mismatch .audiobook-poster-container {
  border-bottom: 3px solid #f39c12;
}

.audiobook-item.status-quality-match .audiobook-poster-container {
  border-bottom: 3px solid #2ecc71;
}

/* List view status borders */
.audiobook-list-item.status-no-file .list-thumb {
  border-bottom: 3px solid #e74c3c;
}

.audiobook-list-item.status-downloading .list-thumb {
  border-bottom: 3px solid #3498db;
  animation: pulse 2s ease-in-out infinite;
}

.audiobook-list-item.status-quality-mismatch .list-thumb {
  border-bottom: 3px solid #f39c12;
}

.audiobook-list-item.status-quality-match .list-thumb {
  border-bottom: 3px solid #2ecc71;
}

.selection-checkbox {
  /* default used in grid; overridden in list below */
  position: absolute;
  top: 8px;
  left: 8px;
  z-index: 40; /* keep checkbox above row click overlay */
  height: 22px;
  width: 22px;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0;
  box-sizing: border-box;
  background-color: rgba(0, 0, 0, 0.45);
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 6px;
  cursor: pointer;
  transition: all 0.12s ease;
  opacity: 0;
  user-select: none;
  -webkit-user-select: none;
  -moz-user-select: none;
  -ms-user-select: none;
}
/* Hide the native input visually but keep it accessible and interactive */
.selection-checkbox input[type='checkbox'] {
  position: absolute;
  inset: 0;
  margin: 0;
  padding: 0;
  width: 100%;
  height: 100%;
  opacity: 0;
  cursor: pointer;
  z-index: 41; /* ensure native input is above overlay and container pseudo-elements */
}

/* Draw a custom box and checkmark using container pseudo-elements */
.selection-checkbox::before {
  content: '';
  position: absolute;
  left: 50%;
  top: 50%;
  transform: translate(-50%, -50%);
  width: 14px;
  height: 14px;
  border-radius: 4px;
  border: 2px solid rgba(255, 255, 255, 0.14);
  background: transparent;
  box-sizing: border-box;
  transition:
    border-color 0.12s ease,
    background-color 0.12s ease,
    box-shadow 0.12s ease;
  z-index: 1;
}

.selection-checkbox:hover {
  background-color: rgba(0, 0, 0, 0.6);
  border-color: rgba(255, 255, 255, 0.18);
}

/* Custom checkmark */

/* Remove container hover darkening when focusing the native checkbox so contrast stays good */
.selection-checkbox:hover input[type='checkbox'] {
  transform: translateY(0);
}

/* Only show checkbox when hovered or selected */

.audiobook-item:hover .selection-checkbox,
.audiobook-item.selected .selection-checkbox,
.audiobook-list-item:hover .selection-checkbox,
.audiobook-list-item.selected .selection-checkbox,
.audiobooks-scroll-container.has-selection .selection-checkbox {
  opacity: 1;
}

/* When the item is selected, style the custom box and show the check */
.audiobook-item.selected .selection-checkbox::before,
.audiobook-list-item.selected .selection-checkbox::before {
  background-color: var(--brand-500);
  border-color: var(--brand-500);
  box-shadow: 0 0 0 4px rgba(var(--brand-rgb), 0.12);
}

.audiobook-item.selected .selection-checkbox::after,
.audiobook-list-item.selected .selection-checkbox::after {
  border-right-color: #fff;
  border-bottom-color: #fff;
  transform: translate(-50%, -50%) rotate(45deg) scale(1);
}

/* Checked state for grid and list rows */
.audiobook-item.selected .selection-checkbox input[type='checkbox'],
.audiobook-list-item.selected .selection-checkbox input[type='checkbox'] {
  /* keep native checked UI; add slight background for custom look */
  background-color: transparent;
}

/* Focus outlines for keyboard navigation */
.selection-checkbox input[type='checkbox']:focus-visible {
  outline: 2px solid rgba(var(--brand-rgb), 0.3);
  outline-offset: 2px;
}

.audiobook-list-item:focus,
.audiobook-list-item:focus-within,
.audiobook-item:focus,
.audiobook-item:focus-within {
  outline: 2px solid rgba(var(--brand-rgb), 0.18);
  outline-offset: 2px;
  background-color: rgba(255, 255, 255, 0.02);
}

/* List-specific override for the checkbox so it participates in the grid */
.audiobooks-list .selection-checkbox {
  position: relative;
  top: auto;
  left: auto;
  z-index: 40; /* ensure list checkboxes stay above the row overlay */
  height: 20px;
  width: 20px;
  margin: 0;
  background-color: rgba(0, 0, 0, 0);
  border: 1px solid rgba(255, 255, 255, 0.06);
  display: flex;
  align-items: center;
  justify-content: center;
}

/* In list view, always show checkboxes (outline). Filled/checkmark still only shows for selected items */
.audiobooks-list .selection-checkbox {
  opacity: 1;
}
.audiobooks-list .selection-checkbox::before {
  opacity: 1;
}
.audiobooks-list .selection-checkbox input[type='checkbox'] {
  opacity: 0; /* native input remains visually hidden */
}

.audiobooks-list .selection-checkbox {
  justify-self: center;
}

.audiobooks-list .selection-checkbox::after {
  left: 6px;
  top: 2px;
}

.audiobook-poster-container {
  position: relative;
  aspect-ratio: 1/1;
  border-radius: 6px;
  overflow: hidden;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.5);
}

.image-loading-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  background-color: rgba(0, 0, 0, 0.45);
  z-index: 10;
}

.image-loading-overlay .ph-spin.small {
  width: 28px;
  height: 28px;
  font-size: 24px;
}

.audiobook-poster {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
}

.author-poster {
  background: #1f1f1f;
}

.author-placeholder {
  position: absolute;
  inset: 0;
  background: linear-gradient(90deg, #242424 0%, #2f343a 50%, #242424 100%);
  background-size: 200% 100%;
  animation: authorShimmer 1.6s ease infinite;
  opacity: 1;
  transition: opacity 0.2s ease;
  z-index: 1;
}

.author-placeholder.loaded {
  opacity: 0;
}

.author-placeholder-icon {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  color: #9aa4b2;
  z-index: 2;
  opacity: 1;
  transition: opacity 0.2s ease;
}

.author-placeholder-icon svg {
  height: 3em;
  width: 3em;
}

.author-placeholder-icon.loaded {
  opacity: 0;
}

.author-cover {
  position: absolute;
  inset: 0;
  z-index: 3;
  opacity: 0;
  transition: opacity 0.2s ease;
}

.author-cover.loaded {
  opacity: 1;
}

@keyframes authorShimmer {
  0% {
    background-position: 200% 0;
  }
  100% {
    background-position: -200% 0;
  }
}

.status-overlay {
  position: absolute;
  bottom: 0;
  left: 0;
  right: 0;
  background: linear-gradient(transparent, rgba(0, 0, 0, 0.9));
  padding: 8px;
  transition:
    padding 0.2s ease,
    opacity 0.2s ease;
  opacity: 0;
  z-index: 101; /* ensure overlay sits above covers but below action buttons */
  pointer-events: none; /* don't block action buttons or other controls */
}

.audiobook-poster-container:hover .status-overlay {
  padding: 80px 8px 8px;
  opacity: 1;
}

/* When 'show-details' class is present, render overlay expanded */
.audiobook-poster-container.show-details .status-overlay {
  padding: 80px 8px 8px;
  opacity: 1;
}

/* When global details toggle is enabled, hide status overlay for author and series collections (only show on hover disabled) */
.audiobooks-view.details-enabled .collection-card.author-collection .status-overlay,
.audiobooks-view.details-enabled .collection-card.series-collection .status-overlay {
  opacity: 0 !important;
  pointer-events: none !important;
}

.audiobook-poster-container .audiobook-title,
.audiobook-poster-container .audiobook-author {
  opacity: 0;
  transition: opacity 0.2s ease;
}

.audiobook-poster-container.show-details .audiobook-title,
.audiobook-poster-container.show-details .audiobook-author {
  opacity: 1;
}

.audiobook-extra-details {
  margin-top: 8px;
  color: #e6eef8;
}
.audiobook-extra-details .detail-line {
  font-size: 12px;
  line-height: 1.2;
  margin: 2px 0;
  color: #cfd8e3;
}
.audiobook-extra-details .detail-line.title {
  font-weight: 500;
  color: #fff;
}
.audiobook-extra-details .detail-line.small {
  font-size: 11px;
  color: #bfcad6;
}
.list-extra-details {
  margin-top: 6px;
  color: #e6eef8;
}
.list-extra-details .detail-line {
  font-size: 12px;
  color: #bfcad6;
}
.grid-bottom-details {
  margin-top: 8px;
  color: #e6eef8;
  padding: 0 4px;
  width: 100%;
}
.grid-bottom-details .detail-line {
  font-size: 12px;
  color: #bfcad6;
  text-align: center;
}
.grid-bottom-details .detail-line.title {
  color: #fff;
  font-weight: 500;
  margin-bottom: 4px;
}

.audiobook-title {
  font-size: 13px;
  font-weight: 500;
  color: #fff;
  margin-bottom: 4px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  opacity: 0;
  transition: opacity 0.2s ease;
}

.audiobook-author {
  font-size: 11px;
  color: #ccc;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  opacity: 0;
  transition: opacity 0.2s ease;
}

.audiobook-poster-container:hover .audiobook-title,
.audiobook-poster-container:hover .audiobook-author {
  opacity: 1;
}

.quality-profile-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  margin-top: 0.5rem;
  padding: 0.25rem 0.5rem;
  margin-right: 0.5rem;
  background-color: rgba(52, 152, 219, 0.2);
  border: 1px solid rgba(52, 152, 219, 0.4);
  border-radius: 6px;
  font-size: 10px;
  font-weight: 500;
  color: #3498db;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
}

.status-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.25rem 0.5rem;
  margin-right: 0.5rem;
  background-color: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 6px;
  font-size: 10px;
  font-weight: 500;
  color: #cfcfcf;
  margin-top: 0.5rem;
  cursor: pointer;
  white-space: nowrap;
}

.status-badge.no-file {
  background-color: rgba(231, 76, 60, 0.12);
  border-color: rgba(231, 76, 60, 0.18);
  color: #e74c3c;
}

.status-badge.downloading {
  background-color: rgba(52, 152, 219, 0.1);
  border-color: rgba(52, 152, 219, 0.2);
  color: #3498db;
}

.status-badge.quality-mismatch {
  background-color: rgba(243, 156, 18, 0.1);
  border-color: rgba(243, 156, 18, 0.18);
  color: #f39c12;
}

.status-badge.quality-match {
  background-color: rgba(46, 204, 113, 0.1);
  border-color: rgba(46, 204, 113, 0.18);
  color: #2ecc71;
}

.quality-profile-badge i {
  font-size: 12px;
  flex-shrink: 0;
}

.monitored-badge {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  margin-top: 0.5rem;
  padding: 0.25rem 0.5rem;
  margin-left: 0.25rem;
  background-color: rgba(46, 204, 113, 0.2);
  border: 1px solid rgba(46, 204, 113, 0.4);
  border-radius: 6px;
  font-size: 10px;
  font-weight: 500;
  color: #2ecc71;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
}

.monitored-badge.unmonitored {
  background-color: rgba(231, 76, 60, 0.2);
  border-color: rgba(231, 76, 60, 0.4);
  color: #e74c3c;
}

.monitored-badge i {
  font-size: 12px;
  flex-shrink: 0;
}

.action-buttons {
  position: absolute;
  top: 8px;
  right: 8px;
  display: flex;
  gap: 4px;
  opacity: 0;
  transition: opacity 0.2s;
  z-index: 30; /* keep action buttons above the row click overlay */
}

.audiobook-item:hover .action-buttons {
  opacity: 1;
}

.action-btn {
  padding: 6px 8px;
  background-color: rgba(0, 0, 0, 0.8);
  border: 1px solid rgba(255, 255, 255, 0.2);
  border-radius: 6px;
  color: white;
  cursor: pointer;
  font-size: 14px;
  transition: background-color 0.2s;
}

.action-btn:hover {
  background-color: rgba(0, 0, 0, 0.95);
}

.delete-btn-small {
  background-color: rgba(231, 76, 60, 0.9);
  border-color: rgba(192, 57, 43, 0.5);
}

.delete-btn-small:hover {
  background-color: rgba(192, 57, 43, 1);
}

.edit-btn-small {
  background-color: rgba(52, 152, 219, 0.9);
  border-color: rgba(41, 128, 185, 0.5);
}

.edit-btn-small:hover {
  background-color: rgba(41, 128, 185, 1);
}

.loading-state,
.empty-state,
.error-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  height: calc(
    100dvh - var(--app-top-offset, 60px) - var(--audiobooks-toolbar-offset) - var(--legend-height)
  );
  color: #ccc;
  text-align: center;
}

.loading-state i,
.empty-icon,
.error-icon {
  font-size: 4rem;
  color: #868e96;
  margin-bottom: 1rem;
}

.loading-state i {
  color: var(--brand-500);
}

.error-icon {
  color: #e74c3c;
}

.error-state h2 {
  color: white;
  margin-bottom: 0.5rem;
}

.error-state p {
  margin-bottom: 2rem;
  color: #e74c3c;
}

.retry-button {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 12px 24px;
  background-color: var(--brand-500);
  color: white;
  border: none;
  border-radius: 6px;
  cursor: pointer;
  font-weight: 500;
  transition: background-color 0.2s;
}

.retry-button:hover {
  background-color: var(--brand-700);
}

.empty-state h2 {
  color: white;
  margin-bottom: 0.5rem;
}

.empty-state p {
  margin-bottom: 2rem;
}

.add-button {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  padding: 12px 24px;
  background-color: var(--brand-500);
  color: white;
  border-radius: 6px;
  text-decoration: none;
  font-weight: 500;
  transition: background-color 0.2s;
}

.add-button:hover {
  background-color: var(--brand-700);
}

/* Delete dialog styling is centralized in `src/assets/modals.css` */
/* Legacy .dialog classes are still used in a few places (e.g., Audiobook detail delete), but visual styles are now centralized. */
/* Modal action button colors are centralized in `src/assets/modals.css` */

/* List view styles */
.audiobooks-list {
  display: flex;
  flex-direction: column;
  padding: 8px 0;
}

.audiobook-list-item {
  display: grid;
  grid-template-columns: 40px 64px minmax(0, 1.5fr) minmax(0, 1fr) minmax(0, 1fr) 260px 120px;
  gap: 12px;
  align-items: center;
  padding: 10px 12px;
  background-color: transparent;
  border-radius: 6px;
  transition:
    background-color 0.12s,
    transform 0.12s;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
  cursor: pointer;
}

.audiobook-list-item:hover {
  background-color: rgba(255, 255, 255, 0.02);
  transform: translateY(-1px);
}

/* When a row is selected, apply the same hover visual treatment so it appears highlighted */
.audiobook-list-item.selected {
  background-color: rgba(255, 255, 255, 0.02);
  transform: translateY(-1px);
}

.list-thumb {
  width: 56px;
  height: 56px;
  object-fit: cover;
  border-radius: 6px;
  flex-shrink: 0;
}

.list-details {
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.list-details .audiobook-title {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  font-size: 14px;
  color: #fff;
}

.list-details .audiobook-author {
  font-size: 12px;
  color: #ccc;
}

.list-actions {
  margin-left: 0;
  display: flex;
  gap: 8px;
  align-items: center;
  justify-self: end;
}

/* Header row to mimic table columns */
.list-header {
  display: grid;
  grid-template-columns: 40px 64px minmax(0, 1.5fr) minmax(0, 1fr) minmax(0, 1fr) 260px 120px;
  gap: 12px;
  padding: 8px 12px;
  color: #aaa;
  font-size: 12px;
  border-bottom: 1px solid rgba(255, 255, 255, 0.04);
  align-items: center;
}

.list-header .col-cover {
  opacity: 0.9;
  text-align: center;
}
.list-header .col-title {
  opacity: 0.9;
}
.list-header .col-series,
.list-header .col-narrator {
  opacity: 0.9;
  display: flex;
  align-items: center;
  gap: 4px;
}
.list-header .col-series.sortable,
.list-header .col-narrator.sortable {
  cursor: pointer;
  user-select: none;
}
.list-header .col-series.sortable:hover,
.list-header .col-narrator.sortable:hover {
  color: #fff;
}
.list-header .col-series.sort-active,
.list-header .col-narrator.sort-active {
  color: #fff;
  opacity: 1;
}
.list-header .col-series.sortable:focus-visible,
.list-header .col-narrator.sortable:focus-visible {
  outline: 2px solid rgba(140, 180, 255, 0.6);
  outline-offset: 2px;
  border-radius: 3px;
}
.list-header .sort-caret {
  width: 12px;
  height: 12px;
}
.list-header .col-status {
  opacity: 0.9;
}
.list-header .col-actions {
  text-align: right;
}

/* Collection list rows (author / series grouping in list view) */
.collections-list-header {
  grid-template-columns: 64px 1fr auto;
}

.collections-list-header .col-count {
  opacity: 0.9;
  text-align: right;
}

.collection-list-item {
  grid-template-columns: 64px 1fr auto;
}

.collection-list-item .collection-count {
  font-size: 12px;
  color: #ccc;
  text-align: right;
}

.collection-have-count {
  font-weight: 600;
}

.collection-have-count.count-all-good {
  color: #2ecc71;
}

.collection-have-count.count-has-mismatch {
  color: #f39c12;
}

/* Series + narrator cell content */
.col-series-cell,
.col-narrator-cell {
  font-size: 13px;
  color: #ddd;
  display: flex;
  align-items: baseline;
  gap: 6px;
  min-width: 0;
}
.col-series-cell .series-name,
.col-narrator-cell .narrator-name {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  min-width: 0;
}
.col-series-cell .extra-count,
.col-narrator-cell .extra-count {
  font-size: 11px;
  color: #888;
  background: rgba(255, 255, 255, 0.05);
  padding: 1px 5px;
  border-radius: 8px;
  flex-shrink: 0;
}
.col-series-cell .muted,
.col-narrator-cell .muted {
  color: #555;
}

/* Hide the Series/Narrator lines in the extra-details sub-row when the
   dedicated columns are visible. The .list-narrow-only* classes are unhidden
   at the same breakpoint where the columns collapse. */
.list-narrow-only-block,
.list-narrow-only-inline {
  display: none;
}

/* Below 1100px the Series and Narrator columns collapse — surface them in
   the existing extra-details sub-row instead. */
@media (max-width: 1099px) {
  .audiobook-list-item {
    grid-template-columns: 40px 64px 1fr auto 120px;
  }
  .list-header {
    grid-template-columns: 40px 64px 1fr auto 120px;
  }
  .list-header .col-series,
  .list-header .col-narrator,
  .col-series-cell,
  .col-narrator-cell {
    display: none;
  }
  .list-narrow-only-block {
    display: block;
  }
  .list-narrow-only-inline {
    display: inline;
  }
}

/* Position badges between details and actions */
.list-badges {
  display: flex;
  gap: 8px;
  align-items: center;
  justify-self: start;
}

/* Stack badges vertically on screens 768px and below */
@media (max-width: 978px) {
  .list-badges {
    flex-direction: column;
    gap: 4px;
    align-items: flex-start;
    margin-left: 0;
    margin-top: 8px;
  }
}

/* Ensure list view text is visible (overrides poster-hover rules) */
.audiobooks-list .audiobook-title,
.audiobooks-list .audiobook-author {
  opacity: 1;
  transition: none;
  color: inherit;
}

.audiobook-wrapper {
  display: flex;
  flex-direction: column;
}
</style>
