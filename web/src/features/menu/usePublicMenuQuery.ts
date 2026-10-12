import { useQuery } from '@tanstack/react-query'
import { httpClient } from '../auth/httpClient'
import type { PublicMenuItem } from './types'

// Anonymous tenant public-menu tree: GET /api/public/{tenant}/menus. Shared by the public shell
// (sidebar) and the index route so both read one cache entry.
export function publicMenuQueryKey(tenant: string) {
  return ['public-menu', tenant] as const
}

export function usePublicMenuQuery(tenant: string) {
  return useQuery({
    queryKey: publicMenuQueryKey(tenant),
    queryFn: () =>
      httpClient.get<PublicMenuItem[]>(`/api/public/${encodeURIComponent(tenant)}/menus`),
    retry: false,
    refetchOnWindowFocus: false,
  })
}

// First designer-bound item in display order (depth-first), used to land `/{tenant}/public`
// on real content.
export function firstBoundItem(items: PublicMenuItem[]): PublicMenuItem | null {
  for (const item of items) {
    if (item.designerId) return item
    const nested = firstBoundItem(item.children)
    if (nested) return nested
  }
  return null
}
