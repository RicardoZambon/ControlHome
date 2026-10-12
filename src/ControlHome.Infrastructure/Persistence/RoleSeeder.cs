using ControlHome.Domain.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ControlHome.Infrastructure.Persistence;

/// <summary>Ensures the three household roles exist (idempotent).</summary>
internal static class RoleSeeder
{
    public static async Task SeedAsync(ControlHomeDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.Roles.Select(r => r.NormalizedName).ToListAsync(cancellationToken);
        var added = false;
        foreach (var role in Roles.All)
        {
            // Same normalization as Identity's default UpperInvariantLookupNormalizer.
            var normalized = role.Normalize().ToUpperInvariant();
            if (existing.Contains(normalized))
            {
                continue;
            }

            db.Roles.Add(new IdentityRole<Guid>(role)
            {
                Id = Guid.CreateVersion7(),
                NormalizedName = normalized,
                ConcurrencyStamp = Guid.NewGuid().ToString(),
            });
            added = true;
        }

        if (added)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
