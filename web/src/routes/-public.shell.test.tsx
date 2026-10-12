import { describe, it, expect, vi, afterEach } from 'vitest'
import { cleanup, fireEvent, render } from '@testing-library/react'
import type { PublicMenuItem } from '../features/menu/types'
import { isAnonymousPageActive } from '../features/auth/anonymousPage'

vi.mock('react-i18next', () => ({
  useTranslation: () => ({
    t: (key: string, vars?: Record<string, unknown>) =>
      vars ? `${key}:${JSON.stringify(vars)}` : key,
  }),
}))

vi.mock('@tanstack/react-router', () => ({
  createFileRoute: () => (_config: unknown) => ({
    useParams: () => ({ tenant: 'acme' }),
  }),
  Outlet: () => <div data-testid="outlet" />,
  Navigate: (props: { to: string; params?: Record<string, string> }) => (
    <div data-testid="navigate" data-to={props.to} data-params={JSON.stringify(props.params)} />
  ),
  // Minimal Link: resolves $params so the href is assertable.
  Link: (props: {
    to: string
    params?: Record<string, string>
    children?: React.ReactNode
    className?: string
    onClick?: () => void
    'aria-label'?: string
  }) => {
    let href = props.to
    for (const [k, v] of Object.entries(props.params ?? {})) href = href.replace(`$${k}`, v)
    return (
      <a href={href} className={props.className} onClick={props.onClick} aria-label={props['aria-label']}>
        {props.children}
      </a>
    )
  },
}))

vi.mock('../lib/theme/ThemeProvider', () => ({
  useTheme: () => ({ theme: 'light', setTheme: () => {} }),
}))

vi.mock('../components/ui/select', () => ({
  Select: ({ children }: { children?: React.ReactNode }) => <div>{children}</div>,
  SelectTrigger: ({ children }: { children?: React.ReactNode }) => <div data-testid="theme-select">{children}</div>,
  SelectValue: () => null,
  SelectContent: () => null,
  SelectItem: () => null,
}))

interface QueryStub {
  data?: PublicMenuItem[]
  isPending: boolean
  isError: boolean
  error?: unknown
}
let queryStub: QueryStub = { data: [], isPending: false, isError: false }

vi.mock('../features/menu/usePublicMenuQuery', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../features/menu/usePublicMenuQuery')>()
  return { ...actual, usePublicMenuQuery: () => queryStub }
})

import { PublicShell } from './$tenant.public'
import { PublicIndex } from './$tenant.public.index'

function item(over: Partial<PublicMenuItem> & { id: string; name: string }): PublicMenuItem {
  return {
    order: 0,
    icon: null,
    parentId: null,
    designerId: null,
    routePath: null,
    children: [],
    ...over,
  }
}

afterEach(() => {
  cleanup()
  queryStub = { data: [], isPending: false, isError: false }
})

describe('PublicShell', () => {
  it('renders the sidebar tree, outlet, theme selector and sign-in link — no user chip/logout', () => {
    queryStub = {
      data: [
        item({ id: 'm1', name: 'Home page', designerId: 'public_view' }),
        item({
          id: 'p',
          name: 'Section',
          children: [item({ id: 'c1', name: 'Docs', routePath: '/docs', parentId: 'p' })],
        }),
      ],
      isPending: false,
      isError: false,
    }

    const { getByText, queryByText, getByTestId, getByRole } = render(<PublicShell />)

    expect(getByText('acme')).toBeTruthy()
    expect(getByTestId('outlet')).toBeTruthy()
    expect(getByTestId('theme-select')).toBeTruthy()
    expect(getByRole('link', { name: /publicMenu\.signIn/ }).getAttribute('href')).toBe('/login')
    expect(queryByText('auth.logout.button')).toBeNull()

    // Designer-bound item links to /{tenant}/public/{menuId} inside the shell.
    expect(getByText('Home page').closest('a')?.getAttribute('href')).toBe('/$tenant/public/m1'.replace('$tenant', 'acme'))

    // Nested child is revealed by the disclosure toggle.
    expect(queryByText('Docs')).toBeNull()
    fireEvent.click(getByRole('button', { name: /nav\.expandSubmenu/ }))
    expect(getByText('Docs').closest('a')?.getAttribute('href')).toBe('/docs')
  })

  it('marks the page anonymous while mounted so a 401 never redirects to /login', () => {
    const { unmount } = render(<PublicShell />)
    expect(isAnonymousPageActive()).toBe(true)
    unmount()
    expect(isAnonymousPageActive()).toBe(false)
  })
})

describe('PublicIndex', () => {
  it('redirects to the first designer-bound item (depth-first)', () => {
    queryStub = {
      data: [
        item({ id: 'p', name: 'Section', children: [item({ id: 'c1', name: 'Child', designerId: 'd1', parentId: 'p' })] }),
        item({ id: 'm2', name: 'Other', designerId: 'd2' }),
      ],
      isPending: false,
      isError: false,
    }

    const { getByTestId } = render(<PublicIndex />)

    const nav = getByTestId('navigate')
    expect(nav.getAttribute('data-to')).toBe('/$tenant/public/$menuId')
    expect(JSON.parse(nav.getAttribute('data-params')!)).toEqual({ tenant: 'acme', menuId: 'c1' })
  })

  it('shows the empty hint when nothing is linkable', () => {
    queryStub = { data: [], isPending: false, isError: false }
    const { getByText, queryByTestId } = render(<PublicIndex />)
    expect(getByText('publicMenu.empty')).toBeTruthy()
    expect(queryByTestId('navigate')).toBeNull()
  })
})
