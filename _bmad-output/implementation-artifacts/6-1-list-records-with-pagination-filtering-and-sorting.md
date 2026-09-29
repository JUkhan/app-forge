# Story 6.1: List Records with Pagination, Filtering, and Sorting

Status: done

## Story

As an authorized user with `canRead`,
I want to retrieve a paginated, filtered, and sorted list of records from any dynamic table,
So that I can browse and find data efficiently.

## Acceptance Criteria

**AC-1 — Happy-path list response**
**Given** a provisioned table for `designerId`
**When** I GET `/api/data/{designerId}?page=1&pageSize=25&sort=created_at:desc&filter[title]=foo`
**Then** the response is HTTP 200 with `PagedResult<DynamicRecord>` containing `data`, `total`, `page`, `pageSize`, `totalPages`
**And** system columns are serialized as camelCase JSON keys (`createdAt`, `updatedAt`, `isDeleted`, `createdBy`, `updatedBy`, `cascadeEventId`)
**And** user fieldKey columns are serialized verbatim (e.g., `report_title` stays `report_title`) per AR-46 Option C hybrid

**AC-2 — Sort validation**
**Given** the `sort` query parameter
**When** the server parses it
**Then** up to 3 `column:direction` pairs are accepted (comma-separated, e.g., `sort=created_at:desc,title:asc`)
**And** valid direction values are `asc` and `desc` (case-insensitive)
**And** column names are whitelisted against the schema registry's `ColumnName` list PLUS the four filterable system columns: `id`, `created_at`, `updated_at`, `is_deleted`
**And** an unknown column name returns HTTP 422 with `code: "VALIDATION_FAILED"` and `messageKey: "errors.validationFailed"`

**AC-3 — Filter validation and parameterization**
**Given** the `filter` query parameter (format: `filter[fieldKey]=value`)
**When** the server parses it
**Then** filter keys are whitelisted: user fieldKeys from the schema registry, plus the system keys `id`, `created_at`, `updated_at`, `is_deleted`, `created_by`
**And** filter values are passed as Dapper parameters — never interpolated into SQL (NFR-6)
**And** an unknown filter key returns HTTP 422 with `code: "VALIDATION_FAILED"`

**AC-4 — Unprovisioned designer**
**Given** a `designerId` for which no menu exists with `provisioningStatus = 'Success'`
**When** I GET `/api/data/{designerId}`
**Then** the response is HTTP 404 with `code: "TABLE_NOT_PROVISIONED"` and `messageKey: "errors.tableNotProvisioned"`

**AC-5 — Soft-deleted records included by default**
**Given** the default query (no explicit filter on `is_deleted`)
**When** the list query runs
**Then** soft-deleted records ARE included in `data` and counted in `total`
**And** consumers who want only live records add `filter[is_deleted]=false` to the URL

**AC-6 — Query timeout**
**Given** the dynamic CRUD database connection
**When** any Dapper query runs
**Then** `commandTimeout` is set to 5 seconds on every `connection.QueryAsync` and `connection.ExecuteScalarAsync` call (Decision 1.6 / NFR-6)

**AC-7 — Permission enforcement**
**Given** an authenticated user without `canRead` on this resource
**When** I GET `/api/data/{designerId}`
**Then** the response is HTTP 403 with `code: "FORBIDDEN"` (via the existing `RequirePermission("read")` filter)

---

## Tasks / Subtasks

- [x] **Task 1 — Create `DynamicRecord.cs`** (AC: 1)
  - [x] Create `src/AppForge.Api/Features/DynamicCrud/DynamicRecord.cs`
  - Represents one row from a provisioned dynamic table
  - Internal constructor accepts `IReadOnlyDictionary<string, object?>` (raw PG column names → values from Dapper)
  - Contains the `DynamicRecordJsonConverter : JsonConverter<DynamicRecord>` nested class (or companion class in same file) that implements AR-46 Option C hybrid serialization:
    - For each key in the raw dict:
      - If the key matches a system column name (`id`, `created_at`, `created_by`, `updated_at`, `updated_by`, `is_deleted`, `cascade_event_id`), use the camelCase JSON name from the static mapping table
      - Otherwise, write the key verbatim (user fieldKeys are already validated identifiers)
    - Values are serialized via `JsonSerializer.Serialize(writer, value, options)` — this handles `Guid`, `DateTime`, `DateTimeOffset`, `bool`, `decimal`, `string`, `null`
  - Register the converter in the ASP.NET JSON options (see `Program.cs` where `builder.Services.ConfigureHttpJsonOptions` is called)
  - System column → JSON key mapping (hardcoded static dictionary):
    ```
    id          → id
    created_at  → createdAt
    created_by  → createdBy
    updated_at  → updatedAt
    updated_by  → updatedBy
    is_deleted  → isDeleted
    cascade_event_id → cascadeEventId
    ```

