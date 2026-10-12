import { useState } from 'react'
import { createFileRoute } from '@tanstack/react-router'
import { useQuery, keepPreviousData } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Loader2 } from 'lucide-react'
import { httpClient } from '../features/auth/httpClient'
import { ApiError } from '../lib/api/apiError'

// Anonymous top-level route (outside _app, no beforeLoad guard). Read-only, paged table of the
// records behind a public designer-bound menu item, from
// GET /api/public/{tenant}/menus/{menuId}/records. The trailing underscore on `public_menu_`
// keeps this route un-nested from the /$tenant/public_menu list page (the URL is unchanged).
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

export function PublicMenuRecordsPage() {
  const { t } = useTranslation()
  const { tenant, menuId } = Route.useParams()
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

  return (
    <div className="flex min-h-screen items-start justify-center bg-gradient-to-br from-background to-muted px-4 py-12">
      <div className="w-full max-w-4xl rounded-xl border border-border bg-card p-6 shadow-sm">
        <div className="mb-4 flex items-center justify-between gap-4">
          <h1 className="text-2xl font-bold tracking-tight text-foreground">
            {t('publicMenu.records.title')}
          </h1>
          <a
            href={`/${encodeURIComponent(tenant)}/public_menu`}
            className="text-sm font-medium text-primary hover:underline"
          >
            {t('publicMenu.records.back')}
          </a>
        </div>
        {query.isPending ? (
          <Loader2
            role="status"
            aria-label={t('publicMenu.records.loading')}
            className="h-5 w-5 animate-spin"
          />
        ) : notFound ? (
          <p role="alert" className="text-sm text-muted-foreground">
            {t('publicMenu.records.notFound')}
          </p>
        ) : query.isError || !result ? (
          <p role="alert" className="text-sm text-destructive">
            {t('publicMenu.records.loadError')}
          </p>
        ) : result.data.length === 0 ? (
          <p className="text-sm text-muted-foreground">{t('publicMenu.records.empty')}</p>
        ) : (
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
        )}
      </div>
    </div>
  )
}
