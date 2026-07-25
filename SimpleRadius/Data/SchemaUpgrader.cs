using System.Data;
using Microsoft.EntityFrameworkCore;

namespace SimpleRadius.Data;

/// <summary>
/// Brings a database created by an earlier build up to date: creates tables and adds columns introduced
/// since it was made.
///
/// The schema is created with <c>EnsureCreated</c>, which never touches an existing database, and the
/// project has no full EF Core migration history yet. So a new entity's table, or a new column on an
/// existing entity, is missing on an older database and every query for it fails ("no such table/column").
/// This creates missing tables and adds missing nullable columns by hand, all guarded and idempotent, so
/// it is a no-op on a fresh database and safe to run on every startup. It only ever adds — never drops,
/// renames or retypes — so it cannot lose data. A fresh database still gets its schema from the model via
/// EnsureCreated; this only fills the gap for one already on disk.
/// </summary>
public static class SchemaUpgrader
{
    // Table plus the DDL that creates it, matching what EF Core generates for the entity. IF NOT EXISTS
    // keeps it idempotent, and a fresh database already has the table from EnsureCreated.
    private static readonly (string Table, string CreateSql)[] AdditiveTables =
    [
        ("SsidVlanRules", """
            CREATE TABLE IF NOT EXISTS "SsidVlanRules" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SsidVlanRules" PRIMARY KEY AUTOINCREMENT,
                "Ssid" TEXT NOT NULL,
                "VlanDefinitionId" INTEGER NOT NULL,
                "Notes" TEXT NULL,
                "CreatedUtc" TEXT NOT NULL,
                "UpdatedUtc" TEXT NOT NULL,
                CONSTRAINT "FK_SsidVlanRules_VlanDefinitions_VlanDefinitionId" FOREIGN KEY ("VlanDefinitionId") REFERENCES "VlanDefinitions" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SsidVlanRules_Ssid" ON "SsidVlanRules" ("Ssid");
            CREATE INDEX IF NOT EXISTS "IX_SsidVlanRules_VlanDefinitionId" ON "SsidVlanRules" ("VlanDefinitionId");
            """),
    ];

    // (table, column, SQL type). Table and column names are fixed literals, not user input.
    private static readonly (string Table, string Column, string Type)[] AdditiveColumns =
    [
        ("ClientDevices", "Notes", "TEXT"),
        ("NetworkAccessServers", "Notes", "TEXT"),
        ("VlanDefinitions", "Notes", "TEXT"),
        ("VlanDefinitions", "Group", "TEXT"),
        ("AccountingSessions", "Ssid", "TEXT"),
        ("AccountingSessions", "NasIdentifier", "TEXT"),
        ("AccountingSessions", "NasPortType", "TEXT"),
    ];

    public static async Task ApplyAsync(RadiusDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        foreach (var (table, createSql) in AdditiveTables)
        {
            if (await GetColumnsAsync(db, table, cancellationToken) is { Count: > 0 })
            {
                continue;
            }

            await db.Database.ExecuteSqlRawAsync(createSql, cancellationToken);
            logger.LogInformation("Created missing table {Table} to bring the database up to date", table);
        }

        foreach (var (table, column, type) in AdditiveColumns)
        {
            var existing = await GetColumnsAsync(db, table, cancellationToken);

            // If the table itself is absent, EnsureCreated will have built it complete from the model.
            if (existing.Count == 0 || existing.Contains(column))
            {
                continue;
            }

            await db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type} NULL;",
                cancellationToken);

            logger.LogInformation("Added missing column {Table}.{Column} to bring the database up to date", table, column);
        }
    }

    private static async Task<HashSet<string>> GetColumnsAsync(RadiusDbContext db, string table, CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk.
                columns.Add(reader.GetString(1));
            }
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }

        return columns;
    }
}
