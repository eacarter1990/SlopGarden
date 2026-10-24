using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using SlopGarden.Server.Data;

namespace SlopGarden.Server.Infrastructure;

/// <summary>Encrypts model API keys, search keys and webhook URLs at rest. Values are never returned to the browser.</summary>
public sealed class SecretStore(SlopDbContext db, IDataProtectionProvider dp)
{
    private readonly IDataProtector _protector = dp.CreateProtector("SlopGarden.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);

    public async Task<Guid> SetAsync(Guid? existingId, string name, string purpose, string value, CancellationToken ct = default)
    {
        var secret = existingId is { } id ? await db.Secrets.FindAsync([id], ct) : null;
        if (secret is null)
        {
            secret = new Data.Secret { Name = $"{name}:{Guid.NewGuid():N}", Purpose = purpose };
            db.Secrets.Add(secret);
        }
        secret.Ciphertext = Protect(value);
        await db.SaveChangesAsync(ct);
        return secret.Id;
    }

    public async Task<string?> GetAsync(Guid? id, CancellationToken ct = default)
    {
        if (id is null) return null;
        var secret = await db.Secrets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (secret is null) return null;
        try
        {
            return Unprotect(secret.Ciphertext);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public async Task DeleteAsync(Guid? id, CancellationToken ct = default)
    {
        if (id is null) return;
        await db.Secrets.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
    }
}