- [x] **Task 2 — Create `DynamicQueryBuilder.cs`** (AC: 1, 2, 3, 5, 6)
  - [x] Create `src/AppForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`
  - Pure static class; all methods are `internal static`; no DI dependencies — makes unit testing straightforward
  - **`ParseSort(string? sortParam, IReadOnlySet<string> allowedColumns)`** → `ParseSortResult` (discriminated union: success list of `(Column, Direction)` or validation error string/code)
    - Splits on comma, then each token on `:` — exactly two parts required
    - Direction: case-insensitive `asc` or `desc`; anything else → validation error
    - Column: must be in `allowedColumns` → if not, return error with the offending column name
    - Max 3 pairs; if more → validation error
  - **`BuildSelectQuery(SafeIdentifier tableName, IReadOnlyList<ColumnDefinition> userColumns, IReadOnlyList<SortParam> sorts, IReadOnlyDictionary<string, string> filters, int page, int pageSize)`** → `(string selectSql, DynamicParameters selectParams)`
    - SELECT list: all system columns + all user column names, each double-quoted: `"id", "created_at", "created_by", "updated_at", "updated_by", "is_deleted", "cascade_event_id", "col1", "col2"...`
    - FROM: `FROM "{tableName.Value}"` (double-quoted — SafeIdentifier is already validated)
    - WHERE: `WHERE 1=1` + per-filter ` AND "{pgColumnName}" = @filter_N` (N = 0, 1, 2 ...)
      - Filter keys that match system column JSON names (`isDeleted`, `createdAt` etc.) must be mapped to their PG names before quoting (see filter key mapping note below)
      - Filter keys that are user fieldKeys are used verbatim (they ARE the PG column names)
    - ORDER BY: built from `sorts` list using double-quoted column names and direction; default (empty sorts) = `ORDER BY "created_at" DESC` for stable pagination
    - LIMIT + OFFSET: `LIMIT @pageSize OFFSET @offset` where `@offset = (page - 1) * pageSize`
  - **`BuildCountQuery(SafeIdentifier tableName, IReadOnlyDictionary<string, string> filters)`** → `(string countSql, DynamicParameters countParams)`
    - `SELECT COUNT(*) FROM "{tableName.Value}" WHERE 1=1 {filterClause}` — same filter clause as SELECT, same params (no LIMIT/OFFSET/ORDER BY)
  - **Filter key mapping note**: consumer sends camelCase system keys in `filter[]` params (per AC-5 example `filter[isDeleted]=false`). The query builder must map:
    ```
    isDeleted    → is_deleted
    createdAt    → created_at
    createdBy    → created_by
    updatedAt    → updated_at
    updatedBy    → updated_by
    cascadeEventId → cascade_event_id
    id           → id  (unchanged)
    ```
    User fieldKeys pass through unchanged (they ARE the PG column names). Filter key whitelist check: validate against PG column names (after mapping), not the JSON names.
  - **Allowed sort columns**: `IReadOnlySet<string>` built in the handler from schema registry columns (`col.ColumnName`) PLUS the four explicitly allowed system columns: `id`, `created_at`, `updated_at`, `is_deleted`
  - **Allowed filter columns**: same registry user columns + the five system PG names: `id`, `created_at`, `updated_at`, `is_deleted`, `created_by`

- [x] **Task 3 — Implement GET handler in `DynamicDataEndpoints.cs`** (AC: 1–7)
  - [x] Modify `src/AppForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`
  - Add `GET /` handler (maps to the base path of the group `/api/data/{designerId}`)
  - Handler signature (Minimal API parameter binding):
    ```csharp
    internal static async Task<IResult> ListRecordsHandler(
        string designerId,
        int page,
        int pageSize,
        string? sort,
        [AsParameters] FilterParams filterParams,
        AppForgeDbContext db,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        CancellationToken ct)
    ```
    - `page` defaults to 1 if ≤ 0; `pageSize` clamped to [1, 100], default 25
    - `[AsParameters]` for `FilterParams` (a record wrapping `HttpContext.Request.Query` or a custom model binder for `filter[key]` syntax — see notes)
  - **Schema resolution flow**:
    1. Validate `designerId` via `SafeIdentifier.TryCreate` → if invalid, return 422 (IDENTIFIER_INVALID / messageKey: "errors.identifierInvalid")
    2. Query EF: find the highest `BoundVersion` for a `Menu` with `DesignerId == designerId AND ProvisioningStatus == "Success"` via `db.Menus.AsNoTracking().Where(...).OrderByDescending(m => m.BoundVersion).Select(m => (int?)m.BoundVersion).FirstOrDefaultAsync(ct)` → if null, return 404 TABLE_NOT_PROVISIONED
    3. Try schema registry cache: `schemaRegistry.TryGet(designerId, boundVersion)` → if miss, load from DB using `db.ComponentSchemaVersions.AsNoTracking().Where(v => v.DesignerId == designerId && v.Version == boundVersion).Select(v => v.RootElement).FirstOrDefaultAsync(ct)`, parse via `RootElementParser`, build entry, `schemaRegistry.Populate(entry)`
    4. Build `allowedSortCols` and `allowedFilterCols` (user columns + system column sets)
    5. Call `DynamicQueryBuilder.ParseSort(sort, allowedSortCols)` → if error, return 422
    6. Parse filter params from query string → validate each key against `allowedFilterCols` → if unknown key, return 422
    7. Build SELECT + COUNT SQL via `DynamicQueryBuilder.BuildSelectQuery(...)` and `DynamicQueryBuilder.BuildCountQuery(...)`
    8. Open connection: `await using var conn = await connectionFactory.CreateOpenConnectionAsync(ct)`
    9. Execute COUNT: `var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(countSql, countParams, commandTimeout: 5, cancellationToken: ct))`
    10. Execute SELECT: `var rows = await conn.QueryAsync(new CommandDefinition(selectSql, selectParams, commandTimeout: 5, cancellationToken: ct))`
    11. Map rows to `DynamicRecord[]`
    12. Return `Results.Ok(new PagedResult<DynamicRecord>(records, total, page, pageSize))`
  - **Filter query string parsing**: `filter[key]=value` is a standard ASP.NET query string array notation. Bind via `HttpContext.Request.Query` where keys matching `filter[*]` are extracted. A helper method `ParseFilterParams(IQueryCollection query)` → `IReadOnlyDictionary<string, string>` in the endpoint class handles this.
  - **Permission**: add `.RequirePermission("read")` to the mapped handler (same pattern as `RequirePermissionFilter` in `RouteGroupExtensions.cs`)
  - **OpenAPI metadata** (per Decision 3.7): `Produces<PagedResult<DynamicRecord>>(200)`, `Produces<ProblemDetails>(404)`, `Produces<ProblemDetails>(422)`, `Produces<ProblemDetails>(403)`

