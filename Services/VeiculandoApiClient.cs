using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
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

    /// <summary>
    /// Cliente autenticado para a API do Veiculando Core, usando a conta de
    /// serviço da instância WhiteLabel.
    /// </summary>
    /// <remarks>
    /// Existe porque escrever <c>Local</c>/<c>Peca</c> direto por EF a partir do
    /// BFF exigiria replicar o <c>LocalCadastroHandler</c> inteiro — geração de
    /// código com repetição em colisão, transição de aprovação, validação de
    /// afiliada, notificações do domínio. Regra duplicada diverge; então o BFF
    /// monta o command e delega ao core, que continua sendo o dono da regra.
    ///
    /// <para>Um efeito importante: como a conta de serviço é um
    /// <c>UsuarioAfiliada</c>, o handler entra no ramo
    /// <c>EnviarParaAprovacao()</c> sozinho — é exatamente o fluxo da ADR-WL-004,
    /// sem nenhum código de aprovação no BFF.</para>
    /// </remarks>
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
            if (string.IsNullOrWhiteSpace(_options?.Email) || string.IsNullOrWhiteSpace(_options?.Password))
                throw new InvalidOperationException("Conta de serviço não configurada (seção SeedAccount).");

            // O endpoint é 'login-usuario-afiliada', e não 'login': a conta de
            // serviço é um UsuarioAfiliada, e é esse endpoint que devolve as
            // claims de afiliada que o core espera nas rotas de cadastro.
            //
            // O corpo vai FORM-URLENCODED porque o AccountController declara o
            // parâmetro como [FromForm]. A versão anterior deste cliente enviava
            // JSON para 'api/account/login' — o binding produzia um command com
            // Email/Senha nulos e a autenticação nunca poderia ter funcionado.
            var conteudo = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("Email", _options.Email),
                new KeyValuePair<string, string>("Senha", _options.Password)
            });

            var response = await _httpClient.PostAsync("api/account/login-usuario-afiliada", conteudo);

            if (!response.IsSuccessStatusCode)
            {
                var corpo = await response.Content.ReadAsStringAsync();
                _logger.LogError("Falha ao autenticar a conta de serviço no core: {Status} — {Corpo}",
                    response.StatusCode, corpo);
                throw new InvalidOperationException("Falha ao autenticar a conta de serviço no Veiculando Core.");
            }

            var responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);

            if (!doc.RootElement.TryGetProperty("token", out var tokenElement))
                throw new InvalidOperationException("Token não encontrado na resposta de login do core.");

            var token = tokenElement.GetString();

            // A resposta usa 'expires' (em minutos). O nome 'expiresIn', que a
            // versão anterior procurava, não existe no contrato.
            var expiraEmMinutos = 300;
            if (doc.RootElement.TryGetProperty("expires", out var expElement) &&
                expElement.TryGetInt32(out var minutos) && minutos > 5)
            {
                expiraEmMinutos = minutos;
            }

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(expiraEmMinutos - 5)); // margem de 5 min

            _cache.Set(TokenCacheKey, token, cacheOptions);

            return token;
        }
    }
}
