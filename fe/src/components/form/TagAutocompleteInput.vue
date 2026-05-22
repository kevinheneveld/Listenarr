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
  <div class="tag-autocomplete">
    <div class="tag-input-group">
      <input
        :id="inputId"
        ref="inputEl"
        v-model="typed"
        type="text"
        class="tag-input"
        :placeholder="placeholder"
        autocomplete="off"
        spellcheck="false"
        role="combobox"
        :aria-expanded="dropdownOpen"
        :aria-controls="listboxId"
        :aria-activedescendant="activeOptionId"
        aria-autocomplete="list"
        @input="onInput"
        @focus="onFocus"
        @blur="onBlur"
        @keydown.down.prevent="cursorDown"
        @keydown.up.prevent="cursorUp"
        @keydown.enter.prevent="onEnter"
        @keydown.escape="closeDropdown"
      />
      <button
        type="button"
        class="icon-btn btn-primary btn-add-tag"
        :disabled="!typed.trim()"
        :title="addButtonTitle"
        :aria-label="addButtonTitle"
        @click="commitTyped"
      >
        <PhPlus :size="16" />
      </button>
    </div>
    <ul
      v-show="dropdownOpen && filteredSuggestions.length > 0"
      :id="listboxId"
      class="tag-autocomplete-list"
      role="listbox"
    >
      <li
        v-for="(suggestion, index) in filteredSuggestions"
        :id="`${listboxId}-opt-${index}`"
        :key="suggestion"
        class="tag-autocomplete-option"
        :class="{ active: index === cursorIndex }"
        role="option"
        :aria-selected="index === cursorIndex"
        @mousedown.prevent="commitSuggestion(suggestion)"
        @mouseenter="cursorIndex = index"
      >
        {{ suggestion }}
      </li>
    </ul>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, useId, watch } from 'vue'
import { PhPlus } from '@phosphor-icons/vue'

const props = withDefaults(
  defineProps<{
    /** Full list of unique values for this field across the library. */
    suggestions: string[]
    /** Values the user has already added — filtered out of the dropdown so they
     *  don't appear as candidates again. */
    excluded?: string[]
    placeholder?: string
    inputId?: string
    /** Tooltip + aria-label on the plus button. Defaults to "Add". */
    addButtonTitle?: string
    /** How many top matches to show. Caps the dropdown height. */
    maxResults?: number
  }>(),
  {
    excluded: () => [],
    placeholder: '',
    inputId: undefined,
    addButtonTitle: 'Add',
    maxResults: 8,
  },
)

const emit = defineEmits<{
  /** Fired when the user commits a value, either by clicking +, hitting Enter,
   *  or picking a suggestion. The parent decides whether to accept it (the
   *  parent's add* handler already dedupes against the current list). */
  (e: 'add', value: string): void
}>()

const typed = ref('')
const dropdownOpen = ref(false)
const cursorIndex = ref(-1)
const inputEl = ref<HTMLInputElement | null>(null)
const listboxId = `tag-autocomplete-${useId?.() ?? Math.random().toString(36).slice(2)}`

const activeOptionId = computed(() =>
  cursorIndex.value >= 0 ? `${listboxId}-opt-${cursorIndex.value}` : undefined,
)

const filteredSuggestions = computed<string[]>(() => {
  const needle = normalize(typed.value)
  const typedRaw = typed.value.trim()
  const excludedSet = new Set(props.excluded.map(normalize))
  const results: string[] = []
  for (const candidate of props.suggestions) {
    if (!candidate) continue
    const normalized = normalize(candidate)
    if (!normalized) continue
    if (excludedSet.has(normalized)) continue
    // When the user has typed something, require it to appear in the
    // normalized candidate. When the input is empty (just focused), show the
    // top suggestions unfiltered so the user can browse.
    if (needle && !normalized.includes(needle)) continue
    // Hide the literal exact match — no point suggesting what the user has
    // fully typed. Comparison is on the raw trimmed values (not normalized)
    // so a punctuation-only mismatch like "Obrien" vs "O'Brien" still surfaces
    // as a candidate — that's the spelling-correction use case.
    if (typedRaw && candidate.trim() === typedRaw) continue
    results.push(candidate)
    if (results.length >= props.maxResults) break
  }
  return results
})

watch(filteredSuggestions, () => {
  // Reset cursor when the visible list changes so it doesn't dangle on a
  // now-invisible row. -1 means "no row highlighted; Enter commits typed text".
  cursorIndex.value = -1
})

function onInput() {
  dropdownOpen.value = true
}

function onFocus() {
  dropdownOpen.value = true
}

function onBlur() {
  // Defer so a mousedown on a suggestion still resolves before the dropdown
  // hides. @mousedown.prevent on the option also helps by preventing the blur
  // event from firing on the option click.
  setTimeout(() => {
    dropdownOpen.value = false
    cursorIndex.value = -1
  }, 120)
}

function closeDropdown() {
  dropdownOpen.value = false
  cursorIndex.value = -1
}

function cursorDown() {
  if (!dropdownOpen.value) {
    dropdownOpen.value = true
    return
  }
  if (filteredSuggestions.value.length === 0) return
  cursorIndex.value = (cursorIndex.value + 1) % filteredSuggestions.value.length
}

function cursorUp() {
  if (!dropdownOpen.value) return
  if (filteredSuggestions.value.length === 0) return
  cursorIndex.value =
    cursorIndex.value <= 0
      ? filteredSuggestions.value.length - 1
      : cursorIndex.value - 1
}

function onEnter() {
  if (cursorIndex.value >= 0 && filteredSuggestions.value[cursorIndex.value]) {
    commitSuggestion(filteredSuggestions.value[cursorIndex.value])
    return
  }
  commitTyped()
}

function commitTyped() {
  const value = typed.value.trim()
  if (!value) return
  emit('add', value)
  typed.value = ''
  closeDropdown()
  void nextTick(() => inputEl.value?.focus())
}

function commitSuggestion(value: string) {
  emit('add', value)
  typed.value = ''
  closeDropdown()
  void nextTick(() => inputEl.value?.focus())
}

function normalize(value: string): string {
  return value
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[^\p{Letter}\p{Number}\s]/gu, '')
    .replace(/\s+/g, ' ')
    .trim()
}
</script>

<style scoped>
.tag-autocomplete {
  position: relative;
  flex: 1;
}

.tag-input-group {
  display: flex;
  gap: 0.4rem;
}

.tag-input {
  flex: 1;
}

.tag-autocomplete-list {
  position: absolute;
  top: 100%;
  left: 0;
  right: 0;
  margin-top: 0.25rem;
  padding: 0.25rem 0;
  list-style: none;
  background: var(--color-surface, #1f2229);
  border: 1px solid var(--border-color, #333);
  border-radius: 6px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.35);
  z-index: 50;
  max-height: 14rem;
  overflow-y: auto;
}

.tag-autocomplete-option {
  padding: 0.4rem 0.75rem;
  cursor: pointer;
  font-size: 14px;
  color: var(--text-primary, #fff);
  user-select: none;
}

.tag-autocomplete-option:hover,
.tag-autocomplete-option.active {
  background: rgba(255, 255, 255, 0.06);
}
</style>