- [x] **Task 4 — Backend tests** (AC: 1–7)
  - [x] Create `src/AppForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs` (unit tests, no DB needed)
    - `ParseSort_SingleAscColumn_ReturnsParsed`
    - `ParseSort_ThreeColumns_AllValid_ReturnsParsed`
    - `ParseSort_FourColumns_ReturnsError`
    - `ParseSort_UnknownColumn_ReturnsError`
    - `ParseSort_InvalidDirection_ReturnsError`
    - `ParseSort_Null_ReturnsEmptyList` (no sort = valid, default ORDER BY applied)
    - `BuildSelectQuery_WithSortAndFilter_GeneratesCorrectSql`
    - `BuildSelectQuery_SystemColumnFilterMapping_IsDeleted_MapsCorrectly` (filter[isDeleted] → is_deleted)
    - `BuildCountQuery_WithFilter_CountSqlHasNoLimitOffset`
    - Estimated: ~10 unit tests

  - [x] Create `src/AppForge.Api.Tests/Features/DynamicCrud/DynamicCrudIntegrationTests.cs` (integration tests, uses `PostgresFixture` + `WebApplicationFactory<Program>`)
    - Class signature: `IClassFixture<PostgresFixture>, IAsyncLifetime`
    - `InitializeAsync`: same pattern as `ProvisioningIntegrationTests` — TRUNCATE tables, drop dynamic tables, seed roles+users, create `_client`
    - Integration tests:
      1. `ListRecords_ProvisionedTable_ReturnsPagedResult` — create designer + publish version + bind menu → GET → 200 with PagedResult (empty data, total=0)
      2. `ListRecords_DefaultIncludesSoftDeleted` — after a soft-delete via raw SQL, verify the deleted record still appears in list
      3. `ListRecords_SortByUserColumn_ReturnsCorrectOrder` — insert 2 records, sort by user column desc, verify order
      4. `ListRecords_FilterByUserColumn_ReturnsFilteredRecords` — insert 2 records, filter by one field value, verify only 1 returned
      5. `ListRecords_UnknownSortColumn_Returns422` — invalid sort column → 422 with VALIDATION_FAILED
      6. `ListRecords_UnprovisionnedDesigner_Returns404` — unknown designerId → 404 with TABLE_NOT_PROVISIONED
      7. `ListRecords_NoCanRead_Returns403` — user without canRead → 403
    - Estimated: ~7 integration tests
  - **Test count**: 407 (end of Story 5.8) → approximately 424 (+17)
  - **xUnit collection**: `[Collection("DynamicCrudTests")]` on this class; if collection attribute is also needed on ProvisioningIntegrationTests to avoid parallel-TRUNCATE race, add it — but DO NOT change the provisioning test classes without also adding the collection attribute to them (pre-existing deferred item about shared PostgresFixture)

- [x] **Task 5 — Frontend API client and hook** (no AC — foundational for Stories 6.9/6.10)
  - [x] Create `web/src/features/data-entry/recordListApi.ts`
    - TypeScript interface `DynamicRecord` with typed system columns + index signature for user fields:
      ```ts
      export interface DynamicRecord {
        id: string
        createdAt: string         // ISO-8601
        createdBy: string | null
        updatedAt: string
        updatedBy: string | null
        isDeleted: boolean
        cascadeEventId: string | null
        [fieldKey: string]: unknown   // user fieldKey columns — verbatim names
      }
      export interface RecordPagedResult {
        data: DynamicRecord[]
        total: number
        page: number
        pageSize: number
        totalPages: number
      }
      export interface RecordListParams {
        page?: number
        pageSize?: number
        sort?: string              // e.g. "created_at:desc,title:asc"
        filter?: Record<string, string>  // e.g. { is_deleted: "false" }
      }
      ```
    - `listRecords(designerId: string, params: RecordListParams): Promise<RecordPagedResult>` — builds URL with query params via `URLSearchParams`, calls `httpClient.get<RecordPagedResult>(...)`
    - Filter params are serialized as `filter[key]=value` by building each pair into `URLSearchParams.append('filter[key]', value)`
  - [x] Create `web/src/features/data-entry/useRecordList.ts`
    - TanStack Query hook:
      ```ts
      export function useRecordList(designerId: string, params: RecordListParams) {
        return useQuery({
          queryKey: ['data', designerId, 'list', params],
          queryFn: () => listRecords(designerId, params),
          staleTime: 0,            // record data is mutable — never serve stale
          placeholderData: keepPreviousData,  // AR-49: no flash during page transitions
          enabled: !!designerId,
        })
      }
      ```
    - Import `keepPreviousData` from `@tanstack/react-query`
    - Re-export `RecordListParams` and `DynamicRecord` for consumers
  - No i18n keys needed (API-only story; no new UI strings)

