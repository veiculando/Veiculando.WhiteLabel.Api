using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// VEI-RD-83 / HF-8 (D10) — auditoria consultável da sessão de prospecção
    /// (cenário 7) e uso único garantido pelo banco (cenário 4).
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class ProspeccaoAuditoriaTests
    {
        private const int Afiliada = 8960;
        private readonly SqlServerFixture _db;

        public ProspeccaoAuditoriaTests(SqlServerFixture db) => _db = db;

        private sealed record Sessao(string AppUrl, string Token, int FonteUsuarioId);

        // O anunciante vai na emissão porque faz parte do recurso assinado: um token
        // emitido sem ele não é resgatável (pendência do Humano sobre o seletor).
        private static async Task<Sessao> EmitirAsync(HttpClient operador, int? anuncianteId)
        {
            var resposta = await operador.PostAsJsonAsync("/api/wl/prospeccao/sessao", new { anuncianteId });
            resposta.StatusCode.Should().Be(HttpStatusCode.OK, await resposta.Content.ReadAsStringAsync());
            return (await resposta.Content.ReadFromJsonAsync<Sessao>())!;
        }

        private static Task<HttpResponseMessage> ResgatarAsync(HttpClient app, Sessao s, int anuncianteId) =>
            app.PostAsJsonAsync("/api/wl/app/prospeccao/sessao",
                new { Token = s.Token, OperadorId = s.FonteUsuarioId, AnuncianteId = anuncianteId });

        [Fact]
        public async Task Emissao_e_resgate_ficam_na_trilha_consultavel_sem_o_token()
        {
            var sufixo = Guid.NewGuid().ToString("N")[..8];
            var admin = $"prosp-admin-{sufixo}@exemplo.com";
            var operadorId = await Seed.OperadorAsync(Afiliada, admin,
                new[] { "PedidoCriar", "UsuarioAfiliadaGerenciar" }, nome: "Rafael Andrade");
            var anuncianteId = await Seed.AnuncianteAsync(Afiliada, $"cliente-{sufixo}@exemplo.com");

            using var factory = new WlApiFactory(_db, Afiliada);
            using var operador = await factory.ClienteAutenticadoAsync(admin, Seed.SenhaPadrao);
            using var app = factory.ClienteAnonimo();

            var sessao = await EmitirAsync(operador, anuncianteId);
            sessao.AppUrl.Should().Be("https://app.invalido");

            (await ResgatarAsync(app, sessao, anuncianteId)).StatusCode.Should().Be(HttpStatusCode.OK);

            var resposta = await operador.GetAsync("/api/wl/prospeccao/auditoria");
            resposta.StatusCode.Should().Be(HttpStatusCode.OK);
            var corpo = await resposta.Content.ReadAsStringAsync();
            corpo.Should().NotContain(sessao.Token, "a trilha nunca expõe o token (cenário 8)");

            using var json = JsonDocument.Parse(corpo);
            var linha = json.RootElement.GetProperty("itens").EnumerateArray()
                .Single(i => i.GetProperty("operador").GetProperty("id").GetInt32() == operadorId);
            linha.GetProperty("operador").GetProperty("nome").GetString().Should().Be("Rafael Andrade");
            linha.GetProperty("anunciante").GetProperty("id").GetInt32().Should().Be(anuncianteId);
            linha.GetProperty("emitidaEm").GetDateTime().Should().BeBefore(linha.GetProperty("expiraEm").GetDateTime());
            linha.GetProperty("usadaEm").ValueKind.Should().NotBe(JsonValueKind.Null);
            linha.GetProperty("pedidos").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task Segundo_resgate_e_recusado_mesmo_sem_o_cache_da_instancia()
        {
            var sufixo = Guid.NewGuid().ToString("N")[..8];
            var email = $"prosp-op-{sufixo}@exemplo.com";
            await Seed.OperadorAsync(Afiliada, email, new[] { "PedidoCriar" });
            var anuncianteId = await Seed.AnuncianteAsync(Afiliada, $"cliente2-{sufixo}@exemplo.com");

            Sessao sessao;
            using (var factory = new WlApiFactory(_db, Afiliada))
            {
                using var operador = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
                sessao = await EmitirAsync(operador, anuncianteId);
                using var app = factory.ClienteAnonimo();
                (await ResgatarAsync(app, sessao, anuncianteId)).StatusCode.Should().Be(HttpStatusCode.OK);
            }

            // Outra instância do BFF = outro IMemoryCache, como uma segunda réplica.
            // Quem recusa aqui é o índice único do banco.
            using var outraReplica = new WlApiFactory(_db, Afiliada);
            using var outroApp = outraReplica.ClienteAnonimo();
            (await ResgatarAsync(outroApp, sessao, anuncianteId)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            using var db = new VeiculandoDataContext();
            var jti = (await db.WlProspeccaoSessaoEventos.Where(e => e.Evento == WlProspeccaoEventoTipo.Resgatada
                && e.AnuncianteId == anuncianteId).ToListAsync()).Should().ContainSingle().Which.Jti;
            (await db.WlAppSessoes.CountAsync(s => s.UsuarioId == anuncianteId)).Should().Be(1);
            jti.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public async Task Auditoria_exige_gerir_usuarios_e_nao_mostra_outra_afiliada()
        {
            var sufixo = Guid.NewGuid().ToString("N")[..8];
            var soAbre = $"prosp-so-abre-{sufixo}@exemplo.com";
            await Seed.OperadorAsync(Afiliada, soAbre, new[] { "PedidoCriar" });

            const int outra = 8961;
            var deOutra = $"prosp-outra-{sufixo}@exemplo.com";
            var operadorDeOutra = await Seed.OperadorAsync(outra, deOutra, new[] { "PedidoCriar" });
            using (var factoryOutra = new WlApiFactory(_db, outra))
            using (var clienteOutra = await factoryOutra.ClienteAutenticadoAsync(deOutra, Seed.SenhaPadrao))
                await EmitirAsync(clienteOutra, null);

            using var factory = new WlApiFactory(_db, Afiliada);
            using var semPermissao = await factory.ClienteAutenticadoAsync(soAbre, Seed.SenhaPadrao);
            (await semPermissao.GetAsync("/api/wl/prospeccao/auditoria")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var admin = $"prosp-admin2-{sufixo}@exemplo.com";
            await Seed.OperadorAsync(Afiliada, admin, new[] { "PedidoCriar", "UsuarioAfiliadaGerenciar" });
            using var comPermissao = await factory.ClienteAutenticadoAsync(admin, Seed.SenhaPadrao);
            using var json = JsonDocument.Parse(await comPermissao.GetStringAsync("/api/wl/prospeccao/auditoria"));
            json.RootElement.GetProperty("itens").EnumerateArray()
                .Should().NotContain(i => i.GetProperty("operador").GetProperty("id").GetInt32() == operadorDeOutra);
        }

        [Fact]
        public async Task Token_sem_emissao_registrada_nao_abre_sessao()
        {
            // Um token validamente assinado, mas que nunca passou pela trilha (emitido
            // fora do endpoint), não pode abrir sessão: sessão fora da auditoria.
            var sufixo = Guid.NewGuid().ToString("N")[..8];
            var email = $"prosp-fora-{sufixo}@exemplo.com";
            var operadorId = await Seed.OperadorAsync(Afiliada, email, new[] { "PedidoCriar" });
            var anuncianteId = await Seed.AnuncianteAsync(Afiliada, $"cliente3-{sufixo}@exemplo.com");

            using var factory = new WlApiFactory(_db, Afiliada);
            var assinador = (Veiculando.WhiteLabel.Api.Services.IWlLinkTemporario)factory.Services
                .GetService(typeof(Veiculando.WhiteLabel.Api.Services.IWlLinkTemporario))!;
            var token = assinador.Assinar(Veiculando.WhiteLabel.Api.Services.WlLinkTemporario.PropositoProspeccao,
                $"{operadorId}:{anuncianteId}", Afiliada, TimeSpan.FromMinutes(2));

            using var app = factory.ClienteAnonimo();
            (await ResgatarAsync(app, new Sessao("", token.Token, operadorId), anuncianteId))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }
}
