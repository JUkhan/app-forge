import { createFileRoute, Navigate } from '@tanstack/react-router'
import { useTranslation } from 'react-i18next'
import { Loader2 } from 'lucide-react'
import { ApiError } from '../lib/api/apiError'
import { firstBoundItem, usePublicMenuQuery } from '../features/menu/usePublicMenuQuery'

// `/{tenant}/public` with no item selected: land on the first designer-bound item when there is
// one, otherwise show an empty-state hint inside the shell's content area.
// eslint-disable-next-line react-refresh/only-export-components
export const Route = createFileRoute('/$tenant/public/')({
  component: PublicIndex,
})

export function PublicIndex() {
  const { t } = useTranslation()
  const { tenant } = Route.useParams()
  const query = usePublicMenuQuery(tenant)

  if (query.isPending) {
    return <Loader2 role="status" aria-label={t('publicMenu.loading')} className="h-5 w-5 animate-spin" />
  }
  if (query.isError) {
    const notFound = query.error instanceof ApiError && query.error.status === 404
    return (
      <p role="alert" className={`text-sm ${notFound ? 'text-muted-foreground' : 'text-destructive'}`}>
        {notFound ? t('publicMenu.notFound') : t('publicMenu.loadError')}
      </p>
    )
  }

  const first = firstBoundItem(query.data)
  if (first) {
    return <Navigate to="/$tenant/public/$menuId" params={{ tenant, menuId: first.id }} replace />
  }

  return (
    <p className="text-sm text-muted-foreground">
      {query.data.length === 0 ? t('publicMenu.empty') : t('publicMenu.selectItem')}
    </p>
  )
}