### Review Findings

- [x] [Review][Patch] Invalid typed filter value falls back to raw string and causes PostgreSQL 500 instead of 422 [`DynamicQueryBuilder.cs:CoerceFilterValue`] — `bool.TryParse("foo")` and `Guid.TryParse("not-a-guid")` fall back to passing the raw string to PostgreSQL for a BOOLEAN/UUID column, which causes a DB runtime exception propagated as 500; should return 422 VALIDATION_FAILED
- [x] [Review][Patch] Integer overflow in OFFSET calculation for large page values [`DynamicQueryBuilder.cs:BuildSelectQuery`] — `var offset = (page - 1) * pageSize` is `int * int` with no upper bound on page; `page = int.MaxValue` overflows to a negative offset and PostgreSQL rejects with `OFFSET must not be negative`, surfacing as 500; fix: use `long` arithmetic or cap page
- [x] [Review][Patch] Missing `<ProblemDetails>` type argument on error `Produces()` declarations [`DynamicDataEndpoints.cs`] — `.Produces(403)`, `.Produces(404)`, `.Produces(422)` lack the `<ProblemDetails>` generic; spec (Decision 3.7) requires `Produces<ProblemDetails>(...)` for all three error responses so OpenAPI emits the body schema
- [x] [Review][Defer] Dead `CoerceFilterValue` branches for `updated_by` and `cascade_event_id` [`DynamicQueryBuilder.cs:CoerceFilterValue`] — deferred, pre-existing: both columns are excluded from `SystemFilterPgColumns` so the Guid coercion is unreachable; the mapping entries in `SystemFilterJsonToPg` for `updatedBy`/`cascadeEventId` exist but lead to a 422; remove the dead coercion or extend the whitelist in a future story if these columns become filterable
- [x] [Review][Defer] Schema registry check-then-act race on cache miss [`DynamicDataEndpoints.cs:ListRecordsHandler`] — deferred, pre-existing: two concurrent requests observing `entry is null` both parse from DB and both call `Populate`; same pattern as DdlEmitter; `ISchemaRegistry.Populate` should be checked for idempotency under concurrent writes; pre-existing known deferred item from Story 5.6 review
- [x] [Review][Defer] Duplicate sort column not deduplicated [`DynamicQueryBuilder.cs:ParseSort`] — deferred: `sort=created_at:asc,created_at:desc` produces `ORDER BY "created_at" ASC, "created_at" DESC` (no error, ambiguous ordering); spec AC-2 does not prohibit this; low impact, address in a later story if clients report confusing behaviour
- [x] [Review][Defer] Schema registry poisoned with zero-column entry when `ComponentSchemaVersions` row is absent [`DynamicDataEndpoints.cs:ListRecordsHandler`] — deferred: if `RootElement` is null (row missing), `ParseFull(null)` returns `([], [])`, the registry is populated with an empty entry, and the handler returns 200 with only system columns; intentional design decision per Dev Agent Record; should return 404 or 500 for data-integrity violations but change is non-trivial
- [x] [Review][Defer] User fieldKey matching a system column name could yield duplicate column in SELECT [`DynamicQueryBuilder.cs:AppendSelectColumns`] — deferred: depends on whether DdlEmitter guards against system column name collisions during provisioning; if provisioning fails, the 404 guard prevents this from being reachable; verify DdlEmitter rejects or skips user columns that match `SystemColumnNames`
- [x] [Review][Defer] Sort parameter lacks camelCase→PG translation that filter parameter has [`DynamicDataEndpoints.cs`] — deferred: `filter[isDeleted]` works (camelCase mapped) but `sort=isDeleted:asc` returns 422; asymmetric API contract; spec uses PG names for sort so this is not a spec violation; document in API guide or add symmetrical translation in a later story
- [x] [Review][Defer] pageSize out-of-range silently corrected to 25 [`DynamicDataEndpoints.cs:ListRecordsHandler`] — deferred: consistent with `AuditEndpoints.cs` pattern cited in Dev Notes; response `pageSize` echoes the corrected value, not the client-supplied value; acceptable for v1 per project convention
- [x] [Review][Defer] Integration test hardcodes `publishedVersion = 2` [`DynamicCrudIntegrationTests.cs:CreateAndPublishDesignerWithFieldsAsync`] — deferred: assumes the designer service always auto-creates version 1 (Draft) so the first manual save is version 2; fragile if versioning logic changes; replace with a dynamic version query in a future test-hardening pass

---

