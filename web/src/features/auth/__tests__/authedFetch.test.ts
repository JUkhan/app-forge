import { afterEach, describe, expect, it, vi } from 'vitest'
import { authedFetch } from '../authedFetch'
import { enterAnonymousPage } from '../anonymousPage'
import * as guestSession from '../guestSession'
import { tokenStore } from '../tokenStore'

afterEach(() => {
  vi.restoreAllMocks()
  tokenStore.clear()
  guestSession.clearGuestSession()
})

describe('authedFetch', () => {
  it('sends only the guest token on an anonymous public page and retries once on 401', async () => {
    tokenStore.set('real-session-token')
    const release = enterAnonymousPage()
    vi.spyOn(guestSession, 'getGuestToken').mockReturnValue('guest-1')
    vi.spyOn(guestSession, 'refreshGuestToken').mockResolvedValue('guest-2')
    const send = vi
      .fn<(t: string) => Promise<Response>>()
      .mockResolvedValueOnce(new Response(null, { status: 401 }))
      .mockResolvedValueOnce(new Response('ok', { status: 200 }))

    const res = await authedFetch(send)

    expect(res.status).toBe(200)
    expect(send.mock.calls.map((c) => c[0])).toEqual(['guest-1', 'guest-2'])
    release()
  })

  it('uses the session token outside public pages', async () => {
    tokenStore.set('real-session-token')
    const send = vi.fn<(t: string) => Promise<Response>>().mockResolvedValue(new Response('ok'))
    await authedFetch(send)
    expect(send).toHaveBeenCalledWith('real-session-token')
  })
})
