using System.Data;
using Microsoft.EntityFrameworkCore;

namespace SimpleRadius.Data;

/// <summary>
/// Adds columns that were introduced after a database was first created.
///
/// The schema is created with <c>EnsureCreated</c>, which never alters an existing database, and the
/// project has no full EF Core migration history yet. So when a new nullable column is added to an entity,
/// an existing database is missing it and every query that selects it fails with "no such column". This
/// runs the additive <c>ALTER TABLE ADD COLUMN</c> statements by hand, guarded so it is a no-op on a fresh
/// database that already has them. It only ever adds nullable columns — never drops, renames or retypes —
/// so it is safe to run on every startup and cannot lose data.
/// </summary>
public static class SchemaUpgrader
{
    // (table, column, SQL type). Table and column names are fixed literals, not user input.
    private static readonly (string Table, string Column, string Type)[] AdditiveColumns =
    [
        ("ClientDevices", "Notes", "TEXT"),
        ("NetworkAccessServers", "Notes", "TEXT"),
        ("VlanDefinitions", "Notes", "TEXT"),
    ];

    public static async Task ApplyAsync(RadiusDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
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