## Dev Notes

### Architecture compliance — critical constraints

**1. SafeIdentifier is mandatory for any user-supplied identifier in SQL**
The `designerId` route value is user-supplied and MUST pass through `SafeIdentifier.TryCreate` before being interpolated into the table name in SQL. System column names (`id`, `created_at`, etc.) are hardcoded by the system and are safe to quote directly. User fieldKeys from the schema registry were validated by `SafeIdentifier` at provisioning time (Story 5.1), so they are also safe to use with double-quoting in the SELECT and WHERE clauses.

Never write: `$"SELECT * FROM {designerId}"` — always use `$"SELECT * FROM \"{safeId.Value}\""`.

**2. commandTimeout = 5 on every Dapper call** (Decision 1.6)
The `DbConnectionFactory.DdlCommandTimeoutSeconds = 60` constant is for DDL operations only. CRUD queries MUST use timeout = 5. Pass via `CommandDefinition`:
```csharp
new CommandDefinition(sql, parameters, commandTimeout: 5, cancellationToken: ct)
```
There is no CrudCommandTimeoutSeconds constant — hardcode 5 at the call site.

**3. Option C hybrid JSON serialization** (AR-46)
The `DynamicRecord` JSON converter MUST serialize system column PG names as camelCase and user fieldKeys verbatim. Failing to implement this means the frontend receives `is_deleted` instead of `isDeleted` for the standard delete flag, breaking the `DynamicRecord` TypeScript interface.

The converter lives in `DynamicRecord.cs` and must be registered in `Program.cs` where `builder.Services.ConfigureHttpJsonOptions` or `builder.Services.AddControllers().AddJsonOptions(...)` is configured. Check how the existing `PagedResult<T>` is returned to find the current JSON serialization setup — the converter should be added to the same options.

**4. Schema registry is the column source of truth** (Decision 1.4)
Do NOT use `information_schema.columns` to build the SELECT column list at query time — that is a pg_attribute round-trip on every request. The schema registry cache was built specifically to avoid this. If `schemaRegistry.TryGet()` returns null (cache evicted), re-populate from EF (`db.ComponentSchemaVersions`) and `RootElementParser` — do NOT fall back to `SELECT *`.

**5. Dapper dynamic → DynamicRecord mapping**
`connection.QueryAsync(...)` returns `IEnumerable<dynamic>`. Each dynamic result is a `DapperRow` which implements `IDictionary<string, object>`. Convert via:
```csharp
var records = rows
    .Select(row => new DynamicRecord(
        ((IDictionary<string, object>)row)
            .ToDictionary(
                kvp => kvp.Key,
                kvp => (object?)kvp.Value,
                StringComparer.Ordinal)))
    .ToArray();
```
Do not use `QueryAsync<DynamicRecord>` — Dapper cannot deserialize into a type with an arbitrary dictionary structure.

**6. `ColumnDefinition.IsRepeater` is absent in the current codebase**
The architecture document lists `IsRepeater: bool` on `ColumnDefinition`, but the actual `ColumnDefinition.cs` (Story 5.3) only has `ColumnName`, `PgType`, `ComponentType`, `IsImage`. Do NOT add `IsRepeater` in this story — it is needed for Story 6.5 (soft-delete cascade) and will be added there. Story 6.1 only needs `ColumnName` from the registry entry.

**7. `filter[key]` query string parsing**
ASP.NET Minimal APIs do not auto-bind `filter[key]=value` into a dictionary out of the box. Parse it manually from `HttpContext.Request.Query`:
```csharp
var filters = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var (key, value) in httpContext.Request.Query)
{
    if (key.StartsWith("filter[", StringComparison.Ordinal) && key.EndsWith(']'))
    {
        var fieldKey = key[7..^1];  // strip "filter[" and "]"
        filters[fieldKey] = value.ToString();
    }
}
```
The handler receives `HttpContext` or `IHttpContextAccessor` to access `Request.Query`. The simplest approach: inject `HttpContext` via the delegate parameter directly (Minimal API supports it).

**8. Rate limiting — already applied**
`Program.cs` registers the `/api/data/{designerId}` group with `RequireRateLimiting("data-read")` (300 req/min per user). The GET handler inherits this. Do NOT add a second `.RequireRateLimiting(...)` call to the mapped GET endpoint.

**9. EF + Dapper connection discipline** (Decision 1.6)
For CRUD queries, open a raw `NpgsqlConnection` via `DbConnectionFactory.CreateOpenConnectionAsync` — do NOT use `AppForgeDbContext.Database.GetDbConnection()`. EF owns static schema reads (menu lookup, schema version lookup); Dapper owns all dynamic table queries. The EF queries (schema resolution) run first via the injected `AppForgeDbContext`; then the Dapper queries run on their own fresh connection.

**10. Pagination defaults and clamping**
Follow the existing convention in `AuditEndpoints.cs`:
```csharp
if (page < 1) page = 1;
if (pageSize is < 1 or > 100) pageSize = 25;
```
Apply this at the start of the handler before any DB calls.

---

### File locations — new files

