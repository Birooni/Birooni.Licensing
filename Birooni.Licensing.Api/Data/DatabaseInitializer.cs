using Microsoft.EntityFrameworkCore;

namespace Birooni.Licensing.Api.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeSchemaAsync(IServiceProvider serviceProvider, ILogger logger)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicensingDbContext>();

            // Ensure columns exist on licenses
            var sql = """
                ALTER TABLE licenses ADD COLUMN IF NOT EXISTS license_mode VARCHAR(50) NOT NULL DEFAULT 'Individual';
                ALTER TABLE licenses ADD COLUMN IF NOT EXISTS company VARCHAR(200);
                ALTER TABLE licenses ADD COLUMN IF NOT EXISTS allowed_domain VARCHAR(100);
                ALTER TABLE licenses ADD COLUMN IF NOT EXISTS concurrent_seats INT;

                CREATE INDEX IF NOT EXISTS idx_licenses_allowed_domain ON licenses(allowed_domain);
                CREATE INDEX IF NOT EXISTS idx_licenses_mode ON licenses(license_mode);

                CREATE TABLE IF NOT EXISTS product_releases (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    product VARCHAR(100) NOT NULL,
                    version VARCHAR(50) NOT NULL,
                    revit_version VARCHAR(50) NOT NULL,
                    download_url VARCHAR(500) NOT NULL,
                    release_notes TEXT,
                    is_mandatory BOOLEAN NOT NULL DEFAULT FALSE,
                    checksum_sha256 VARCHAR(128),
                    released_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS idx_product_releases_prod_revit ON product_releases(product, revit_version);
                CREATE INDEX IF NOT EXISTS idx_product_releases_released ON product_releases(released_at);

                CREATE TABLE IF NOT EXISTS floating_sessions (
                    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    license_id UUID NOT NULL REFERENCES licenses(id) ON DELETE CASCADE,
                    device_id VARCHAR(200) NOT NULL,
                    user_name VARCHAR(200),
                    plugin_version VARCHAR(50),
                    started_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    last_heartbeat_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    expires_at TIMESTAMPTZ NOT NULL,
                    is_active BOOLEAN NOT NULL DEFAULT TRUE
                );

                CREATE INDEX IF NOT EXISTS idx_floating_sessions_lic ON floating_sessions(license_id);
                CREATE INDEX IF NOT EXISTS idx_floating_sessions_lic_dev ON floating_sessions(license_id, device_id);
                CREATE INDEX IF NOT EXISTS idx_floating_sessions_exp ON floating_sessions(expires_at);
                """;

            await db.Database.ExecuteSqlRawAsync(sql);
            logger.LogInformation("Database schema migration verified successfully.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Database schema migration notice (columns/tables may already exist or running in offline mode).");
        }
    }
}
