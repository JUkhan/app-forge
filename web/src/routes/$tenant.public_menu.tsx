import { createFileRoute } from '@tanstack/react-router'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Loader2 } from 'lucide-react'
import { httpClient } from '../features/auth/httpClient'
import { ApiError } from '../lib/api/apiError'
import { NavLucideIcon } from '../components/icons/NavLucideIcon'
import type { PublicMenuItem } from '../features/menu/types'

// Anonymous top-level route (sibling of login.tsx, outside _app) — no beforeLoad
// guard. Lists the tenant's active public menu items from
// GET /api/public/{tenant}/menus. Route-path items link out; designer-bound items
// link to /{tenant}/public_menu/{menuId}, an anonymous read-only table of the records.
export const Route = createFileRoute('/$tenant/public_menu')({
  component: PublicMenuPage,
})

function itemHref(tenant: string, item: PublicMenuItem): string | null {
  if (item.routePath) return item.routePath
  if (item.designerId) {
    return `/${encodeURIComponent(tenant)}/public_menu/${encodeURIComponent(item.id)}`
  }
  return null
}

function PublicMenuNode({ tenant, item }: { tenant: string; item: PublicMenuItem }) {
  const href = itemHref(tenant, item)
  const iconName = item.icon?.type === 'lucide' ? item.icon.name : undefined
  const label = (
    <span className="flex items-center gap-2">
      {iconName ? <NavLucideIcon name={iconName} size={16} /> : null}
      {item.name}
    </span>
  )
  return (
    <li>
      {href ? (
        <a href={href} className="font-medium text-primary hover:underline">
          {label}
        </a>
      ) : (
        <span className="font-medium text-foreground">{label}</span>
      )}
      {item.children.length > 0 && (
        <ul className="ml-6 mt-2 space-y-2">
          {item.children.map((c) => (
            <PublicMenuNode key={c.id} tenant={tenant} item={c} />
          ))}
        </ul>
      )}
    </li>
  )
}

// eslint-disable-next-line react-refresh/only-export-components
function PublicMenuPage() {
  const { t } = useTranslation()
  const { tenant } = Route.useParams()

  const query = useQuery({
    queryKey: ['public-menu', tenant],
    queryFn: () => httpClient.get<PublicMenuItem[]>(`/api/public/${encodeURIComponent(tenant)}/menus`),
    retry: false,
    refetchOnWindowFocus: false,
  })

  const notFound = query.error instanceof ApiError && query.error.status === 404

  return (
    <div className="flex min-h-screen items-start justify-center bg-gradient-to-br from-background to-muted px-4 py-12">
      <div className="w-full max-w-md rounded-xl border border-border bg-card p-6 shadow-sm">
        <h1 className="mb-4 text-2xl font-bold tracking-tight text-foreground">
          {t('publicMenu.title')}
        </h1>
        {query.isPending ? (
          <Loader2 role="status" aria-label={t('publicMenu.loading')} className="h-5 w-5 animate-spin" />
        ) : notFound ? (
          <p role="alert" className="text-sm text-muted-foreground">{t('publicMenu.notFound')}</p>
        ) : query.isError ? (
          <p role="alert" className="text-sm text-destructive">{t('publicMenu.loadError')}</p>
        ) : query.data.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t('publicMenu.empty')}</p>
        ) : (
          <ul className="space-y-2">
            {query.data.map((item) => (
              <PublicMenuNode key={item.id} tenant={tenant} item={item} />
            ))}
          </ul>
        )}
      </div>
    </div>
  )
}
