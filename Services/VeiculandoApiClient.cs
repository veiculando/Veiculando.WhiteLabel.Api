using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services
{
    public interface IVeiculandoApiClient
    {
        Task<HttpClient> GetAuthenticatedClientAsync();
    }

    /// <summary>
    /// Cliente autenticado para a API do Veiculando Core, usando a conta de
    /// servico vinculada ao tenant resolvido por Host.
    /// </summary>
    public class VeiculandoApiClient : IVeiculandoApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly ISeedAccountResolver _seedAccountResolver;
        private readonly ITenantContext _tenant;
        private readonly ILogger<VeiculandoApiClient> _logger;

        public VeiculandoApiClient(
            HttpClient httpClient,
            IMemoryCache cache,
            ISeedAccountResolver seedAccountResolver,
            ITenantContext tenant,
            ILogger<VeiculandoApiClient> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _seedAccountResolver = seedAccountResolver;
            _tenant = tenant;
            _logger = logger;
        }

        public async Task<HttpClient> GetAuthenticatedClientAsync()
        {
            ValidarTenantResolvido();
            var cacheKey = $"SeedToken:{_tenant.AfiliadaId}";

            if (!_cache.TryGetValue(cacheKey, out string token))
                token = await AuthenticateSeedUserAsync(cacheKey);

            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
            return _httpClient;
        }

        private async Task<string> AuthenticateSeedUserAsync(string cacheKey)
        {
            var options = _seedAccountResolver.Resolve();

            var conteudo = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("Email", options.Email),
                new KeyValuePair<string, string>("Senha", options.Password)
            });

            var response = await _httpClient.PostAsync(
                "api/account/login-usuario-afiliada", conteudo);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Falha ao autenticar a conta de servico no core: {Status}",
                    response.StatusCode);
                throw new InvalidOperationException(
                    "Falha ao autenticar a conta de servico no Veiculando Core.");
            }

            var responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);

            if (!TryGetProperty(doc.RootElement, "token", out var tokenElement))
                throw new InvalidOperationException(
                    "Token nao encontrado na resposta de login do core.");

            var token = tokenElement.GetString();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException(
                    "Token vazio na resposta de login do core.");

            if (!TryGetAfiliadaId(doc.RootElement, token, out var afiliadaAutenticada) ||
                afiliadaAutenticada != _tenant.AfiliadaId)
            {
                _logger.LogError(
                    "Conta de servico recusada por divergencia de tenant. Esperado: {AfiliadaId}",
                    _tenant.AfiliadaId);
                throw new InvalidOperationException(
                    "Conta de servico nao pertence ao tenant resolvido.");
            }

            var expiraEmMinutos = 300;
            if (TryGetProperty(doc.RootElement, "expires", out var expElement) &&
                expElement.TryGetInt32(out var minutos) &&
                minutos > 5)
            {
                expiraEmMinutos = minutos;
            }

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(expiraEmMinutos - 5));

            _cache.Set(cacheKey, token, cacheOptions);
            return token;
        }

        private void ValidarTenantResolvido()
        {
            if (!_tenant.Resolvido || _tenant.AfiliadaId <= 0)
                throw new InvalidOperationException(
                    "Tenant nao resolvido para chamada ao Veiculando Core.");
        }

        private static bool TryGetAfiliadaId(
            JsonElement root,
            string token,
            out int afiliadaId)
        {
            if (TryGetProperty(root, "afiliadaId", out var property) &&
                TryGetInt32(property, out afiliadaId))
            {
                return true;
            }

            return TryGetJwtAfiliadaId(token, out afiliadaId);
        }

        private static bool TryGetJwtAfiliadaId(string token, out int afiliadaId)
        {
            afiliadaId = 0;
            var partes = token.Split('.');
            if (partes.Length < 2)
                return false;

            try
            {
                var payload = partes[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(
                    payload.Length + ((4 - payload.Length % 4) % 4), '=');

                var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                using var doc = JsonDocument.Parse(json);

                return TryGetProperty(doc.RootElement, "AfiliadaId", out var claim) &&
                       TryGetInt32(claim, out afiliadaId);
            }
            catch (FormatException)
            {
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryGetProperty(
            JsonElement element,
            string name,
            out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static bool TryGetInt32(JsonElement element, out int value)
        {
            if (element.ValueKind == JsonValueKind.Number)
                return element.TryGetInt32(out value);

            if (element.ValueKind == JsonValueKind.String)
                return int.TryParse(element.GetString(), out value);

            value = 0;
            return false;
        }
    }
}
