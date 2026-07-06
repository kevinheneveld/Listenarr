/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import RenameFileModal from '@/components/domain/organize/RenameFileModal.vue'
import { apiService } from '@/services/api'
import type { RenamePreview } from '@/types'

const CURRENT_PATH = 'D:\\test\\Author\\Alchemised\\Alchemised.m4b'

function previewWithFile(): RenamePreview {
  return {
    audiobookId: 7,
    audiobookTitle: 'Alchemised',
    currentFolderPath: 'D:\\test\\Author\\Alchemised',
    newFolderPath: 'D:\\test\\Author\\Alchemised',
    folderChanged: false,
    hasChanges: false,
    fileRenames: [
      {
        fileId: 71,
        currentPath: CURRENT_PATH,
        newPath: CURRENT_PATH,
        currentFilename: 'Alchemised.m4b',
        newFilename: 'Alchemised.m4b',
        changed: false,
      },
    ],
  }
}

describe('RenameFileModal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(apiService.previewRenameAudiobook).mockResolvedValue(previewWithFile())
  })

  it('hydrates the input with the current filename stem and previews the new full path', async () => {
    const wrapper = mount(RenameFileModal, {
      props: { visible: true, audiobookId: 7, file: { id: 71 } },
    })
    await flushPromises()

    expect(apiService.previewRenameAudiobook).toHaveBeenCalledWith(7)
    const input = wrapper.get<HTMLInputElement>('#rename-file-input')
    expect(input.element.value).toBe('Alchemised')

    await input.setValue('Alchemised (Unabridged)')
    expect(wrapper.find('.rename-path-value--new').text()).toBe(
      'D:\\test\\Author\\Alchemised\\Alchemised (Unabridged).m4b',
    )
  })

  it('sends the preview-derived currentPath and a swapped filename, then emits done on success', async () => {
    vi.mocked(apiService.executeRenameAudiobook).mockResolvedValue({
      audiobookId: 7,
      success: true,
      renamedFiles: [
        {
          fileId: 71,
          previousPath: CURRENT_PATH,
          newPath: 'D:\\test\\Author\\Alchemised\\Alchemised (Unabridged).m4b',
          success: true,
        },
      ],
    })

    const wrapper = mount(RenameFileModal, {
      props: { visible: true, audiobookId: 7, file: { id: 71 } },
    })
    await flushPromises()

    const renameButton = wrapper.find('.btn.btn-primary')
    expect(renameButton.attributes('disabled')).toBeDefined()

    await wrapper.find('#rename-file-input').setValue('Alchemised (Unabridged)')
    expect(renameButton.attributes('disabled')).toBeUndefined()

    await renameButton.trigger('click')
    await flushPromises()

    expect(apiService.executeRenameAudiobook).toHaveBeenCalledWith(7, {
      audiobookId: 7,
      fileRenames: [
        {
          fileId: 71,
          currentPath: CURRENT_PATH,
          newPath: 'D:\\test\\Author\\Alchemised\\Alchemised (Unabridged).m4b',
        },
      ],
    })
    expect(wrapper.emitted('done')).toBeTruthy()
  })

  it('does not double-append the extension when the user types it explicitly', async () => {
    const wrapper = mount(RenameFileModal, {
      props: { visible: true, audiobookId: 7, file: { id: 71 } },
    })
    await flushPromises()

    await wrapper.find('#rename-file-input').setValue('Alchemised v2.m4b')
    expect(wrapper.find('.rename-path-value--new').text()).toBe(
      'D:\\test\\Author\\Alchemised\\Alchemised v2.m4b',
    )
  })

  it('flags path separators in the filename as invalid and keeps Rename disabled', async () => {
    const wrapper = mount(RenameFileModal, {
      props: { visible: true, audiobookId: 7, file: { id: 71 } },
    })
    await flushPromises()

    await wrapper.find('#rename-file-input').setValue('foo/bar')

    expect(wrapper.find('.rename-file-error').text()).toContain('path separators')
    expect(wrapper.find('.btn.btn-primary').attributes('disabled')).toBeDefined()
    expect(apiService.executeRenameAudiobook).not.toHaveBeenCalled()
  })

  it('surfaces the per-file error message when the backend reports failure', async () => {
    vi.mocked(apiService.executeRenameAudiobook).mockResolvedValue({
      audiobookId: 7,
      success: false,
      error: 'One or more file organize operations failed.',
      renamedFiles: [
        {
          fileId: 71,
          previousPath: CURRENT_PATH,
          newPath: 'D:\\test\\Author\\Alchemised\\Renamed.m4b',
          success: false,
          error: 'Target file already exists.',
        },
      ],
    })

    const wrapper = mount(RenameFileModal, {
      props: { visible: true, audiobookId: 7, file: { id: 71 } },
    })
    await flushPromises()

    await wrapper.find('#rename-file-input').setValue('Renamed')
    await wrapper.find('.btn.btn-primary').trigger('click')
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('Target file already exists.')
    expect(wrapper.emitted('done')).toBeFalsy()
  })
})
