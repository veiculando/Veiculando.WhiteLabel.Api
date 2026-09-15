using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Veiculando.WhiteLabel.Api.Middleware;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests;

public sealed class AppLoginAttemptGuardTests
{
    [Fact]
    public async Task Rajada_distribuida_por_identidade_nao_ultrapassa_limite_nem_mistura_tenants()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var guard = new AppLoginAttemptGuard(cache);
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => guard.Allow(1, "alvo@exemplo.com"))));
        results.Count(allowed => allowed).Should().Be(10);
        guard.Allow(1, "alvo@exemplo.com").Should().BeFalse();
        guard.Allow(2, "alvo@exemplo.com").Should().BeTrue();
        guard.Allow(1, "outra@exemplo.com").Should().BeTrue();
    }
}
