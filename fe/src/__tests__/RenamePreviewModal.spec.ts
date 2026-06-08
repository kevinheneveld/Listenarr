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
import RenamePreviewModal from '@/components/domain/organize/RenamePreviewModal.vue'
import { apiService } from '@/services/api'
import type { RenamePreview, RenameResult } from '@/types'

describe('RenamePreviewModal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders a cleaner before-and-after preview for organize changes', async () => {
    vi.mocked(apiService.previewRename).mockResolvedValue([
      {
        audiobookId: 7,
        audiobookTitle: 'Alchemised',
        currentFolderPath: 'D:\\test\\Author\\Alchemised',
        newFolderPath: 'D:\\test\\Author\\Alchemised test',
        folderChanged: true,
        hasChanges: true,
        fileRenames: [
          {
            fileId: 71,
            currentPath: 'D:\\test\\Author\\Alchemised\\Alchemised.m4b',
            newPath: 'D:\\test\\Author\\Alchemised test\\Alchemised test.m4b',
            currentFilename: 'Alchemised.m4b',
            newFilename: 'Alchemised test.m4b',
            changed: true,
          },
        ],
      },
    ] satisfies RenamePreview[])

    const wrapper = mount(RenamePreviewModal, {
      props: {
        visible: true,
        audiobookIds: [7],
      },
    })

    await flushPromises()

    expect(apiService.previewRename).toHaveBeenCalledWith([7])
    expect(wrapper.text()).toContain('1 of 1 audiobook(s) selected')
    expect(wrapper.text()).toContain(
      'Review the proposed folder and filename changes before organizing files on disk.',
    )
    expect(wrapper.text()).toContain('Folder + 1 file')
    expect(wrapper.findAll('.rename-path-value--current')).toHaveLength(2)
    expect(wrapper.findAll('.rename-path-value--new')).toHaveLength(2)
    expect(wrapper.text()).toContain('Current')
    expect(wrapper.text()).toContain('New')
    expect(wrapper.find('.btn.btn-primary').text()).toContain('Organize 1')
  })

  it('surfaces a conflict resolver when the destination already exists', async () => {
    vi.mocked(apiService.previewRename).mockResolvedValue([
      {
        audiobookId: 2693,
        audiobookTitle: 'The Lost (mis-identified)',
        currentFolderPath: '/audiobooks/Wrong',
        newFolderPath: '/audiobooks/Correct',
        folderChanged: true,
        hasChanges: true,
        fileRenames: [
          {
            fileId: 100,
            currentPath: '/audiobooks/Wrong/The Lost-001.mp3',
            newPath: '/audiobooks/Correct/The Lost-001.mp3',
            currentFilename: 'The Lost-001.mp3',
            newFilename: 'The Lost-001.mp3',
            changed: true,
          },
        ],
      },
    ] satisfies RenamePreview[])

    vi.mocked(apiService.executeRename).mockResolvedValue([
      {
        audiobookId: 2693,
        success: false,
        error: 'One or more file organize operations failed.',
        renamedFiles: [
          {
            fileId: 100,
            previousPath: '/audiobooks/Wrong/The Lost-001.mp3',
            newPath: '/audiobooks/Correct/The Lost-001.mp3',
            success: false,
            error: 'Target file already exists.',
            isConflict: true,
            conflict: {
              incoming: { path: '/audiobooks/Wrong/The Lost-001.mp3', size: 1000, format: 'mp3' },
              existing: { path: '/audiobooks/Correct/The Lost-001.mp3', size: 1000, format: 'mp3' },
              existingTracked: true,
              existingAudiobookId: 2720,
              existingAudiobookTitle: 'The Lost (correct)',
              existingFileId: 200,
            },
          },
        ],
      },
    ] satisfies RenameResult[])

    const removeFromLibrary = vi.fn().mockResolvedValue({ message: 'ok', id: 2693 })
    ;(apiService as unknown as { removeFromLibrary: typeof removeFromLibrary }).removeFromLibrary =
      removeFromLibrary

    const wrapper = mount(RenamePreviewModal, {
      props: { visible: true, audiobookIds: [2693] },
    })
    await flushPromises()

    // Kick off the organize that collides.
    await wrapper.find('.btn.btn-primary').trigger('click')
    await flushPromises()

    // Conflict resolver is shown with both sides + open-in-new-tab links.
    expect(wrapper.text()).toContain('Resolve conflicts')
    expect(wrapper.text()).toContain('The Lost (correct)')
    const links = wrapper.findAll('.conflict-open').map((a) => a.attributes('href'))
    expect(links).toContain('/audiobooks/2693')
    expect(links).toContain('/audiobooks/2720')

    // Delete the duplicate (the incoming, mis-identified book) and confirm.
    const deleteBtn = wrapper
      .findAll('.conflict-btn')
      .find((b) => b.text().includes('Delete duplicate'))
    expect(deleteBtn).toBeTruthy()
    await deleteBtn!.trigger('click')
    await wrapper.find('.conflict-confirm .btn-danger').trigger('click')
    await flushPromises()

    expect(removeFromLibrary).toHaveBeenCalledWith(2693, {
      deleteFiles: true,
      deleteFolder: true,
    })
    expect(wrapper.text()).toContain('Deleted the duplicate book')
  })
})
