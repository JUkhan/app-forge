using Npgsql;

namespace AppForge.Api.Infrastructure.Persistence;

// Story 11.3 (FR-72 / AR-63) — opens connections from the dedicated, least-privileged
// `appforge_preview` pool used exclusively for read-only query previews. Separate from
// DbConnectionFactory (the privileged `appforge` pool) so preview execution runs as a
// principal that can only SELECT public tables (GRANT/REVOKE set by the dataset migration).
internal interface IPreviewConnectionFactory
{
    Task<NpgsqlConnection> CreateOpenConnectionAsync(CancellationToken ct = default);
}