| New file | Path |
|---|---|
| `DynamicRecord.cs` | `src/AppForge.Api/Features/DynamicCrud/DynamicRecord.cs` |
| `DynamicQueryBuilder.cs` | `src/AppForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs` |
| `DynamicQueryBuilderTests.cs` | `src/AppForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs` |
| `DynamicCrudIntegrationTests.cs` | `src/AppForge.Api.Tests/Features/DynamicCrud/DynamicCrudIntegrationTests.cs` |
| `recordListApi.ts` | `web/src/features/data-entry/recordListApi.ts` |
| `useRecordList.ts` | `web/src/features/data-entry/useRecordList.ts` |

### File locations — modified files

| Modified file | Change |
|---|---|
| `DynamicDataEndpoints.cs` | Add `MapGet("/", ListRecordsHandler)` with `.RequirePermission("read")` |
| `Program.cs` | Register `DynamicRecordJsonConverter` in JSON options |

---

### Schema registry and EF dependency on reads

The GET handler injects both `AppForgeDbContext` (scoped — gets a fresh scope per request via DI) and `ISchemaRegistry` (singleton). The EF context is used for two reads before the Dapper query:

1. **Binding check** — find the highest `BoundVersion` for a successful Menu binding:
   ```csharp
   var boundVersion = await db.Menus
       .AsNoTracking()
       .Where(m => m.DesignerId == designerId.Value
                && m.ProvisioningStatus == "Success"
                && m.BoundVersion != null)
       .OrderByDescending(m => m.BoundVersion)
       .Select(m => (int?)m.BoundVersion)
       .FirstOrDefaultAsync(ct);
   if (boundVersion is null)
       return Results.Problem(...404 TABLE_NOT_PROVISIONED...);
   ```

2. **Schema registry cache miss** — if `schemaRegistry.TryGet()` returns null:
   ```csharp
   var rootElement = await db.ComponentSchemaVersions
       .AsNoTracking()
       .Where(v => v.DesignerId == designerId.Value && v.Version == boundVersion.Value)
       .Select(v => v.RootElement)
       .FirstOrDefaultAsync(ct);
   if (rootElement is null) return Results.Problem(...404 TABLE_NOT_PROVISIONED...);
   var columns = RootElementParser.Parse(rootElement);
   var entry = new SchemaRegistryEntry(designerId.Value, boundVersion.Value, columns, ..., DateTimeOffset.UtcNow);
   schemaRegistry.Populate(entry);
   ```
   `RootElementParser.Parse` is already tested (Story 5.3). `ChildRepeaterDesignerIds` can be populated from the parser output — check `RootElementParser.cs` for how it extracts repeater child IDs.

3. After both schema registry reads (or cache hit), the `AppForgeDbContext` is no longer needed and the Dapper connection is opened. The EF context and Dapper connection do NOT share a transaction for reads (read isolation is fine).

---

### `DynamicRecord` JSON serialization detail

The custom `JsonConverter<DynamicRecord>` must handle these PostgreSQL types that Dapper returns as .NET types:
- `TEXT` → `string`
- `NUMERIC` → `decimal` (Npgsql default for NUMERIC without scale)
- `BOOLEAN` → `bool`
- `TIMESTAMPTZ` → `DateTime` (UTC, Npgsql legacy mode) or `DateTimeOffset`
- `UUID` → `Guid`
- `JSONB` → `string` (raw JSON string from Npgsql)

When writing values in the converter, use `JsonSerializer.Serialize(writer, value, options)` so ASP.NET's configured serializers (e.g., camelCase options) handle each type correctly. The converter only needs to be responsible for property NAMES (Option C hybrid mapping), not value serialization.

Check `Program.cs` for `ConfigureHttpJsonOptions` / `AddJsonOptions` to understand where to register the converter. The existing JSON options use default ASP.NET settings. The converter should be added via:
```csharp
builder.Services.ConfigureHttpJsonOptions(opts =>
    opts.SerializerOptions.Converters.Add(new DynamicRecordJsonConverter()));
```
If `ConfigureHttpJsonOptions` is already in `Program.cs`, append to it; do not create a second call.

---

### Test patterns from previous stories

**Integration test class structure** (mirrors `ProvisioningIntegrationTests.cs`):
- `IClassFixture<PostgresFixture>` — shared Postgres container per class
- `IAsyncLifetime` — `InitializeAsync` for TRUNCATE + drop dynamic tables + seed roles/users; `DisposeAsync` disposes `_factory` and `_client`
- Helper methods for login, designer creation, menu creation, binding — copy the `static` pattern from `ProvisioningRecoveryIntegrationTests.cs` (which parameterizes `HttpClient` instead of relying on class-level fields) OR use the class-level pattern from `ProvisioningIntegrationTests.cs`
- For this story, the class-level pattern (`_factory`, `_client`) is fine since each test uses the same factory without restart simulation

**Helper needed but not yet in DynamicCrud tests**: `CreateAndPublishDesignerWithFieldsAsync` from `ProvisioningIntegrationTests.cs` — copy or reference. This creates a designer with actual user columns (e.g., a `TextInput` with `fieldKey: "title"`). You NEED real user columns to test sort/filter by user fieldKey. The zero-column helper `CreateAndPublishDesignerAsync` from `ProvisioningRecoveryIntegrationTests.cs` is NOT sufficient for AC-2/3 tests.

