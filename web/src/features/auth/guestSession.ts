// In-memory guest session for the anonymous /{tenant}/public shell.
//
// Deliberately SEPARATE from tokenStore / sessionCache / the refresh cookie: a guest token is a
// short-lived, allowlist-scoped credential that must never be mistaken for (or replace) a real
// user session. It lives only in this module's memory, is never persisted, and httpClient reads it
// only while an anonymous public page is mounted (see anonymousPage.ts).

let guestToken: string | null = null
let guestTenant: string | null = null
let inflight: Promise<string | null> | null = null

export function getGuestToken(): string | null {
  return guestToken
}

export function setGuestTenant(tenant: string | null): void {
  if (guestTenant !== tenant) {
    guestToken = null
    inflight = null
  }
  guestTenant = tenant
}

export function clearGuestSession(): void {
  guestToken = null
  guestTenant = null
  inflight = null
}

interface GuestSessionResponse {
  accessToken: string
  expiresIn: number
}

// Requests a fresh guest token for the registered tenant. Plain fetch (NOT httpClient) so it can
// never recurse into the 401 handling, and with no credentials/cookies. Concurrent callers share
// one request. Resolves null (never throws) when the session cannot be obtained.
export function refreshGuestToken(): Promise<string | null> {
  const tenant = guestTenant
  if (!tenant) return Promise.resolve(null)
  if (inflight) return inflight

  const run = (async () => {
    try {
      const response = await fetch(`/api/public/${encodeURIComponent(tenant)}/session`, {
        method: 'POST',
        credentials: 'omit',
      })
      if (!response.ok) return null
      const body = (await response.json()) as GuestSessionResponse
      if (guestTenant !== tenant || typeof body.accessToken !== 'string') return null
      guestToken = body.accessToken
      return guestToken
    } catch {
      return null
    }
  })()

  const shared = run.finally(() => {
    inflight = null
  })
  inflight = shared
  return shared
}
