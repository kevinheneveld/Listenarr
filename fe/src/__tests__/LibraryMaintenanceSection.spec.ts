/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'

const cacheExternalCovers = vi.fn()
const successToast = vi.fn()
const infoToast = vi.fn()
const warningToast = vi.fn()
const errorToast = vi.fn()

vi.mock('@/services/api', () => ({
  apiService: {
    cacheExternalCovers: (...args: unknown[]) => cacheExternalCovers(...args),
  },
}))

vi.mock('@/services/toastService', () => ({
  useToast: () => ({
    info: infoToast,
    success: successToast,
    warning: warningToast,
    error: errorToast,
  }),
}))

describe('LibraryMaintenanceSection', () => {
  beforeEach(() => {
    cacheExternalCovers.mockReset()
    successToast.mockReset()
    infoToast.mockReset()
    warningToast.mockReset()
    errorToast.mockReset()
  })

  it('calls the cache-external-covers endpoint and shows a success toast', async () => {
    cacheExternalCovers.mockResolvedValue({
      message: 'ok',
      totalScanned: 10,
      alreadyLocal: 6,
      queued: 4,
      succeeded: 4,
      failed: 0,
      durationMs: 1234,
    })
    const { default: LibraryMaintenanceSection } = await import(
      '@/components/settings/LibraryMaintenanceSection.vue'
    )
    const wrapper = mount(LibraryMaintenanceSection)

    await wrapper.find('button').trigger('click')
    await flushPromises()

    expect(cacheExternalCovers).toHaveBeenCalledTimes(1)
    expect(successToast).toHaveBeenCalledTimes(1)
    expect(infoToast).not.toHaveBeenCalled()
    expect(warningToast).not.toHaveBeenCalled()
    expect(errorToast).not.toHaveBeenCalled()
  })

  it('shows an info toast when nothing needed caching', async () => {
    cacheExternalCovers.mockResolvedValue({
      message: 'ok',
      totalScanned: 10,
      alreadyLocal: 10,
      queued: 0,
      succeeded: 0,
      failed: 0,
      durationMs: 5,
    })
    const { default: LibraryMaintenanceSection } = await import(
      '@/components/settings/LibraryMaintenanceSection.vue'
    )
    const wrapper = mount(LibraryMaintenanceSection)

    await wrapper.find('button').trigger('click')
    await flushPromises()

    expect(infoToast).toHaveBeenCalledTimes(1)
    expect(successToast).not.toHaveBeenCalled()
  })

  it('shows an error toast when the request rejects', async () => {
    cacheExternalCovers.mockRejectedValue(new Error('network down'))
    const { default: LibraryMaintenanceSection } = await import(
      '@/components/settings/LibraryMaintenanceSection.vue'
    )
    const wrapper = mount(LibraryMaintenanceSection)

    await wrapper.find('button').trigger('click')
    await flushPromises()

    expect(errorToast).toHaveBeenCalledTimes(1)
    expect(errorToast.mock.calls[0][1]).toBe('network down')
  })
})
