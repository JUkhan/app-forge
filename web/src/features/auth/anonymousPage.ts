// Marker for anonymous pages (the /{tenant}/public shell). While one is mounted, httpClient must
// not treat a 401 as an expired session (no silent refresh, no hard redirect to /login): a widget
// that needs an authenticated API just surfaces an ordinary error. A mount counter (not a
// boolean) keeps this correct if shells overlap during a route transition.
let mounted = 0

export function enterAnonymousPage(): () => void {
  mounted += 1
  let released = false
  return () => {
    if (released) return
    released = true
    mounted = Math.max(0, mounted - 1)
  }
}

export function isAnonymousPageActive(): boolean {
  return mounted > 0
}
