using System;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.Domain.ValueObjects;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;
using Veiculando.WhiteLabel.Api.Tests.Infrastructure;
using Xunit;

namespace Veiculando.WhiteLabel.Api.Tests
{
    /// <summary>
    /// VEI-RD-157 (TP2a): carteira da vendedora na Prospecção. Cadastro em rascunho com os
    /// dois documentos (I3), cliente inativo até a aprovação (I4), CNPJ por dígitos no SQL
    /// (I5), escopo por vendedora no contexto/campanha e sessão não confirmada recusada (I1).
    /// </summary>
    [Collection(DatabaseCollection.Nome)]
    public class ProspeccaoCarteiraTests
    {
        private readonly SqlServerFixture _db;

        public ProspeccaoCarteiraTests(SqlServerFixture db) => _db = db;

        private sealed record Sessao(string Token, int FonteUsuarioId);
        private sealed record Login(string Token);
        internal sealed record Cadastro(Guid Id, int ClienteId, string Status, string Origem);

        private static async Task PrepararAfiliadaAsync(int afiliadaId)
        {
            var local = await Seed.LocalAsync(afiliadaId, $"CRT{afiliadaId}");
            var peca = await Seed.PecaAsync(local, $"P-CRT-{afiliadaId}");
            await Seed.ReservaAsync(afiliadaId, $"CRT-{afiliadaId}", peca);
            using var ctx = new VeiculandoDataContext();
            await ctx.Database.ExecuteSqlCommandAsync(@"
IF NOT EXISTS (SELECT 1 FROM PerfilUsuario WHERE Codigo = 'UsuarioAnunciante')
    INSERT PerfilUsuario (Nome, Codigo, DataCadastro, DataAtualizacao, StatusExibicao)
    VALUES ('Anunciante WL', 'UsuarioAnunciante', GETUTCDATE(), GETUTCDATE(), 1);
UPDATE Afiliada SET Cnpj = @p1, IdUsuarioCadastro = 1, RazaoSocial = @p2, Cidade = 'Sao Paulo', Uf = 'SP',
    Telefone = '11999999999' WHERE Id = @p0;",
                afiliadaId, Cnpj(afiliadaId, 0), $"Exibidora {afiliadaId} Ltda");
        }

        /// <summary>CNPJ válido e único por tenant: o cadastro de Cliente do Core é global.</summary>
        internal static string Cnpj(int tenant, int n)
        {
            var d = $"{tenant:D4}{n:D4}0001".Select(c => c - '0').ToList();
            int Dv(int[] pesos) { var r = d.Select((x, i) => x * pesos[i]).Sum() % 11; return r < 2 ? 0 : 11 - r; }
            d.Add(Dv(new[] { 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 }));
            d.Add(Dv(new[] { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 }));
            return string.Concat(d);
        }

        private static string Mascara(string c) => $"{c[..2]}.{c[2..5]}.{c[5..8]}/{c[8..12]}-{c[12..]}";

        private static async Task<(HttpClient App, int VendedoraId)> AbrirCarteiraAsync(WlApiFactory factory, int tenant, string prefixo)
        {
            var email = $"{prefixo}{Guid.NewGuid():N}"[..20] + "@exemplo.com";
            await Seed.OperadorAsync(tenant, email, new[] { "PedidoCriar" });
            var operador = await factory.ClienteAutenticadoAsync(email, Seed.SenhaPadrao);
            var emitida = await operador.PostAsJsonAsync("/api/wl/prospeccao/sessao", new { anuncianteId = (int?)null });
            emitida.StatusCode.Should().Be(HttpStatusCode.OK, await emitida.Content.ReadAsStringAsync());
            var sessao = (await emitida.Content.ReadFromJsonAsync<Sessao>())!;
            var app = factory.ClienteAnonimo();
            var resgate = await app.PostAsJsonAsync("/api/wl/app/prospeccao/sessao",
                new { Token = sessao.Token, OperadorId = sessao.FonteUsuarioId, AnuncianteId = (int?)null });
            resgate.StatusCode.Should().Be(HttpStatusCode.OK, await resgate.Content.ReadAsStringAsync());
            app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                (await resgate.Content.ReadFromJsonAsync<Login>())!.Token);
            return (app, sessao.FonteUsuarioId);
        }

        internal static object Dados(string cnpj) => new
        {
            cnpj, nomeFantasia = "Padaria Teste", razaoSocial = "Padaria Teste Ltda", telefone = "(12) 3333-4444",
            logradouro = "Rua A", numero = "10", bairro = "Centro", cep = "12000-000", cidade = "Taubaté", uf = "SP"
        };

