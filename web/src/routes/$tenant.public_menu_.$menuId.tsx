import { useState } from 'react'
import { createFileRoute } from '@tanstack/react-router'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Loader2 } from 'lucide-react'
import { httpClient } from '../features/auth/httpClient'
import { ApiError } from '../lib/api/apiError'
import DynamicComponent from '../components/designer/DynamicComponent'
import type { ComponentSchemaDto } from '../types/designer'

// Anonymous top-level route (outside _app, no beforeLoad guard). The menu's Published schema is
// loaded from GET /api/public/{tenant}/menus/{menuId}/schema. A VIEW-mode designer renders
// read-only through DynamicComponent (no onSave); anything else falls back to a paged read-only
// table from GET /api/public/{tenant}/menus/{menuId}/records. The trailing underscore on
// `public_menu_` keeps this route un-nested from the /$tenant/public_menu list page.
// eslint-disable-next-line react-refresh/only-export-components
export const Route = createFileRoute('/$tenant/public_menu_/$menuId')({
  component: PublicMenuRecordsPage,
})

const PAGE_SIZE = 25

interface PublicMenuRecordsResponse {
  columns: string[]
  data: Array<Record<string, unknown>>
  total: number
  page: number
  pageSize: number
  totalPages: number
}

function formatCell(value: unknown): string {
  if (value === null || value === undefined) return ''
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

function Shell({ title, tenant, wide, children }: {
  title: string
  tenant: string
  wide?: boolean
  children: React.ReactNode
}) {
  const { t } = useTranslation()
  return (
    <div className="flex min-h-screen items-start justify-center bg-gradient-to-br from-background to-muted px-4 py-12">
      <div
        className={`w-full ${wide ? 'max-w-4xl' : 'max-w-2xl'} rounded-xl border border-border bg-card p-6 shadow-sm`}
      >
        <div className="mb-4 flex items-center justify-between gap-4">
          <h1 className="text-2xl font-bold tracking-tight text-foreground">{title}</h1>
          <a
            href={`/${encodeURIComponent(tenant)}/public_menu`}
            className="text-sm font-medium text-primary hover:underline"
          >
            {t('publicMenu.records.back')}
          </a>
        </div>
        {children}
      </div>
    </div>
  )
}

function RecordsTable({ tenant, menuId }: { tenant: string; menuId: string }) {
  const { t } = useTranslation()
  const [page, setPage] = useState(1)

  const query = useQuery({
    queryKey: ['public-menu-records', tenant, menuId, page],
    queryFn: () =>
      httpClient.get<PublicMenuRecordsResponse>(
        `/api/public/${encodeURIComponent(tenant)}/menus/${encodeURIComponent(menuId)}/records?page=${page}&pageSize=${PAGE_SIZE}`,
      ),
    placeholderData: keepPreviousData,
    retry: false,
    refetchOnWindowFocus: false,
  })

  const notFound = query.error instanceof ApiError && query.error.status === 404
  const result = query.data

  if (query.isPending) {
    return (
      <Loader2
        role="status"
        aria-label={t('publicMenu.records.loading')}
        className="h-5 w-5 animate-spin"
      />
    )
  }
  if (notFound) {
    return (
      <p role="alert" className="text-sm text-muted-foreground">
        {t('publicMenu.records.notFound')}
      </p>
    )
  }
  if (query.isError || !result) {
    return (
      <p role="alert" className="text-sm text-destructive">
        {t('publicMenu.records.loadError')}
      </p>
    )
  }
  if (result.data.length === 0) {
    return <p className="text-sm text-muted-foreground">{t('publicMenu.records.empty')}</p>
  }
  return (
    <>
      <div className="overflow-x-auto">
        <table className="w-full text-left text-sm">
          <thead>
            <tr className="border-b border-border">
              {result.columns.map((c) => (
                <th key={c} scope="col" className="px-3 py-2 font-medium text-foreground">
                  {c}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {result.data.map((row, i) => (
              <tr key={String(row.id ?? i)} className="border-b border-border last:border-0">
                {result.columns.map((c) => (
                  <td key={c} className="px-3 py-2 text-foreground">
                    {formatCell(row[c])}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="mt-4 flex items-center justify-between text-sm text-muted-foreground">
        <span>{t('publicMenu.records.total', { count: result.total })}</span>
        <div className="flex items-center gap-3">
          <button
            type="button"
            className="rounded-md border border-border px-3 py-1 disabled:opacity-50"
            disabled={page <= 1}
            onClick={() => setPage((p) => Math.max(1, p - 1))}
          >
            {t('publicMenu.records.previous')}
          </button>
          <span>
            {t('publicMenu.records.pageOf', {
              page: result.page,
              totalPages: Math.max(1, result.totalPages),
            })}
          </span>
          <button
            type="button"
            className="rounded-md border border-border px-3 py-1 disabled:opacity-50"
            disabled={page >= result.totalPages}
            onClick={() => setPage((p) => p + 1)}
          >
            {t('publicMenu.records.next')}
          </button>
        </div>
      </div>
    </>
  )
}

export function PublicMenuRecordsPage() {
  const { t } = useTranslation()
  const { tenant, menuId } = Route.useParams()

  const schemaQuery = useQuery({
    queryKey: ['public-menu-schema', tenant, menuId],
    queryFn: () =>
      httpClient.get<ComponentSchemaDto>(
        `/api/public/${encodeURIComponent(tenant)}/menus/${encodeURIComponent(menuId)}/schema`,
      ),
    retry: false,
    refetchOnWindowFocus: false,
  })

  const schema = schemaQuery.data

  if (schemaQuery.isPending) {
    return (
      <Shell title={t('publicMenu.records.title')} tenant={tenant}>
        <Loader2
          role="status"
          aria-label={t('publicMenu.records.loading')}
          className="h-5 w-5 animate-spin"
        />
      </Shell>
    )
  }

  if (schemaQuery.isError || !schema) {
    const notFound = schemaQuery.error instanceof ApiError && schemaQuery.error.status === 404
    return (
      <Shell title={t('publicMenu.records.title')} tenant={tenant}>
        <p
          role="alert"
          className={`text-sm ${notFound ? 'text-muted-foreground' : 'text-destructive'}`}
        >
          {notFound ? t('publicMenu.records.notFound') : t('publicMenu.records.loadError')}
        </p>
      </Shell>
    )
  }

  if (schema.mode === 'VIEW') {
    return (
      <Shell title={schema.displayName} tenant={tenant}>
        {/* No onSave → read-only render (same as the authenticated VIEW-mode page). */}
        <DynamicComponent designerId={schema.designerId} schema={schema} />
      </Shell>
    )
  }

  return (
    <Shell title={schema.displayName || t('publicMenu.records.title')} tenant={tenant} wide>
      <RecordsTable tenant={tenant} menuId={menuId} />
    </Shell>
  )
}
