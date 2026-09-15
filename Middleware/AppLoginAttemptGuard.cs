using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Veiculando.WhiteLabel.Api.Middleware;

/// <summary>Limite por identidade e tenant, independente de IP; chave nunca contém o e-mail.</summary>
public sealed class AppLoginAttemptGuard
{
    private readonly IMemoryCache _cache;
    private readonly object _sync = new();
    public AppLoginAttemptGuard(IMemoryCache cache) => _cache = cache;
    public bool Allow(int tenant, string email)
    {
        var key = "AppLogin:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenant}:{email}")));
        lock (_sync)
        {
            var counter = _cache.GetOrCreate(key, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
                return new Counter();
            });
            if (counter.Value >= 10) return false;
            counter.Value++;
            return true;
        }
    }
    private sealed class Counter { public int Value; }
}