        internal static async Task<Cadastro> CadastrarAsync(HttpClient app, string cnpj)
        {
            var r = await app.PostAsJsonAsync("/api/wl/app/prospeccao/carteira", Dados(cnpj));
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            return (await r.Content.ReadFromJsonAsync<Cadastro>())!;
        }

        internal static async Task<HttpResponseMessage> AnexarAsync(HttpClient app, Guid id, string tipo)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(tipo), "type");
            var pdf = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj"));
            pdf.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(pdf, "file", tipo + ".pdf");
            return await app.PostAsync($"/api/wl/app/prospeccao/carteira/{id}/documentos", form);
        }

        internal static async Task EnviarComDocumentosAsync(HttpClient app, Guid id)
        {
            foreach (var tipo in new[] { WlCarteiraDocumento.TipoContratoSocial, WlCarteiraDocumento.TipoResponsavel })
            {
                var r = await AnexarAsync(app, id, tipo);
                r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            }
            var enviado = await app.PostAsync($"/api/wl/app/prospeccao/carteira/{id}/enviar", null);
            enviado.StatusCode.Should().Be(HttpStatusCode.OK, await enviado.Content.ReadAsStringAsync());
        }

        /// <summary>Aprovação como o TP2b fará: domínio, vínculo ativo e contrato com a venda direta.</summary>
        internal static async Task AprovarAsync(int tenant, Guid carteiraId)
        {
            using var ctx = new VeiculandoDataContext();
            var carteira = await ctx.WlCarteiraClientes.SingleAsync(c => c.Id == carteiraId);
            carteira.Aprovar(analistaId: 999_999);
            (await ctx.AfiliadaClientes.SingleAsync(v => v.IdAfiliada == tenant && v.IdCliente == carteira.ClienteId)).Ativar();
            var afiliada = await ctx.Afiliadas.Include(a => a.UsuarioCadastro).SingleAsync(a => a.Id == tenant);
            var emailDaCasa = AnuncianteDaCasaProvisionamento.EmailDe(tenant);
            var casa = await ctx.WlUsuariosAnunciante.SingleAsync(u => u.AfiliadaId == tenant && u.Email.Endereco == emailDaCasa);
            var agencia = await ctx.Agencias.SingleAsync(a => a.Id == casa.AgenciaId);
            var cliente = await ctx.Clientes.SingleAsync(c => c.Id == carteira.ClienteId);
            ctx.AgenciaClientes.Add(new AgenciaCliente(agencia, cliente, 0m, null, DateTime.UtcNow.Date.AddDays(-1),
                DateTime.UtcNow.Date.AddYears(2), afiliada.UsuarioCadastro));
            await ctx.SaveChangesAsync();
        }

        private static async Task<JsonElement> ContextoAsync(HttpClient app)
        {
            var r = await app.GetAsync("/api/wl/app/checkout/context");
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        [Fact]
        public async Task Cadastro_nasce_em_rascunho_inativo_e_so_vai_para_analise_com_os_dois_documentos()
        {
            const int tenant = 8985;
            await PrepararAfiliadaAsync(tenant);
            using var factory = new WlApiFactory(_db, tenant);
            var (app, vendedora) = await AbrirCarteiraAsync(factory, tenant, "crt-ana");

            var c = await CadastrarAsync(app, Cnpj(tenant, 1));
            c.Status.Should().Be(nameof(WlCarteiraStatus.Rascunho));
            c.Origem.Should().Be(nameof(WlCarteiraOrigem.Cadastro));

            using (var ctx = new VeiculandoDataContext())
            {
                var vinculo = await ctx.AfiliadaClientes.SingleAsync(v => v.IdAfiliada == tenant && v.IdCliente == c.ClienteId);
                vinculo.Status.Should().Be(StatusVinculoEnum.Inativo, "cadastro não aprovado não é cliente da exibidora (I4)");
                vinculo.FonteUsuarioId.Should().Be(vendedora);
            }

            var semDocumento = await app.PostAsync($"/api/wl/app/prospeccao/carteira/{c.Id}/enviar", null);
            semDocumento.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await AnexarAsync(app, c.Id, WlCarteiraDocumento.TipoContratoSocial)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await app.PostAsync($"/api/wl/app/prospeccao/carteira/{c.Id}/enviar", null)).StatusCode
                .Should().Be(HttpStatusCode.Conflict, "falta o documento do responsável (I3)");

            (await AnexarAsync(app, c.Id, WlCarteiraDocumento.TipoResponsavel)).StatusCode.Should().Be(HttpStatusCode.OK);
            var enviado = await app.PostAsync($"/api/wl/app/prospeccao/carteira/{c.Id}/enviar", null);
            enviado.StatusCode.Should().Be(HttpStatusCode.OK, await enviado.Content.ReadAsStringAsync());

            var lista = JsonDocument.Parse(await app.GetStringAsync("/api/wl/app/prospeccao/carteira")).RootElement;
            lista.EnumerateArray().Single().GetProperty("status").GetString().Should().Be(nameof(WlCarteiraStatus.PendenteAnalise));
            (await AnexarAsync(app, c.Id, WlCarteiraDocumento.TipoResponsavel)).StatusCode
                .Should().Be(HttpStatusCode.Conflict, "em análise o documento não muda");

            // Em análise ainda não vende; e a própria exibidora (cliente da casa) nunca aparece.
            (await ContextoAsync(app)).GetProperty("clients").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task Cliente_aprovado_fica_disponivel_so_para_a_vendedora_dona()
        {
            const int tenant = 8986;
            await PrepararAfiliadaAsync(tenant);
            using var factory = new WlApiFactory(_db, tenant);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "crt-ana");
            var (bia, _) = await AbrirCarteiraAsync(factory, tenant, "crt-bia");

            var c = await CadastrarAsync(ana, Cnpj(tenant, 1));
            await EnviarComDocumentosAsync(ana, c.Id);
            await AprovarAsync(tenant, c.Id);

            var daAna = await ContextoAsync(ana);
            daAna.GetProperty("clients").EnumerateArray().Select(x => x.GetProperty("id").GetInt32())
                .Should().Equal(c.ClienteId);
            var campanha = await ana.PostAsJsonAsync("/api/wl/app/checkout/context/campaigns", new
            {
                name = "Campanha Ana", product = "Pão", startDate = DateTime.UtcNow.Date.AddDays(1),
                endDate = DateTime.UtcNow.Date.AddDays(30), budget = 1000m, clientId = c.ClienteId
            });
            campanha.StatusCode.Should().Be(HttpStatusCode.Created, await campanha.Content.ReadAsStringAsync());
            (await ContextoAsync(ana)).GetProperty("campaigns").GetArrayLength().Should().Be(1);

            var daBia = await ContextoAsync(bia);
            daBia.GetProperty("clients").GetArrayLength().Should().Be(0);
            daBia.GetProperty("campaigns").GetArrayLength().Should().Be(0, "a campanha é da carteira da Ana");
            var roubo = await bia.PostAsJsonAsync("/api/wl/app/checkout/context/campaigns", new
            {
                name = "Campanha Bia", product = "Pão", startDate = DateTime.UtcNow.Date.AddDays(1),
                endDate = DateTime.UtcNow.Date.AddDays(30), budget = 1000m, clientId = c.ClienteId
            });
            roubo.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // Sem cliente, nem a Ana cria campanha: na Prospecção não existe venda "da casa".
            var semCliente = await ana.PostAsJsonAsync("/api/wl/app/checkout/context/campaigns", new
            {
                name = "Casa", product = "X", startDate = DateTime.UtcNow.Date.AddDays(1), endDate = DateTime.UtcNow.Date.AddDays(30)
            });
            semCliente.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task Cnpj_de_outra_vendedora_gravado_com_mascara_nao_duplica()
        {
            const int tenant = 8987;
            await PrepararAfiliadaAsync(tenant);
            using var factory = new WlApiFactory(_db, tenant);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "crt-ana");
            var (bia, _) = await AbrirCarteiraAsync(factory, tenant, "crt-bia");
            var cnpj = Cnpj(tenant, 1);

            var c = await CadastrarAsync(ana, Mascara(cnpj));
            await EnviarComDocumentosAsync(ana, c.Id);
            using (var ctx = new VeiculandoDataContext())
                await ctx.Database.ExecuteSqlCommandAsync("UPDATE Cliente SET Cnpj = @p0 WHERE Id = @p1", Mascara(cnpj), c.ClienteId);

            var daBia = await bia.PostAsJsonAsync("/api/wl/app/prospeccao/carteira", Dados(cnpj));
            daBia.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var corpo = await daBia.Content.ReadAsStringAsync();
            corpo.Should().Contain("other_seller").And.Contain("crt-ana");

            var deNovo = await ana.PostAsJsonAsync("/api/wl/app/prospeccao/carteira", Dados(cnpj));
            deNovo.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await deNovo.Content.ReadAsStringAsync()).Should().Contain("own_client");

            using var db = new VeiculandoDataContext();
            (await db.Clientes.CountAsync(x => x.Id == c.ClienteId)).Should().Be(1);
            (await db.WlCarteiraClientes.CountAsync(x => x.AfiliadaId == tenant)).Should().Be(1);
        }

        [Fact]
        public async Task Cliente_ativo_da_exibidora_sem_vendedora_vira_solicitacao_sem_criar_outro_cliente()
        {
            const int tenant = 8988;
            await PrepararAfiliadaAsync(tenant);
            var cnpj = Cnpj(tenant, 1);
            int clienteId;
            using (var ctx = new VeiculandoDataContext())
            {
                var afiliada = await ctx.Afiliadas.Include(a => a.UsuarioCadastro).SingleAsync(a => a.Id == tenant);
                var cliente = new Cliente($"M{tenant}", "Mercado", "Mercado Ltda",
                    new Endereco("Centro", "Rua B", "20", null, null, new Cep("12000-000")), "Taubaté", "SP",
                    new Telefone("1233334444"), new Cnpj(Mascara(cnpj)), null, null, "Taubaté", null, null, null,
                    string.Empty, 0m, afiliada.UsuarioCadastro);
                ctx.Clientes.Add(cliente);
                ctx.AfiliadaClientes.Add(new AfiliadaCliente(afiliada, cliente, afiliada.UsuarioCadastro));
                await ctx.SaveChangesAsync();
                clienteId = cliente.Id;
            }
            using var factory = new WlApiFactory(_db, tenant);
            var (ana, _) = await AbrirCarteiraAsync(factory, tenant, "crt-ana");

            var c = await CadastrarAsync(ana, cnpj);

            c.ClienteId.Should().Be(clienteId);
            c.Origem.Should().Be(nameof(WlCarteiraOrigem.Solicitacao));
            using var db = new VeiculandoDataContext();
            (await db.AfiliadaClientes.SingleAsync(v => v.IdAfiliada == tenant && v.IdCliente == clienteId)).Status
                .Should().Be(StatusVinculoEnum.Ativo, "o vínculo que já existia não muda");
            (await db.Clientes.CountAsync(x => x.Id == clienteId)).Should().Be(1);
        }

        [Fact]
        public async Task Sessao_de_prospeccao_que_nao_confere_e_recusada_em_vez_de_vender_como_a_casa()
        {
            const int tenant = 8989;
            using var db = new VeiculandoDataContext();
            var carteira = new CarteiraProspeccao(db, new TenantQueries(db, new TenantFixo(tenant)));
            var casa = new WlUsuarioAnunciante(AnuncianteDaCasaProvisionamento.EmailDe(tenant), "hash", tenant);
            var comum = new WlUsuarioAnunciante("anunciante@exemplo.com", "hash", tenant);

            static ClaimsPrincipal Usuario(params Claim[] claims) => new(new ClaimsIdentity(claims, "teste"));
            var jtiSemResgate = new Claim("jti", Guid.NewGuid().ToString("N"));

            (await carteira.ResolverAsync(Usuario(new Claim("Prospeccao", "true"), jtiSemResgate), casa, CancellationToken.None))
                .Modo.Should().Be(ModoSessaoApp.Recusada, "resgate não encontrado (I1)");
            (await carteira.ResolverAsync(Usuario(jtiSemResgate), casa, CancellationToken.None))
                .Modo.Should().Be(ModoSessaoApp.Recusada, "a casa só entra por Prospecção");
            (await carteira.ResolverAsync(Usuario(new Claim("Prospeccao", "true")), comum, CancellationToken.None))
                .Modo.Should().Be(ModoSessaoApp.Recusada, "sem jti não há sessão");
            (await carteira.ResolverAsync(Usuario(jtiSemResgate), comum, CancellationToken.None))
                .Modo.Should().Be(ModoSessaoApp.Comum, "login comum do anunciante segue igual");
        }

        private sealed class TenantFixo : ITenantContext
        {
            public TenantFixo(int afiliadaId) => AfiliadaId = afiliadaId;
            public int AfiliadaId { get; }
            public string Host => $"tenant-{AfiliadaId}.teste";
            public WlDominioTipoEnum Tipo => WlDominioTipoEnum.Painel;
            public bool Resolvido => true;
            public void Definir(WlTenantInfo tenant) => throw new NotSupportedException();
        }
    }
}
