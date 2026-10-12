import { useState, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { ChevronDown, ChevronRight, LayoutDashboard, Menu as MenuIcon, X } from 'lucide-react'
import { cn } from '@/lib/utils'
import { NavLucideIcon } from '../icons/NavLucideIcon'
import { Skeleton } from '@/components/ui/skeleton'
import type { NavMenuItem } from '../../features/menu/types'
import type { MenuIcon as MenuIconType } from '../../features/menu/types'

// Presentational desktop sidebar + mobile drawer shared by the authenticated Navbar and the
// anonymous public-menu shell. It owns the markup/styling, drawer state and disclosure state;
// the caller supplies the items and how a row/brand turns into a router <Link>:
//   - external http(s) routePath rows are rendered here as new-tab anchors;
//   - every other linkable row goes through `renderItemLink` (return null for "not a link",
//     which renders a plain button — e.g. a section header).
//
// Touch targets: every interactive control gets `min-h-[44px] min-w-[44px]` (FR-37 AC-3).

export interface NavItemLinkProps {
  onClick: () => void
  // Always-on layout classes + the inactive/active colour variants (kept separate so a router
  // <Link> that concatenates base + activeProps cannot leave a stale inactive colour behind).
  baseClassName: string
  activeClassName: string
  inactiveClassName: string
  children: ReactNode
}

export interface NavBrandLinkProps {
  onClick: () => void
  className: string
  ariaLabel: string
  children: ReactNode
}

export interface NavSidebarProps {
  items: NavMenuItem[]
  isPending: boolean
  isError: boolean
  brandName: string
  renderBrandLink: (props: NavBrandLinkProps) => ReactNode
  renderItemLink: (item: NavMenuItem, props: NavItemLinkProps) => ReactNode | null
}

export function NavSidebar({
  items,
  isPending,
  isError,
  brandName,
  renderBrandLink,
  renderItemLink,
}: NavSidebarProps) {
  const { t } = useTranslation()
  const [drawerOpen, setDrawerOpen] = useState(false)
  const [openParents, setOpenParents] = useState<Set<string>>(() => new Set())

  function toggleParent(id: string) {
    setOpenParents((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  function closeDrawer() {
    setDrawerOpen(false)
  }

  return (
    <>
      {/* Skip-link for keyboard users (FR-42 boundary). Visually hidden until focused. */}
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-50 focus:rounded-md focus:bg-popover focus:px-3 focus:py-2 focus:text-sm focus:text-popover-foreground focus:shadow-lg focus:ring-2 focus:ring-ring"
      >
        {t('nav.skipToContent')}
      </a>

      {/* Mobile hamburger — hidden at md+. Positioned over the global header
          on mobile (the global header has py-2 + the hamburger is top-2). */}
      <button
        type="button"
        aria-label={drawerOpen ? t('nav.closeMenu') : t('nav.openMenu')}
        aria-expanded={drawerOpen}
        onClick={() => setDrawerOpen((v) => !v)}
        className="fixed left-2 top-2 z-30 inline-flex min-h-[44px] min-w-[44px] items-center justify-center rounded-md border border-border bg-card text-foreground shadow-sm transition-colors hover:bg-overlay-hover active:bg-overlay-active md:hidden"
      >
        {drawerOpen ? <X className="h-5 w-5" /> : <MenuIcon className="h-5 w-5" />}
      </button>

      {/* Mobile drawer backdrop */}
      {drawerOpen && (
        <button
          type="button"
          aria-label={t('nav.closeMenu')}
          onClick={closeDrawer}
          className="fixed inset-0 z-20 bg-black/40 backdrop-blur-sm md:hidden"
        />
      )}

      <nav
        role="navigation"
        aria-label={t('nav.primaryAriaLabel')}
        className={cn(
          // bg-sidebar base + a theme-neutral white→black overlay gives the
          // panel a soft top-to-bottom gradient (lighter top, deeper bottom).
          'z-30 flex w-64 flex-col border-r border-sidebar-border bg-sidebar bg-gradient-to-b from-white/[0.06] to-black/[0.08] md:fixed md:inset-y-0 md:left-0 md:block',
          drawerOpen ? 'fixed inset-y-0 left-0 block' : 'hidden',
        )}
      >
        {/* Brand mark — small icon-tile + wordmark across the top of the sidebar. */}
        <div className="flex shrink-0 items-center gap-2 px-4 py-4">
          {renderBrandLink({
            onClick: closeDrawer,
            ariaLabel: t('nav.brandName'),
            className:
              'flex h-8 w-8 items-center justify-center rounded-lg bg-primary text-primary-foreground shadow-sm transition-opacity hover:opacity-90',
            children: <LayoutDashboard className="h-4 w-4" />,
          })}
          <span className="text-sm font-bold tracking-tight text-sidebar-foreground">
            {brandName}
          </span>
        </div>

        {/* Menu items — scrolls if the list grows beyond the viewport. */}
        <div className="flex-1 overflow-y-auto p-3">
          {isPending ? (
            <NavSkeleton />
          ) : isError ? (
            // Render an empty nav rather than a misleading "No menus" copy when the backend is down.
            null
          ) : items.length === 0 ? (
            <p className="px-2 text-sm text-muted-foreground">{t('nav.emptyMessage')}</p>
          ) : (
            <ul className="space-y-0.5">
              {items.map((item) => (
                <NavListRow
                  key={item.id}
                  item={item}
                  expanded={openParents.has(item.id)}
                  onToggle={() => toggleParent(item.id)}
                  onLeafClick={closeDrawer}
                  renderItemLink={renderItemLink}
                />
              ))}
            </ul>
          )}
        </div>
      </nav>
    </>
  )
}

interface NavListRowProps {
  item: NavMenuItem
  expanded: boolean
  onToggle: () => void
  onLeafClick: () => void
  renderItemLink: NavSidebarProps['renderItemLink']
}

function NavListRow({ item, expanded, onToggle, onLeafClick, renderItemLink }: NavListRowProps) {
  const { t } = useTranslation()
  const hasChildren = item.children.length > 0

  return (
    <li>
      <div className="flex items-center gap-0.5">
        <NavLeaf
          item={item}
          onLeafClick={onLeafClick}
          className="flex-1"
          renderItemLink={renderItemLink}
        />
        {hasChildren && (
          <button
            type="button"
            onClick={onToggle}
            aria-expanded={expanded}
            aria-label={
              expanded
                ? t('nav.collapseSubmenu', { name: item.name })
                : t('nav.expandSubmenu', { name: item.name })
            }
            className="inline-flex min-h-[44px] min-w-[44px] items-center justify-center rounded-md text-muted-foreground transition-colors hover:bg-overlay-hover active:bg-overlay-active hover:text-sidebar-foreground"
          >
            {expanded ? (
              <ChevronDown className="h-4 w-4" />
            ) : (
              <ChevronRight className="h-4 w-4" />
            )}
          </button>
        )}
      </div>
      {hasChildren && expanded && (
        <ul className="mt-0.5 ml-3 space-y-0.5 border-l border-sidebar-border pl-2">
          {item.children.map((child) => (
            <li key={child.id}>
              <NavLeaf
                item={child}
                onLeafClick={onLeafClick}
                className="w-full"
                renderItemLink={renderItemLink}
              />
            </li>
          ))}
        </ul>
      )}
    </li>
  )
}

// A menu row's clickable area. Both link and button forms call onLeafClick so the mobile
// drawer auto-closes (AC-4) when a leaf is tapped.
function NavLeaf({
  item,
  onLeafClick,
  className,
  renderItemLink,
}: {
  item: NavMenuItem
  onLeafClick: () => void
  className?: string
  renderItemLink: NavSidebarProps['renderItemLink']
}) {
  const itemBase = cn(
    'group flex min-h-[44px] items-center gap-2.5 rounded-md px-2.5 text-left text-sm transition-colors',
    className,
  )
  const itemInactive =
    'text-sidebar-foreground/80 hover:bg-overlay-hover hover:text-sidebar-foreground'
  const itemActive = 'bg-primary/10 text-primary hover:bg-primary/15 hover:text-primary'

  const content = (
    <>
      <NavIcon icon={item.icon} />
      <span className="truncate">{item.name}</span>
    </>
  )

  // An http(s) routePath opens externally in a new tab via a plain anchor.
  if (item.routePath && /^https?:\/\//.test(item.routePath)) {
    return (
      <a
        href={item.routePath}
        target="_blank"
        rel="noopener noreferrer"
        onClick={onLeafClick}
        className={cn(itemBase, itemInactive)}
      >
        {content}
      </a>
    )
  }

  const link = renderItemLink(item, {
    onClick: onLeafClick,
    baseClassName: itemBase,
    activeClassName: cn(itemBase, itemActive),
    inactiveClassName: cn(itemBase, itemInactive),
    children: content,
  })
  if (link !== null) return <>{link}</>

  return (
    <button type="button" onClick={onLeafClick} className={cn(itemBase, itemInactive)}>
      {content}
    </button>
  )
}

// MinIO icons render a placeholder for now (the real image needs files-api refresh-urls).
// Unknown lucide names also fall back to the Box placeholder.
function NavIcon({ icon }: { icon: MenuIconType | null }) {
  const { t } = useTranslation()
  if (icon?.type === 'lucide' && icon.name) {
    return <NavLucideIcon name={icon.name} size={16} aria-hidden className="shrink-0 text-muted-foreground group-hover:text-sidebar-foreground" />
  }
  if (icon?.type === 'minio') {
    return <NavLucideIcon name="Box" size={16} aria-label={t('nav.menuIconPlaceholder')} className="shrink-0 text-muted-foreground group-hover:text-sidebar-foreground" />
  }
  return <NavLucideIcon name="Box" size={16} aria-hidden className="shrink-0 text-muted-foreground group-hover:text-sidebar-foreground" />
}

function NavSkeleton() {
  return (
    // Tests assert exactly 3 .animate-pulse rows during isPending — keep
    // <Skeleton> (which adds animate-pulse) and the 3-entry length.
    <ul className="space-y-2" aria-hidden>
      {[0, 1, 2].map((i) => (
        <li key={i}>
          <Skeleton className="h-9 w-full" />
        </li>
      ))}
    </ul>
  )
}
