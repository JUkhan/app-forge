import { useState } from 'react'
import { createFileRoute } from '@tanstack/react-router'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Loader2 } from 'lucide-react'
import { httpClient } from '../features/auth/httpClient'
import { ApiError } from '../lib/api/apiError'
import DynamicComponent from '../components/designer/DynamicComponent'
import type { ComponentSchemaDto } from '../types/designer'

// Content of the anonymous public shell ($tenant.public.tsx): the selected menu item, rendered in
// the shell's content area. The menu's Published schema is loaded from
// GET /api/public/{tenant}/menus/{menuId}/schema. A VIEW-mode designer renders read-only through
// DynamicComponent (no onSave); anything else falls back to a paged read-only table from
// GET /api/public/{tenant}/menus/{menuId}/records.
// eslint-disable-next-line react-refresh/only-export-components
export const Route = createFileRoute('/$tenant/public/$menuId')({
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

function Page({ title, wide, children }: { title: string; wide?: boolean; children: React.ReactNode }) {
  return (
    <div
      className={`mx-auto w-full ${wide ? 'max-w-5xl' : 'max-w-3xl'} rounded-xl border border-border bg-card p-6 shadow-sm`}
    >
      <h1 className="mb-4 text-2xl font-bold tracking-tight text-foreground">{title}</h1>
      {children}
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
      <Page title={t('publicMenu.records.title')}>
        <Loader2
          role="status"
          aria-label={t('publicMenu.records.loading')}
          className="h-5 w-5 animate-spin"
        />
      </Page>
    )
  }

  if (schemaQuery.isError || !schema) {
    const notFound = schemaQuery.error instanceof ApiError && schemaQuery.error.status === 404
    return (
      <Page title={t('publicMenu.records.title')}>
        <p
          role="alert"
          className={`text-sm ${notFound ? 'text-muted-foreground' : 'text-destructive'}`}
        >
          {notFound ? t('publicMenu.records.notFound') : t('publicMenu.records.loadError')}
        </p>
      </Page>
    )
  }

  if (schema.mode === 'VIEW') {
    return (
      <Page title={schema.displayName}>
        {/* No onSave → read-only render (same as the authenticated VIEW-mode page). */}
        <DynamicComponent designerId={schema.designerId} schema={schema} />
      </Page>
    )
  }

  return (
    <Page title={schema.displayName || t('publicMenu.records.title')} wide>
      <RecordsTable tenant={tenant} menuId={menuId} />
    </Page>
  )
}
