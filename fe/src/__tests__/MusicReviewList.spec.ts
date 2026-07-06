import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import MusicReviewList from '@/components/dashboard/MusicReviewList.vue'
import type { MusicCandidate } from '@/types'

const rows: MusicCandidate[] = [
  {
    id: 11,
    title: 'Sticky Fingers',
    score: 1,
    reasons: ['album shape: 12 tracks, median 3.1 min, longest 4.5 min'],
    fileCount: 12,
    medianDurationSeconds: 186,
  },
  {
    id: 22,
    title: 'Some Other Album',
    score: 0.75,
    reasons: ['transcript dominated by music cues (4 cue(s))'],
    fileCount: 9,
    medianDurationSeconds: 210,
  },
]

const routerLinkStub = { template: '<a><slot /></a>' }

describe('MusicReviewList', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders one row per candidate with evidence and a sweep-all button', () => {
    const wrapper = mount(MusicReviewList, {
      props: { rows },
      global: { stubs: { RouterLink: routerLinkStub } },
    })

    const rowEls = wrapper.findAll('.music-row')
    expect(rowEls).toHaveLength(2)
    expect(rowEls[0].text()).toContain('Sticky Fingers')
    expect(rowEls[0].text()).toContain('100%')
    expect(rowEls[0].text()).toContain('12 files')
    expect(rowEls[0].text()).toContain('album shape')
    expect(wrapper.find('.sweep-all-btn').text()).toContain('Sweep all (2)')
  })

  it('per-row sweep asks for confirmation and calls the not-audiobook endpoint', async () => {
    const confirmModule = await import('@/composables/useConfirm')
    const confirmSpy = vi.spyOn(confirmModule, 'showConfirm').mockResolvedValue(true)
    const api = (await import('@/services/api')).apiService

    const wrapper = mount(MusicReviewList, {
      props: { rows },
      global: { stubs: { RouterLink: routerLinkStub } },
    })

    await wrapper.findAll('.sweep-btn')[0].trigger('click')
    await flushPromises()

    expect(confirmSpy).toHaveBeenCalledTimes(1)
    expect(api.rejectNotAudiobook).toHaveBeenCalledTimes(1)
    expect(api.rejectNotAudiobook).toHaveBeenCalledWith(11)
    expect(wrapper.emitted('swept')?.[0]).toEqual([[11]])
  })

  it('a declined confirm sweeps nothing', async () => {
    const confirmModule = await import('@/composables/useConfirm')
    vi.spyOn(confirmModule, 'showConfirm').mockResolvedValue(false)
    const api = (await import('@/services/api')).apiService

    const wrapper = mount(MusicReviewList, {
      props: { rows },
      global: { stubs: { RouterLink: routerLinkStub } },
    })

    await wrapper.find('.sweep-all-btn').trigger('click')
    await flushPromises()

    expect(api.rejectNotAudiobook).not.toHaveBeenCalled()
    expect(wrapper.emitted('swept')).toBeUndefined()
  })

  it('sweep-all runs sequentially over every row and reports partial failures', async () => {
    const confirmModule = await import('@/composables/useConfirm')
    vi.spyOn(confirmModule, 'showConfirm').mockResolvedValue(true)
    const api = (await import('@/services/api')).apiService
    ;(api.rejectNotAudiobook as ReturnType<typeof vi.fn>)
      .mockResolvedValueOnce({
        message: '',
        id: 11,
        filesRemoved: 12,
        searchStarted: true,
        warnings: [],
      })
      .mockRejectedValueOnce(new Error('boom'))

    const wrapper = mount(MusicReviewList, {
      props: { rows },
      global: { stubs: { RouterLink: routerLinkStub } },
    })

    await wrapper.find('.sweep-all-btn').trigger('click')
    await flushPromises()

    expect(api.rejectNotAudiobook).toHaveBeenCalledTimes(2)
    // Only the successful id is reported swept; the failed one stays listed.
    expect(wrapper.emitted('swept')?.[0]).toEqual([[11]])
  })
})
