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
vi.mock('@/features/auth/httpClient', () => ({
  httpClient: { get: (...args: unknown[]) => getMock(...args) },
}))
vi.mock('../features/auth/httpClient', () => ({
  httpClient: { get: (...args: unknown[]) => getMock(...args) },
}))

import { PublicMenuRecordsPage } from './$tenant.public_menu_.$menuId'
import { ApiError } from '../lib/api/apiError'

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
  it('requests the anonymous records endpoint and renders a read-only table', async () => {
    getMock.mockResolvedValue({
      columns: ['title'],
      data: [{ id: 'r1', title: 'Alpha' }],
      total: 1,
      page: 1,
      pageSize: 25,
      totalPages: 1,
    })

    renderPage()

    expect(await screen.findByText('Alpha')).toBeTruthy()
    expect(screen.getByRole('columnheader', { name: 'title' })).toBeTruthy()
    expect(getMock).toHaveBeenCalledWith(
      '/api/public/acme/menus/menu-1/records?page=1&pageSize=25',
    )
  })

  it('shows the not-found message on a 404', async () => {
    getMock.mockRejectedValue(new ApiError(404, 'TENANT_NOT_FOUND', 'nope'))

    renderPage()

    await waitFor(() => expect(screen.getByRole('alert')).toBeTruthy())
    expect(screen.getByRole('alert').textContent).toBe('publicMenu.records.notFound')
  })
})
