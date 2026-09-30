---
title: 'Show tenant admin email before password on tenant-created panel'
type: 'feature'
created: '2026-09-30'
status: 'done'
route: 'oneshot'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** After creating a tenant, the panel in `TenantsPage.tsx` shows only the tenant admin's temporary password; the admin's email is server-derived and never returned, so the super-admin cannot tell which login the password belongs to.

**Approach:** Return the derived admin email in `CreateTenantResponse` and render it above the tenant admin's password in the panel (same email-then-password order as the dev credentials block).

</frozen-after-approval>

## Implementation Notes

- Backend: `CreateTenantResponse` gains `AdminEmail` (after `TemporaryPassword`); `TenantEndpoints` passes the already-derived `adminEmail`.
- Frontend: `CreateTenantResponse.adminEmail` added; `TenantsPage` renders it before the password row.
