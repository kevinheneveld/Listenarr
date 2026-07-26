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
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import AudiobookFileUploadZone from '@/components/library/AudiobookFileUploadZone.vue'
import { apiService } from '@/services/api'

vi.mock('@/services/api', () => ({
  apiService: {
    uploadAudiobookFile: vi.fn(async () => ({
      message: 'Uploaded 1 file(s)',
      uploaded: 1,
      skipped: [],
    })),
  },
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
    warning: vi.fn(),
  }),
}))

function makeDropEvent(files: File[]): DragEvent {
  return {
    preventDefault: vi.fn(),
    dataTransfer: { files },
  } as unknown as DragEvent
}

describe('AudiobookFileUploadZone', () => {
  beforeEach(() => {
    vi.mocked(apiService.uploadAudiobookFile).mockClear()
  })

  it('uploads dropped audio files and emits uploaded', async () => {
    const wrapper = mount(AudiobookFileUploadZone, {
      props: { audiobookId: 42 },
    })

    const file = new File(['audio'], 'chapter1.mp3', { type: 'audio/mpeg' })
    await wrapper.find('.upload-zone').trigger('drop', makeDropEvent([file]))
    await flushPromises()

    expect(apiService.uploadAudiobookFile).toHaveBeenCalledTimes(1)
    expect(vi.mocked(apiService.uploadAudiobookFile).mock.calls[0][0]).toBe(42)
    expect(vi.mocked(apiService.uploadAudiobookFile).mock.calls[0][1]).toBe(file)
    expect(wrapper.emitted('uploaded')).toBeTruthy()
    expect(wrapper.emitted('uploaded')![0]).toEqual([1])
  })

  it('does not call the API for non-audio files', async () => {
    const wrapper = mount(AudiobookFileUploadZone, {
      props: { audiobookId: 42 },
    })

    const file = new File(['text'], 'notes.txt', { type: 'text/plain' })
    await wrapper.find('.upload-zone').trigger('drop', makeDropEvent([file]))
    await flushPromises()

    expect(apiService.uploadAudiobookFile).not.toHaveBeenCalled()
    expect(wrapper.emitted('uploaded')).toBeFalsy()
  })

  it('marks a failed upload as an error without emitting uploaded', async () => {
    vi.mocked(apiService.uploadAudiobookFile).mockRejectedValueOnce(new Error('Upload failed (500)'))
    const wrapper = mount(AudiobookFileUploadZone, {
      props: { audiobookId: 42 },
    })

    const file = new File(['audio'], 'chapter1.m4b', { type: 'audio/mp4' })
    await wrapper.find('.upload-zone').trigger('drop', makeDropEvent([file]))
    await flushPromises()

    expect(wrapper.emitted('uploaded')).toBeFalsy()
    expect(wrapper.text()).toContain('Upload failed (500)')
  })

  it('accepts zip archives', async () => {
    const wrapper = mount(AudiobookFileUploadZone, {
      props: { audiobookId: 7 },
    })

    const file = new File(['zipbytes'], 'libro-fm-download.zip', { type: 'application/zip' })
    await wrapper.find('.upload-zone').trigger('drop', makeDropEvent([file]))
    await flushPromises()

    expect(apiService.uploadAudiobookFile).toHaveBeenCalledTimes(1)
    expect(wrapper.emitted('uploaded')).toBeTruthy()
  })
})