**Inserting test records**: At the integration test level, use Npgsql directly to INSERT rows into the dynamic table:
```csharp
await using var conn = new NpgsqlConnection(connectionString);
await conn.OpenAsync();
await conn.ExecuteAsync($@"
    INSERT INTO ""{designerId}"" (id, created_at, created_by, updated_at, updated_by, is_deleted, title)
    VALUES (@id, NOW(), @userId, NOW(), @userId, false, @title)",
    new { id = Guid.NewGuid(), userId = _testUserId, title = "Test Record" });
```
Use the connection string from `PostgresFixture` (the same source as `WebApplicationFactory`).

---

### Frontend query key convention (AR-48)

The query key for the record list hook must be:
```ts
queryKey: ['data', designerId, 'list', params]
```
This allows later stories to invalidate all list queries for a designer via:
```ts
queryClient.invalidateQueries({ queryKey: ['data', designerId] })
```

`staleTime: 0` is intentional — unlike the audit log (append-only, 30s staleTime in Story 5.7), record data is mutable and should always be re-fetched. `keepPreviousData` (via `placeholderData: keepPreviousData`) prevents the list from flashing empty on page/filter changes.

---

### `program.cs` — JSON options registration location

Search `Program.cs` for `ConfigureHttpJsonOptions` or `AddJsonOptions`. Currently (end of Story 5.8), there is no explicit JSON configuration call — ASP.NET defaults are used. Add:
```csharp
builder.Services.ConfigureHttpJsonOptions(opts =>
{
    opts.SerializerOptions.Converters.Add(new DynamicRecordJsonConverter());
});
```
Place this near the top of the service registration block (before `builder.Build()`). Check if any existing code depends on the default serializer behavior — the converter is type-specific to `DynamicRecord` so it does not affect other responses.

---

### References

- [Source: `src/AppForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs`] — stub to modify; currently just returns `group`
- [Source: `src/AppForge.Api/Features/SchemaRegistry/ISchemaRegistry.cs`] — `TryGet(designerId, version)`, `Populate(entry)`, `InvalidateDesigner(designerId)` — full interface
- [Source: `src/AppForge.Api/Features/SchemaRegistry/ColumnDefinition.cs`] — has `ColumnName`, `PgType`, `ComponentType`, `IsImage` (no `IsRepeater` yet)
- [Source: `src/AppForge.Api/Features/SchemaRegistry/SchemaRegistryEntry.cs`] — `(DesignerId, Version, Columns, ChildRepeaterDesignerIds, CachedAt)` positional record
- [Source: `src/AppForge.Api/Features/SchemaRegistry/RootElementParser.cs`] — `Parse(string rootElement)` returns column list; check its return type and how `ChildRepeaterDesignerIds` is extracted
- [Source: `src/AppForge.Api/Infrastructure/Persistence/DbConnectionFactory.cs`] — `CreateOpenConnectionAsync(CancellationToken)`, `DdlCommandTimeoutSeconds = 60`; CRUD timeout is 5 (hardcode at call site)
- [Source: `src/AppForge.Api/Common/PagedResult.cs`] — `PagedResult<T>(Data, Total, Page, PageSize)` with `TotalPages` computed property — already exists, do NOT recreate
- [Source: `src/AppForge.Api/Common/Endpoints/RouteGroupExtensions.cs`] — `RequirePermission(string action)` extension method — MUST call on the mapped GET handler
- [Source: `src/AppForge.Api/Features/Provisioning/DdlEmitter.cs:50-53`] — `SystemColumnNames` set: `{ "id", "created_at", "created_by", "updated_at", "updated_by", "is_deleted", "cascade_event_id" }` — use this set (access via `DdlEmitter.SystemColumnNames`) for the SELECT column list rather than duplicating it
- [Source: `src/AppForge.Api/Features/Audit/AuditEndpoints.cs`] — handler pattern: parameter binding, clamping, `Results.Problem(...)` error returns, `Results.Ok(...)` success
- [Source: `src/AppForge.Api/Features/Designer/SafeIdentifier.cs`] — `SafeIdentifier.TryCreate(raw, out result, out error)` two-arg overload; `result.Value` gives the unquoted string; quote as `$"\"{result.Value}\""` in SQL
- [Source: `src/AppForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`] — integration test class shape, `PostgresFixture`, `InitializeAsync` pattern, `LoginAsync`, `CreateMenuViaApiAsync`, `PutBindingAsync`, `PollUntilTerminalAsync`
- [Source: `src/AppForge.Api.Tests/Features/Provisioning/ProvisioningIntegrationTests.cs`] — `CreateAndPublishDesignerWithFieldsAsync` — use this helper (not the zero-column version) for tests that need sortable/filterable user columns
- [Architecture: Decision 1.4] — Schema registry cache: lazy population from `component_schemas` on first CRUD request; `IMemoryCache`-backed; `TryGet` + `Populate` are the two entry points
- [Architecture: Decision 1.6] — EF + Dapper separation; `commandTimeout = 5` for CRUD queries; `commandTimeout = 60` for DDL
- [Architecture: AR-46 (Option C hybrid)] — system columns camelCase in JSON response; user fieldKeys verbatim; custom `JsonConverter<DynamicRecord>`
- [Architecture: AR-21] — `PagedResult<T>` shape; `pageSize ≤ 100`, default 25
- [Architecture: AR-48] — TanStack Query key: `['data', designerId, 'list', params]`
- [Architecture: AR-49] — `keepPreviousData` for page-transition UX; `staleTime: 0` for mutable data (unlike audit log which uses 30 s)
- [Architecture: Decision 3.5] — endpoint group `/api/data/{designerId}` with `RequireAuth()` and `RequireRateLimiting("data-read")` already in `Program.cs` at line ~490
- [Architecture: AR-4] — SafeIdentifier is mandatory for all dynamic SQL identifiers
- [`_bmad-output/implementation-artifacts/deferred-work.md`] — no items from prior stories directly impact Story 6.1, but the `SchemaRegistry.InvalidateDesigner` race (from 5.6 review) is accepted; the lazy re-population in the GET handler handles the consequent cache miss cleanly

