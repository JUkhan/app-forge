---
title: 'Public menu items ("Is Public" flag) served at /{tenant}/public_menu'
type: 'feature'
created: '2026-10-12'
status: 'done'
baseline_commit: 'ab5ccd0c059d2e6b0f569dd6f7403f0448896552'
route: 'dispatch'
review_loop_iteration: 0
context: ['{project-root}/multi-tenant.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Every menu item is authenticated and role-filtered. Tenant admins cannot publish a menu entry that anonymous visitors can see.

**Approach:** Add an `IsPublic` boolean to `Menu`, exposed as an "Is Public" checkbox in Create Menu (and Edit). A new anonymous page `/{tenant_schema_name}/public_menu` lists the tenant's active public menu items; no login, no role check.

## Boundaries & Constraints

**Always:** `IsPublic` defaults to false (existing menus unchanged). Only `IsActive && IsPublic` items are exposed. Tenant resolved from the URL segment (the tenant's `schema_name`), never from a JWT. Anonymous response contains only id/name/order/icon/parentId/designerId/routePath — no role ids or audit fields. Unknown or non-Active tenant → 404, indistinguishable. Public-ness is per item: a public child under a non-public parent is listed at top level. Decision: access scope is **list only** — route-path items link out; designer-bound items are listed but their data still requires login.

**Never:** No change to auth on `/api/menus`, `/api/admin/*` or `/api/data/*`. No anonymous data-read endpoint. No new tenant slug column.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Public list | Active tenant, active public items | 200, nested tree of those items, no auth header | N/A |
| Non-public / inactive item | `IsPublic=false` or `IsActive=false` | Omitted | N/A |
| Unknown tenant | slug matches no tenant | 404 `TENANT_NOT_FOUND` | N/A |
| Suspended tenant | status != Active | 404 same body as unknown | N/A |
| Invalid slug | fails SafeIdentifier shape | 404 before any DB lookup | N/A |
| Authenticated nav | logged-in user | `/api/menus` unchanged (role-filtered); public flag does not bypass roles | N/A |

</frozen-after-approval>

## Code Map

- `src/AppForge.Api/Domain/Entities/Menu.cs` -- add `bool IsPublic`
- `src/AppForge.Api/Infrastructure/Persistence/AppForgeDbContext.cs` + `Migrations/` -- map `is_public` (default false); new migration, replayed into tenant schemas
- `src/AppForge.Api/Features/Menus/Dtos/{CreateMenuRequest,UpdateMenuRequest,MenuResponse,MenuListItem}.cs` -- carry `IsPublic` (optional, default false)
- `src/AppForge.Api/Features/Menus/MenuService.cs` -- persist in Create/Update; `ToResponse`; new `GetPublicMenusAsync(schema)`; reuse `IMenuCache` invalidation
- `src/AppForge.Api/Features/Menus/MenuEndpoints.cs`, `Program.cs:803` -- model for new anonymous group `GET /api/public/{tenant}/menus` (`AllowAnonymous`, dedicated rate-limit)
- `src/AppForge.Api/Features/Tenancy/ITenantLookupCache.cs`, `TenantContext` -- resolve tenant by schema name, `Set(...)` before querying `menus` (middleware leaves context unset for anonymous)
- `web/src/routes/_app/admin/menus.tsx` (create form), `menus.$menuId.tsx` (edit), `web/src/features/menu/types.ts`, i18n files -- checkbox + types + labels
- `web/src/routes/` -- new top-level route `$tenant.public_menu.tsx` outside `_app` (no auth redirect)

## Tasks & Acceptance

**Execution:**
- [x] `Menu.cs`, `AppForgeDbContext.cs`, new migration -- add non-null `is_public` bool default false -- persistence
- [x] Menu DTOs + validators + `MenuService.cs` -- accept/return `IsPublic` on create/update/get -- admin control
- [x] `MenuService.cs`, new `PublicMenuEndpoints.cs`, `Program.cs` -- anonymous `GET /api/public/{tenant}/menus`, resolve tenant by schema name, active+public only -- public read
- [x] `web/src/routes/_app/admin/menus*.tsx`, `types.ts`, i18n -- "Is Public" checkbox in create and edit -- admin UI
- [x] `web/src/routes/$tenant.public_menu.tsx` -- anonymous page rendering the tree -- public UI
- [x] `MenuIntegrationTests.cs`, web tests -- cover the I/O matrix and checkbox payload -- regression

**Acceptance Criteria:**
- Given an admin creates a menu with Is Public checked, when saved, then it is returned with `isPublic: true` and appears at `/{tenant}/public_menu` without login.
- Given a menu is public but inactive, when an anonymous user loads the page, then it is not listed.
- Given an anonymous request, when it calls `/api/menus` or any `/api/data/*`, then it still receives 401.

## Implementation Notes

## Review Triage Log

| Finding | Verdict | Route | Evidence |
|---|---|---|---|
| Missing create/update/list IsPublic tests (BH, VG, EC) | medium | patch | Only PublicMenuIntegrationTests touches IsPublic; null-means-unchanged is unpinned |
| Missing nested-tree / designerId / routePath assertions (VG) | medium | patch | Test seeds only a public child of a private parent |
| No cross-tenant isolation test (BH) | medium | patch | Handler sets tenant context from URL; only single-tenant tests |
| No web test for checkbox payload; task marked [x] (EC, BH) | medium | patch | Diff has no web tests |
| Migration not applied to already-provisioned tenant schemas (BH, VG) | medium | defer | Pre-existing: startup fan-out not implemented per multi-tenant.md |
| Parent cycle breaks public tree (EC) | false | rejected | Max depth 2 enforced on create; UpdateMenuRequest has no ParentId, so cycles cannot form |
| `//host` routePath / javascript: XSS (BH, EC) | false | rejected | SetMenuRoutePathRequestValidator regex rejects `//` and non-http(s) schemes |
| Rate limit shared behind proxy (BH, EC) | false | rejected | `app.UseForwardedHeaders()` is configured (Program.cs:684) |
| Tenant lookup uncached (BH, EC) | low | rejected | Rate limited 60/min/IP; fix adds cache complexity |
| Hide bound items with unusable provisioning (EC) | false | rejected | Spec decision: bound items are listed, data is login-gated |
| Orphaned public child promoted to top level (BH) | false | rejected | Recorded spec decision |
| Role-restricted item exposed once public (EC) | low | rejected | Intended: public means no authorization; help text wording only |
| Client slug guard, Cache-Control, rate-limit test, non-en locales, TRUNCATE race, route shape in 401 test | low | rejected | Cosmetic/unlikely; fixes add complexity |
| ValueGeneratedOnAdd + HasDefaultValue footgun (BH) | false | rejected | Harmless for a bool defaulting to false |

## Spec Change Log

### 2026-10-12 — human renegotiated access scope (list only → read-only data)

User reported a public designer-bound menu redirects to login on click and chose anonymous read access to the bound data ("only 2"; fixing existing tenant schemas explicitly NOT requested). Amended intent:

- Add `GET /api/public/{tenant}/menus/{menuId}/records?page&pageSize` (anonymous, `public-menu` rate limit). designerId is derived server-side from the menu; never taken from the client.
- Served only when the menu is `IsActive && IsPublic`, has a DesignerId with ProvisioningStatus Success, in an Active tenant. Anything else → the same 404.
- Read-only list, paged (max 100), default sort, soft-deleted rows excluded, no client filter/sort params. Designers with an AuthFilterFieldKey (user-scoped) or a dataset binding are NOT served anonymously (404). Only user-defined columns plus `id` are returned.
- Public page: clicking a bound item opens `/{tenant}/public_menu/{menuId}`, a read-only table of those records; no link to `/data/{designerId}`.
- Out of scope: backfilling `is_public` into existing tenant schemas (deferred item stands).

### 2026-10-12 — human requested hidden guest user + auto-login for /{tenant}/public (allowlist-scoped)

Widgets on public layouts call auth-only APIs (`/api/files/presign`, `POST /api/datasets/{id}/rows`) and 401. Human chose: hidden per-tenant guest user auto-logged-in on `/{tenant}/public`, scoped by an allowlist (not a plain user).

- Hidden role `public-guest` + hidden guest user per tenant, created at tenant onboarding (lazily ensured for existing tenants). Hidden from Users/Roles/Menu-role surfaces, not assignable, cannot log in with a password.
- `POST /api/public/{tenant}/session` (anonymous, rate-limited) issues a short-lived access-only JWT with a `guest` claim and the tenant id. No refresh token.
- Guest enforcement is a single choke point: guest tokens may only call an explicit route allowlist, all writes denied, and datasets/rows (+export/chart if used), files/presign, data reads and options only for datasets/file keys/designers referenced by the Published layouts of active public menus (including nested child designers), cached briefly per tenant.
- Frontend: public shell obtains and keeps a guest token (separate from the real session), refreshes on 401 once; httpClient uses it only on public pages.
