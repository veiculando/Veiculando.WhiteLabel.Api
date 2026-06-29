using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veiculando.WhiteLabel.Api.Configurations;

namespace Veiculando.WhiteLabel.Api.Services
{
    public interface IVeiculandoApiClient
    {
        Task<HttpClient> GetAuthenticatedClientAsync();
    }

    public class VeiculandoApiClient : IVeiculandoApiClient
    {
        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly SeedAccountOptions _options;
        private readonly ILogger<VeiculandoApiClient> _logger;

        private const string TokenCacheKey = "VeiculandoApi_SeedToken";

        public VeiculandoApiClient(
            HttpClient httpClient, 
            IMemoryCache cache, 
            IOptions<SeedAccountOptions> options,
            ILogger<VeiculandoApiClient> logger)
        {
            _httpClient = httpClient;
            // Configurar a BaseAddress via DI no Startup
            _cache = cache;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<HttpClient> GetAuthenticatedClientAsync()
        {
            if (!_cache.TryGetValue(TokenCacheKey, out string token))
            {
                token = await AuthenticateSeedUserAsync();
            }

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return _httpClient;
        }

        private async Task<string> AuthenticateSeedUserAsync()
        {
            var loginData = new
            {
                email = _options.Email,
                senha = _options.Password
            };

            var content = new StringContent(JsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("api/account/login", content);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Falha ao autenticar Seed User: {response.StatusCode}");
                throw new Exception("Falha ao autenticar Seed User no Veiculando Core.");
            }

            var responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);
            
            // Supondo que a API do Veiculando retorna { "token": "...", "expiresIn": 300 }
            if (doc.RootElement.TryGetProperty("token", out var tokenElement))
            {
                var token = tokenElement.GetString();
                
                var expiresIn = 300; // default 300 min
                if (doc.RootElement.TryGetProperty("expiresIn", out var expElement))
                {
                    expiresIn = expElement.GetInt32();
                }

                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(TimeSpan.FromMinutes(expiresIn - 5)); // margem de 5 min

                _cache.Set(TokenCacheKey, token, cacheOptions);

                return token;
            }

            throw new Exception("Token não encontrado na resposta de login.");
        }
    }
}
