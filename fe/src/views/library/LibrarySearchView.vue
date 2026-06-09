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
  Unified library search results — the single page that lists everything the user
  owns matching a query, grouped by Books / Series / Authors / Narrators. Distinct
  from content/SearchView.vue, which searches external metadata to add new books.
-->
<template>
  <div class="library-search-view">
    <header class="search-header">
      <h1><PhMagnifyingGlass /> Search results</h1>
      <p v-if="query" class="search-subtitle">
        for <strong>"{{ query }}"</strong>
      </p>
    </header>

    <div v-if="!query" class="search-state">Type a query to search your library.</div>

    <div v-else-if="libraryStore.loading && libraryStore.audiobooks.length === 0" class="search-state">
      Loading your library…
    </div>

    <div v-else-if="totalResults === 0" class="search-state">
      No matches in your library for "{{ query }}".
    </div>

    <template v-else>
      <!-- Books -->
      <section v-if="results.books.length" class="result-section">
        <h2 class="result-section-title">Books <span class="section-count">{{ results.books.length }}</span></h2>
        <div class="card-grid">
          <RouterLink
            v-for="book in results.books"
            :key="`book-${book.id}`"
            class="result-card"
            :to="{ name: 'audiobook-detail', params: { id: String(book.id) } }"
          >
            <img
              class="card-cover"
              :src="getProtectedImageSrc(book.imageUrl, getPlaceholderUrl())"
              :alt="book.title"
              loading="lazy"
              decoding="async"
              @error="handleImageError"
            />
            <div class="card-label" :title="book.title">{{ book.title }}</div>
            <div v-if="book.author" class="card-sub" :title="book.author">{{ book.author }}</div>
          </RouterLink>
        </div>
      </section>

      <!-- Series / Authors / Narrators -->
      <section v-for="group in facetSections" :key="group.kind" class="result-section">
        <h2 class="result-section-title">
          {{ group.label }} <span class="section-count">{{ group.items.length }}</span>
        </h2>
        <div class="card-grid">
          <RouterLink
            v-for="facet in group.items"
            :key="`${group.kind}-${facet.normKey}`"
            class="result-card"
            :to="`/collection/${group.kind}/${encodeURIComponent(facet.name)}`"
          >
            <img
              class="card-cover"
              :src="getProtectedImageSrc(facet.imageUrl || '', getPlaceholderUrl())"
              :alt="facet.name"
              loading="lazy"
              decoding="async"
              @error="handleImageError"
            />
            <div class="card-label" :title="facet.name">{{ facet.name }}</div>
            <div class="card-sub">{{ facet.count }} book{{ facet.count !== 1 ? 's' : '' }}</div>
          </RouterLink>
        </div>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { RouterLink } from 'vue-router'
import { PhMagnifyingGlass } from '@phosphor-icons/vue'
import { useLibraryStore } from '@/stores/library'
import { useProtectedImages } from '@/composables/useProtectedImages'
import { getPlaceholderUrl } from '@/utils/placeholder'
import { handleImageError } from '@/utils/imageFallback'
import {
  buildLibraryFacets,
  matchLibraryBooks,
  matchLibraryFacets,
  type LibraryFacetMatch,
} from '@/utils/librarySearch'

const route = useRoute()
const libraryStore = useLibraryStore()
const { getProtectedImageSrc } = useProtectedImages()

const query = computed(() => {
  const q = route.query.q
  return (typeof q === 'string' ? q : Array.isArray(q) ? q[0] || '' : '').trim()
})

const results = computed(() => {
  const q = query.value
  if (!q) {
    return {
      books: [] as ReturnType<typeof matchLibraryBooks>,
      series: [] as LibraryFacetMatch[],
      authors: [] as LibraryFacetMatch[],
      narrators: [] as LibraryFacetMatch[],
    }
  }
  const books = libraryStore.audiobooks
  const facets = buildLibraryFacets(books)
  return {
    books: matchLibraryBooks(books, q),
    series: matchLibraryFacets(facets.series, q),
    authors: matchLibraryFacets(facets.authors, q),
    narrators: matchLibraryFacets(facets.narrators, q),
  }
})

const facetSections = computed(() =>
  [
    { kind: 'series' as const, label: 'Series', items: results.value.series },
    { kind: 'author' as const, label: 'Authors', items: results.value.authors },
    { kind: 'narrator' as const, label: 'Narrators', items: results.value.narrators },
  ].filter((section) => section.items.length > 0),
)

const totalResults = computed(
  () =>
    results.value.books.length +
    results.value.series.length +
    results.value.authors.length +
    results.value.narrators.length,
)

onMounted(() => {
  if (libraryStore.audiobooks.length === 0) {
    void libraryStore.fetchLibrary()
  }
})
</script>

<style scoped>
.library-search-view {
  padding: 24px;
  max-width: 1400px;
  margin: 0 auto;
}

.search-header {
  margin-bottom: 24px;
}

.search-header h1 {
  display: flex;
  align-items: center;
  gap: 10px;
  font-size: 1.6rem;
  margin: 0;
}

.search-subtitle {
  margin: 6px 0 0;
  color: var(--text-secondary, #bfc8cf);
}

.search-state {
  padding: 48px 12px;
  text-align: center;
  color: var(--text-secondary, #9aa0a6);
}

.result-section {
  margin-bottom: 32px;
}

.result-section-title {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 1.05rem;
  margin: 0 0 12px;
}

.section-count {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--text-secondary, #8a929a);
  background: rgba(255, 255, 255, 0.06);
  border-radius: 10px;
  padding: 1px 8px;
}

.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
  gap: 16px;
}

.result-card {
  display: flex;
  flex-direction: column;
  text-decoration: none;
  color: inherit;
  border-radius: 8px;
  transition: transform 120ms ease;
}

.result-card:hover {
  transform: translateY(-2px);
}

.card-cover {
  width: 100%;
  aspect-ratio: 1 / 1;
  object-fit: cover;
  border-radius: 8px;
  background: #2a2a2a;
}

.card-label {
  margin-top: 8px;
  font-weight: 500;
  font-size: 0.9rem;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.card-sub {
  font-size: 0.8rem;
  color: var(--text-secondary, #bfc8cf);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
