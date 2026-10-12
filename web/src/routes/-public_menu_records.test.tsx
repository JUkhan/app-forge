import { describe, it, expect, vi, afterEach } from 'vitest'
import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, opts?: Record<string, unknown>) =>
      opts ? `${key}:${JSON.stringify(opts)}` : key,
  }),
}))

vi.mock('@tanstack/react-router', () => ({
  createFileRoute: () => (_config: unknown) => ({
    useParams: () => ({ tenant: 'acme', menuId: 'menu-1' }),
  }),
}))

const getMock = vi.fn()
vi.mock('../features/auth/httpClient', () => ({
  httpClient: { get: (...args: unknown[]) => getMock(...args) },
}))

vi.mock('../components/designer/DynamicComponent', () => ({
  default: (props: { designerId: string; onSave?: unknown }) => (
    <div
      data-testid="mock-dynamic-component"
      data-designer-id={props.designerId}
      data-has-onsave={String(props.onSave !== undefined)}
    />
  ),
}))

import { PublicMenuRecordsPage } from './$tenant.public_menu_.$menuId'
import { ApiError } from '../lib/api/apiError'

const SCHEMA_URL = '/api/public/acme/menus/menu-1/schema'
const RECORDS_URL = '/api/public/acme/menus/menu-1/records?page=1&pageSize=25'

function schema(mode: 'VIEW' | 'CRUD') {
  return {
    designerId: 'public_view',
    displayName: 'Public View',
    mode,
    status: 'Published',
    latestVersion: 1,
    rootElement: { id: 'root', type: 'Stack', properties: {}, children: [] },
    createdAt: '2026-10-12T00:00:00Z',
    updatedAt: null,
    publishedAt: '2026-10-12T00:00:00Z',
  }
}

function renderPage() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={qc}>
      <PublicMenuRecordsPage />
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
  getMock.mockReset()
})

describe('PublicMenuRecordsPage', () => {
  it('renders a VIEW-mode schema through DynamicComponent, read-only, without fetching records', async () => {
    getMock.mockImplementation((path: string) =>
      path === SCHEMA_URL ? Promise.resolve(schema('VIEW')) : Promise.reject(new Error(`unexpected ${path}`)),
    )

    renderPage()

    const el = await screen.findByTestId('mock-dynamic-component')
    expect(el.getAttribute('data-designer-id')).toBe('public_view')
    expect(el.getAttribute('data-has-onsave')).toBe('false')
    expect(screen.getByRole('heading', { name: 'Public View' })).toBeTruthy()
    expect(getMock).toHaveBeenCalledTimes(1)
    expect(getMock).toHaveBeenCalledWith(SCHEMA_URL)
  })

  it('falls back to the records table for a non-VIEW schema', async () => {
    getMock.mockImplementation((path: string) => {
      if (path === SCHEMA_URL) return Promise.resolve(schema('CRUD'))
      if (path === RECORDS_URL) {
        return Promise.resolve({
          columns: ['title'],
          data: [{ id: 'r1', title: 'Alpha' }],
          total: 1,
          page: 1,
          pageSize: 25,
          totalPages: 1,
        })
      }
      return Promise.reject(new Error(`unexpected ${path}`))
    })

    renderPage()

    expect(await screen.findByText('Alpha')).toBeTruthy()
    expect(screen.getByRole('columnheader', { name: 'title' })).toBeTruthy()
    expect(screen.queryByTestId('mock-dynamic-component')).toBeNull()
  })

  it('shows the not-found message when the schema request 404s', async () => {
    getMock.mockRejectedValue(new ApiError(404, 'TENANT_NOT_FOUND', 'nope'))

    renderPage()

    await waitFor(() => expect(screen.getByRole('alert')).toBeTruthy())
    expect(screen.getByRole('alert').textContent).toBe('publicMenu.records.notFound')
  })
})