---

## Dev Agent Record

### Agent Model Used

Claude Opus 4.7 (1M context)

### Debug Log References

- All 16 unit tests (`DynamicQueryBuilderTests`) pass.
- All 10 integration tests (`DynamicCrudIntegrationTests`) pass against PostgreSQL 17 testcontainer.
- Full backend suite: 434 / 434 pass (up from 407 in Story 5.8 — +27 from this story plus a +1 fix on a pre-existing CA2234 in `ProvisioningRecoveryIntegrationTests.cs` that blocked the test project build).
- Frontend: `recordListApi.ts` and `useRecordList.ts` pass `npx tsc --noEmit` and `npx eslint`. Two pre-existing failing tests in `ReorderableMenuList.test.tsx` are unrelated (confirmed by stash-pop comparison).

### Completion Notes List

- AR-46 Option C hybrid serialization implemented via `DynamicRecordJsonConverter` registered through `ConfigureHttpJsonOptions` in `Program.cs`. The converter only rewrites property NAMES — value serialization is delegated to the configured options so Guid/DateTime/bool/decimal/string land through the ASP.NET default pipeline.
- `DynamicQueryBuilder` is a pure static class with no DI dependencies, which made unit testing straightforward and kept the SQL assembly inspectable. The system column SELECT order is hardcoded for determinism (HashSet iteration is undefined; this matters for human-readable SQL and test stability, though Dapper does not depend on column order).
- `commandTimeout: 5` is passed explicitly to every Dapper call at the call site per Decision 1.6 — no constant added to `DbConnectionFactory` because there is no shared "CRUD timeout" yet (Stories 6.2-6.6 will reuse the same literal).
- Filter coercion logic in `DynamicQueryBuilder.CoerceFilterValue` only types BOOLEAN/UUID system columns. User fieldKey values stay as strings; PostgreSQL's implicit cast handles `WHERE numeric_col = '25'` and similar. If a future story finds the implicit cast insufficient (e.g., index miss on date filters), the coercion can be extended without touching the handler.
- The handler treats a `boundVersion` of null as 404 `TABLE_NOT_PROVISIONED` (AC-4), but a `RootElement` of null (empty designer) parses to zero user columns and the SELECT proceeds with system columns only — matches the empty-designer provisioning case from Story 5.3.
- Schema registry cache miss path repopulates from `db.ComponentSchemaVersions` via `RootElementParser.ParseFull` and writes back through `schemaRegistry.Populate`, matching `DdlEmitter`'s own populate call. The `DeferredItem` about the `InvalidateDesigner` race is consequently a non-issue for GET — a cache miss after invalidation simply re-fetches.
- One pre-existing analyzer error (CA2234) in `ProvisioningRecoveryIntegrationTests.cs` was blocking the test project build. Fixed by passing `new Uri("/", UriKind.Relative)` to `HttpClient.GetAsync`. This is not directly part of Story 6.1 but was required to land any new test in the project.

### File List

**New files:**
- `src/AppForge.Api/Features/DynamicCrud/DynamicRecord.cs`
- `src/AppForge.Api/Features/DynamicCrud/DynamicQueryBuilder.cs`
- `src/AppForge.Api.Tests/Features/DynamicCrud/DynamicQueryBuilderTests.cs`
- `src/AppForge.Api.Tests/Features/DynamicCrud/DynamicCrudIntegrationTests.cs`
- `web/src/features/data-entry/recordListApi.ts`
- `web/src/features/data-entry/useRecordList.ts`

**Modified files:**
- `src/AppForge.Api/Features/DynamicCrud/DynamicDataEndpoints.cs` — added `MapGet("/")` → `ListRecordsHandler` with `.RequirePermission("read")`
- `src/AppForge.Api/Program.cs` — registered `DynamicRecordJsonConverter` in `ConfigureHttpJsonOptions`
- `src/AppForge.Api.Tests/Features/Provisioning/ProvisioningRecoveryIntegrationTests.cs` — pre-existing CA2234 fix (`HttpClient.GetAsync` overload)
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — `6-1` → `review`, `epic-6` → `in-progress`

## Change Log

| Date | Change |
|---|---|
| 2026-05-26 | Story 6.1 implemented — GET /api/data/{designerId} list endpoint with pagination, filtering, sorting; DynamicRecord JsonConverter; frontend client and TanStack Query hook. 434/434 backend tests pass. |

