import { tokenStore } from './tokenStore'
import { refreshSession } from './refreshCoordinator'
import { isAnonymousPageActive } from './anonymousPage'
import { getGuestToken, refreshGuestToken } from './guestSession'

// Bearer-token fetch for raw (non-JSON) calls such as file exports, which cannot go through
// httpClient. Mirrors httpClient's credential rules: on an anonymous /{tenant}/public page only
// the allowlist-scoped guest token is ever sent (re-fetched once on 401, never a redirect to
// /login); everywhere else the normal session token with one silent refresh + retry, and a hard
// redirect to /login when the session is truly gone.
export async function authedFetch(send: (token: string) => Promise<Response>): Promise<Response> {
  if (isAnonymousPageActive()) {
    let response = await send(getGuestToken() ?? '')
    if (response.status === 401) {
      const fresh = await refreshGuestToken()
      if (fresh) response = await send(fresh)
    }
    return response
  }

  let response = await send(tokenStore.get() ?? '')
  if (response.status === 401) {
    const refreshed = await refreshSession()
    if (refreshed) {
      tokenStore.set(refreshed.accessToken)
      response = await send(refreshed.accessToken)
    } else {
      tokenStore.clear()
      window.location.replace('/login')
    }
  }
  return response
}
