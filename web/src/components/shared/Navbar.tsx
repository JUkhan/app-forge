import { Link } from '@tanstack/react-router'
import { useNavMenusQuery } from '../../features/menu/useNavMenusQuery'
import { useCurrentTenantQuery } from '../../features/tenant/useCurrentTenantQuery'
import { useTranslation } from 'react-i18next'
import { NavSidebar, type NavItemLinkProps } from './NavSidebar'
import type { NavMenuItem } from '../../features/menu/types'

// Story 4.7 — desktop sidebar + mobile drawer for the permission-filtered navbar.
// The markup/styling lives in the shared presentational NavSidebar; this component supplies
// the permission-filtered items and the authenticated link targets.
export function Navbar() {
  const { t } = useTranslation()
  const { data, isPending, isError } = useNavMenusQuery()
  const { data: tenant } = useCurrentTenantQuery()

  return (
    <NavSidebar
      items={data ?? []}
      isPending={isPending}
      isError={isError}
      brandName={tenant?.name ?? t('nav.brandName')}
      renderBrandLink={({ onClick, className, ariaLabel, children }) => (
        <Link to="/" onClick={onClick} aria-label={ariaLabel} className={className}>
          {children}
        </Link>
      )}
      renderItemLink={renderAuthenticatedItemLink}
    />
  )
}

// Custom route path takes precedence over a binding (they're mutually exclusive server-side).
// External http(s) paths are handled by NavSidebar; an internal path uses <Link> for
// client-side SPA navigation. A designer binding links to /data/{designerId}. Anything else
// (section header / unbound) returns null → a plain button.
function renderAuthenticatedItemLink(item: NavMenuItem, p: NavItemLinkProps) {
  if (item.routePath) {
    return (
      <Link
        // The route string is dynamic (admin-authored), so it can't satisfy
        // TanStack Router's typed `to` union — cast to never. Link still builds
        // the href and navigates client-side at runtime.
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
        to="/data/$designerId"
        params={{ designerId: item.designerId }}
        // /data/$designerId's validateSearch defaults page/pageSize, so the
        // TanStack Router types require an explicit search object here.
        search={{ page: 1, pageSize: 25 }}
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
