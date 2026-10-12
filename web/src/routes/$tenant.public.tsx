import { useEffect } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Loader2 } from 'lucide-react'
import { createFileRoute, Link, Outlet } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { LogIn } from 'lucide-react'
import { NavSidebar, type NavItemLinkProps } from '../components/shared/NavSidebar'
import { usePublicMenuQuery } from '../features/menu/usePublicMenuQuery'
import { enterAnonymousPage } from '../features/auth/anonymousPage'
import { clearGuestSession, refreshGuestToken, setGuestTenant } from '../features/auth/guestSession'
import { useTheme } from '../lib/theme/ThemeProvider'
import { THEMES, THEME_LABELS, type Theme } from '../lib/theme/themes'
import { Button } from '../components/ui/button'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '../components/ui/select'
import type { PublicMenuItem } from '../features/menu/types'

// Anonymous layout route (sibling of login.tsx, outside _app — no beforeLoad guard). It is the
// public analogue of the authenticated app shell (_app.tsx + Navbar): a left sidebar / mobile
// drawer showing the tenant's public menu tree from GET /api/public/{tenant}/menus, a header
// with the theme selector, and a content area (<Outlet/>) on the right, so the sidebar persists
// while switching items. `/{tenant}/public/{menuId}` renders the selected item.
// eslint-disable-next-line react-refresh/only-export-components
export const Route = createFileRoute('/$tenant/public')({
  component: PublicShell,
})

// Designer-bound items open in this shell; internal routePaths link as in the authenticated
// navbar (external http(s) ones are handled by NavSidebar); section headers are not links.
function renderPublicItemLink(tenant: string) {
  return function renderLink(item: PublicMenuItem, p: NavItemLinkProps) {
    if (item.routePath) {
      return (
        <Link
          to={item.routePath as never}
          onClick={p.onClick}
          className={p.baseClassName}
          activeProps={{ className: p.activeClassName }}
          inactiveProps={{ className: p.inactiveClassName }}
        >
          {p.children}
        </Link>
      )
    }
    if (item.designerId) {
      return (
        <Link
          to="/$tenant/public/$menuId"
          params={{ tenant, menuId: item.id }}
          onClick={p.onClick}
          className={p.baseClassName}
          activeProps={{ className: p.activeClassName }}
          inactiveProps={{ className: p.inactiveClassName }}
        >
          {p.children}
        </Link>
      )
    }
    return null
  }
}

export function PublicShell() {
  const { t } = useTranslation()
  const { tenant } = Route.useParams()
  const { theme, setTheme } = useTheme()
  const { data, isPending, isError } = usePublicMenuQuery(tenant)

  // While mounted: httpClient uses the separate guest token (never the real session) and a 401
  // never refreshes the user session or redirects to /login.
  useEffect(() => {
    const leave = enterAnonymousPage()
    setGuestTenant(tenant)
    return () => {
      leave()
      clearGuestSession()
    }
  }, [tenant])

  // Obtain the guest token before any content mounts, so widgets that call auth-only APIs
  // (files/presign, datasets/rows, ...) never fire a tokenless request. A failed session
  // still renders the content (anonymous endpoints keep working; widgets surface errors).
  const sessionQuery = useQuery({
    queryKey: ['guest-session', tenant],
    queryFn: async () => {
      setGuestTenant(tenant) // idempotent; guarantees the tenant is registered before the fetch
      return (await refreshGuestToken()) !== null
    },
    staleTime: 10 * 60 * 1000,
    gcTime: 0,
    retry: false,
    refetchOnWindowFocus: false,
  })
  const sessionReady = !sessionQuery.isPending

  return (
    <div className="flex h-screen overflow-hidden md:flex">
      <NavSidebar
        items={data ?? []}
        isPending={isPending}
        isError={isError}
        brandName={tenant}
        renderBrandLink={({ onClick, className, ariaLabel, children }) => (
          <Link to="/$tenant/public" params={{ tenant }} onClick={onClick} aria-label={ariaLabel} className={className}>
            {children}
          </Link>
        )}
        renderItemLink={renderPublicItemLink(tenant)}
      />
      <main
        id="main-content"
        className="flex min-w-0 flex-1 flex-col overflow-x-hidden md:pl-64"
      >
        <header className="relative z-10 flex shrink-0 items-center justify-end gap-2 border-b border-border bg-card bg-gradient-to-b from-white/[0.08] to-black/[0.06] px-4 py-3 shadow-[0_1px_2px_rgb(0_0_0/0.18),inset_0_1px_0_rgb(255_255_255/0.22)] md:gap-3">
          <Select value={theme} onValueChange={(v) => setTheme(v as Theme)}>
            <SelectTrigger className="h-8 w-[160px]" aria-label={t('theme.label')}>
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {THEMES.map((themeName) => (
                <SelectItem key={themeName} value={themeName}>
                  {t(THEME_LABELS[themeName])}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>

          <Button asChild variant="ghost" size="sm">
            <a href="/login">
              <LogIn className="h-4 w-4" />
              {t('publicMenu.signIn')}
            </a>
          </Button>
        </header>
        <div className="relative min-h-0 flex-1 overflow-y-auto p-4">
          {sessionReady ? (
            <Outlet />
          ) : (
            <Loader2 role="status" aria-label={t('publicMenu.loading')} className="h-5 w-5 animate-spin" />
          )}
        </div>
      </main>
    </div>
  )
}
