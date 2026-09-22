using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Análises de KYC — fila de triagem (VEI-RD-80) e detalhe/decisão (VEI-RD-81).
    /// </summary>
    /// <remarks>
    /// <para><b>Permissão.</b> <c>ClienteGerenciar</c>, a mesma de Anunciantes e
    /// Agências: aprovar um KYC é o que CRIA a organização comercial e o vínculo com
    /// a afiliada, então quem pode gerir organizações é quem pode triá-las. Uma
    /// permissão própria (<c>OnboardingAnalisar</c>) exigiria mexer em
    /// <c>WlPermissoesValidas</c> e numa migration de claims — escopo de outro card.</para>
    ///
    /// <para><b>⚠️ Lacuna de modelo herdada do plano 2 (VEI-RD-78).</b>
    /// <c>OrganizacaoOnboarding</c> não tem colunas de dados da empresa — nome
    /// fantasia, razão social, CNPJ, telefone, e-mail corporativo, site, endereço,
    /// inscrições, logo — e <c>IdOrganizacao</c> é nulo por design até a aprovação
    /// ("não existe organização provisória no domínio principal", diz a própria
    /// entidade). O resultado é que um onboarding AINDA NÃO APROVADO não tem de onde
    /// tirar a coluna "Nome / Razão social" da fila (VEI-RD-80) nem a aba 1 "Dados da
    /// empresa" do detalhe (VEI-RD-81) — exatamente os dados que o operador precisa
    /// para decidir. Depois da aprovação os campos existem, o que inverte a ordem
    /// útil: a informação chega quando não é mais necessária.
    ///
    /// Isto NÃO foi contornado aqui de propósito. A tabela é entregue por VEI-RD-78,
    /// que está numa PR aberta (#179); acrescentar as colunas por este card seria
    /// construir o schema de outro card em paralelo ao dele. Os campos vão como
    /// <c>null</c> — nunca string vazia — para a tela conseguir distinguir "ainda não
    /// existe" de "veio em branco". Ver o walkthrough desta execução.</para>
    ///
    /// <para><b>São 5 estados na fila, não 7.</b> <c>Rascunho</c> é estado do
    /// solicitante, ainda não enviado: não é item de triagem porque ninguém pediu
    /// análise. <c>Suspenso</c> é ação PÓS-aprovação, aplicada a organização já ativa
    /// pelo detalhe — também não é triagem. Os dois continuam no domínio
    /// (<c>EstadoOnboardingEnum</c> tem os sete) e aparecem no detalhe; o que muda é
    /// o que a fila mostra.</para>
    /// </remarks>
    [ApiController]
    [Route("api/wl/kyc")]
    [Authorize(Policy = AuthorizationSetup.ClienteGerenciar)]
    public class KycController : ControllerBase
    {
        /// <summary>Os 5 estados que a fila de triagem enxerga (VEI-RD-80).</summary>
        public static readonly EstadoOnboardingEnum[] EstadosDaFila =
        {
            EstadoOnboardingEnum.PendenteVerificacao,
            EstadoOnboardingEnum.EmAnalise,
            EstadoOnboardingEnum.AjustesSolicitados,
            EstadoOnboardingEnum.Aprovado,
            EstadoOnboardingEnum.Rejeitado
        };

        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly IWlLinkTemporario _links;
        private readonly IWlUploadStorage _storage;
        private readonly IConfiguration _config;

        public KycController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            IWlLinkTemporario links,
            IWlUploadStorage storage,
            IConfiguration config)
        {
            _db = db;
            _tenant = tenant;
            _links = links;
            _storage = storage;
            _config = config;
        }

        private int? WlUsuarioId =>
            int.TryParse(User.FindFirst("WlUsuarioId")?.Value, out var id) ? id : (int?)null;

        // ------------------------------------------------------------------
        // VEI-RD-80 — fila
        // ------------------------------------------------------------------

        /// <summary>
        /// Fila paginada de análises. Filtros: tipo, estado, analista, data de envio e
        /// busca por CNPJ / razão social / responsável legal.
        /// </summary>
        /// <remarks>
        /// O filtro por <b>data de envio</b> aceita data livre, e isso não viola o
        /// PRD §2.3. Aquela regra proíbe data livre onde o domínio exige
        /// <c>Periodo.Id</c> — período COMERCIAL de veiculação. <c>DataEnvio</c> é
        /// metadado do processo de onboarding: não existe bissemana de envio de KYC.
        /// </remarks>
        [HttpGet("analises")]
        public async Task<IActionResult> Fila(
            [FromQuery] string tipo,
            [FromQuery] string estado,
            [FromQuery] int? analistaId,
            [FromQuery] DateTime? dataEnvio,
            [FromQuery] string busca,
            [FromQuery] WlPaginaRequest pagina)
        {
            var query = FiltrarFila(tipo, estado, analistaId, dataEnvio, busca);

            var (page, pageSize) = WlPaginacao.Normalizar(pagina);
            var total = await query.CountAsync();

            var itens = await query
                .OrderByDescending(o => o.DataEnvio)
                .ThenBy(o => o.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(o => new
                {
                    o.Id,
                    Tipo = o.TipoOrganizacao,
                    o.Estado,
                    o.DataEnvio,
                    AnalistaId = o.IdResponsavelAnalise,
                    AnalistaNome = o.ResponsavelAnalise == null ? null : o.ResponsavelAnalise.Nome,
                    // LACUNA DE MODELO (ver <remarks> da classe): enquanto o onboarding
                    // nao e aprovado, IdOrganizacao e nulo e nao ha onde ler nome
                    // fantasia, razao social nem CNPJ. Depois da aprovacao a organizacao
                    // existe e os campos sao reais. Vem null em vez de string vazia para
                    // a tela distinguir "ainda nao ha" de "esta em branco".
                    Nome = o.IdOrganizacao == null ? null :
                        (o.TipoOrganizacao == TipoOrganizacaoEnum.Agencia
                            ? _db.Agencias.Where(a => a.Id == o.IdOrganizacao.Value).Select(a => a.Nome).FirstOrDefault()
                            : _db.Clientes.Where(c => c.Id == o.IdOrganizacao.Value).Select(c => c.Nome).FirstOrDefault()),
                    RazaoSocial = o.IdOrganizacao == null ? null :
                        (o.TipoOrganizacao == TipoOrganizacaoEnum.Agencia
                            ? _db.Agencias.Where(a => a.Id == o.IdOrganizacao.Value).Select(a => a.RazaoSocial).FirstOrDefault()
                            : _db.Clientes.Where(c => c.Id == o.IdOrganizacao.Value).Select(c => c.RazaoSocial).FirstOrDefault()),
                    Cnpj = o.IdOrganizacao == null ? null :
                        (o.TipoOrganizacao == TipoOrganizacaoEnum.Agencia
                            ? _db.Agencias.Where(a => a.Id == o.IdOrganizacao.Value).Select(a => a.Cnpj.Numero).FirstOrDefault()
                            : _db.Clientes.Where(c => c.Id == o.IdOrganizacao.Value).Select(c => c.Cnpj.Numero).FirstOrDefault()),
                    Responsavel = _db.OrganizacaoResponsaveisLegais
                        .Where(r => r.IdOnboarding == o.Id)
                        .Select(r => new { r.Nome, r.Cpf, r.Email })
                        .FirstOrDefault()
                })
                .ToListAsync();

            return Ok(WlPaginacao.Montar(itens, page, pageSize, total));
        }

        /// <summary>
        /// Contagem por estado para os 5 chips. Aplica OS MESMOS filtros da listagem —
        /// exceto o próprio estado, que é o eixo da contagem.
        /// </summary>
        /// <remarks>
        /// Se o resumo ignorasse os filtros, o chip diria "12 pendentes" enquanto a
        /// lista filtrada por analista mostrasse 3, e o operador não teria como saber
        /// qual dos dois números é o do seu trabalho. A contagem é feita em SQL
        /// (<c>GroupBy</c> + <c>Count</c>), sem carregar a lista.
        /// </remarks>
        [HttpGet("analises/resumo")]
        public async Task<IActionResult> Resumo(
            [FromQuery] string tipo,
            [FromQuery] int? analistaId,
            [FromQuery] DateTime? dataEnvio,
            [FromQuery] string busca)
        {
            var contagens = await FiltrarFila(tipo, estado: null, analistaId, dataEnvio, busca)
                .GroupBy(o => o.Estado)
                .Select(g => new { Estado = g.Key, Total = g.Count() })
                .ToListAsync();

            // Estado sem nenhum item precisa vir com 0, não sumir: um chip que
            // desaparece quando zera faz a régua de estados mudar de tamanho a cada
            // filtro, e o operador perde a referência de onde clicar.
            var resultado = EstadosDaFila.ToDictionary(
                estado => estado.ToString(),
                estado => contagens.FirstOrDefault(c => c.Estado == estado)?.Total ?? 0);

            return Ok(resultado);
        }

        /// <summary>
        /// Assume a análise: move para <c>EmAnalise</c> e grava o responsável.
        /// </summary>
        /// <remarks>
        /// Idempotente para o mesmo operador; conflito para um segundo. O update é
        /// CONDICIONAL (<c>WHERE IdResponsavelAnalise IS NULL OR = @operador</c>) e não
        /// um "leia, cheque, grave": com duas requisições simultâneas, ler-e-checar
        /// deixaria as duas passarem pela checagem antes de qualquer uma gravar, e a
        /// última escrita venceria em silêncio. Quem não afetar nenhuma linha perdeu.
        /// </remarks>
        [HttpPost("analises/{id}/assumir")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Assumir(int id)
        {
            var operadorId = WlUsuarioId;
            if (operadorId == null)
                return Unauthorized();

            var onboarding = await _tenant.Onboardings.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
            if (onboarding == null)
                return NotFound(new { message = "Análise não encontrada." });

            var afetadas = await _db.Database.ExecuteSqlCommandAsync(
                @"UPDATE OrganizacaoOnboarding
                     SET IdResponsavelAnalise = @p0,
                         Estado = @p1
                   WHERE Id = @p2
                     AND IdAfiliada = @p3
                     AND (IdResponsavelAnalise IS NULL OR IdResponsavelAnalise = @p0)",
                operadorId.Value, (int)EstadoOnboardingEnum.EmAnalise, id, _tenant.AfiliadaId);

            if (afetadas == 0)
                return Conflict(new { message = "Esta análise já foi assumida por outro operador." });

            return Ok(new { Id = id, Estado = EstadoOnboardingEnum.EmAnalise, AnalistaId = operadorId });
        }

        // ------------------------------------------------------------------
        // VEI-RD-81 — detalhe e decisões
        // ------------------------------------------------------------------

        /// <summary>
        /// Detalhe completo, servindo as 7 abas. Onboarding de outra afiliada devolve
        /// 404, não 403 — não se revela a existência do recurso alheio.
        /// </summary>
        /// <remarks>
        /// A aba Documentos vem SEM url. A URL temporária é emitida por
        /// <see cref="UrlDocumento"/>, com expiração própria: embutir um link aqui o
        /// faria nascer junto com a tela e viver enquanto a aba ficasse aberta.
        /// </remarks>
        [HttpGet("analises/{id}")]
        public async Task<IActionResult> Detalhe(int id)
        {
            var onboarding = await _tenant.Onboardings
                .Include(o => o.ResponsavelAnalise)
                .FirstOrDefaultAsync(o => o.Id == id);

            if (onboarding == null)
                return NotFound(new { message = "Análise não encontrada." });

            var responsavel = await _db.OrganizacaoResponsaveisLegais.AsNoTracking()
                .FirstOrDefaultAsync(r => r.IdOnboarding == id);

            var documentos = await _tenant.OnboardingDocumentos.AsNoTracking()
                .Where(d => d.IdOnboarding == id)
                .Select(d => new { d.Id, d.Tipo, d.Status, d.MotivoPendencia })
                .ToListAsync();

            var historico = await _tenant.OnboardingDecisoes.AsNoTracking()
                .Where(d => d.IdOnboarding == id)
                .OrderByDescending(d => d.DataHora)
                .Select(d => new
                {
                    d.Id,
                    d.Estado,
                    d.Justificativa,
                    d.CamposPendentesJson,
                    d.DataHora,
                    Usuario = d.Usuario == null ? null : d.Usuario.Nome
                })
                .ToListAsync();

            // ConviteOrganizacao aponta para a ORGANIZACAO, nao para o onboarding: os
            // convites existem depois que ela e criada. Enquanto o KYC nao foi aprovado
            // nao ha IdOrganizacao, e a aba nasce vazia - que e o estado correto, nao um
            // erro. O recorte por IdAfiliada impede que o onboarding de uma exibidora
            // enxergue convites emitidos por outra para a mesma organizacao.
            var convites = onboarding.IdOrganizacao == null
                ? new List<object>()
                : (await _db.ConvitesOrganizacao.AsNoTracking()
                    .Where(c => c.IdOrganizacaoAlvo == onboarding.IdOrganizacao.Value
                             && c.IdAfiliada == _tenant.AfiliadaId)
                    .Select(c => new { c.Id, c.Email, c.Papel, c.Estado, c.Audience, c.DataExpiracao })
                    .ToListAsync()).Cast<object>().ToList();

            return Ok(new
            {
                onboarding.Id,
                Tipo = onboarding.TipoOrganizacao,
                onboarding.Estado,
                onboarding.DataEnvio,
                onboarding.DataDecisao,
                onboarding.IdOrganizacao,
                AnalistaId = onboarding.IdResponsavelAnalise,
                AnalistaNome = onboarding.ResponsavelAnalise?.Nome,
                ResponsavelLegal = responsavel == null ? null : new
                {
                    responsavel.Nome,
                    responsavel.Cpf,
                    responsavel.Cargo,
                    responsavel.Email,
                    responsavel.Celular,
                    responsavel.DeclaracaoPoderes
                },
                Documentos = documentos,
                UsuariosEConvites = convites,
                Historico = historico
            });
        }

        /// <summary>
        /// Aprova o KYC — a ÚNICA via que cria o vínculo com a afiliada (PRD §8.13).
        /// </summary>
        /// <remarks>
        /// <para>Ordem transacional obrigatória: criar o vínculo, tornar o solicitante
        /// Owner, ativar a organização. Falha em qualquer etapa reverte tudo — nenhuma
        /// organização fica ativa sem Owner, nem vinculada sem estar ativa.</para>
        ///
        /// <para>Consequência testável de §8.13: organização não aprovada NÃO aparece
        /// nas listagens comerciais, porque as listagens de Agências e Anunciantes
        /// partem de <c>AfiliadaAgencia</c>/<c>AfiliadaCliente</c> — e é aqui que essas
        /// linhas nascem.</para>
        /// </remarks>
        [HttpPost("analises/{id}/aprovar")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public Task<IActionResult> Aprovar(int id, [FromBody] KycDecisaoRequest request) =>
            Decidir(id, EstadoOnboardingEnum.Aprovado, request, exigeJustificativa: true, aplicarVinculo: true);

        /// <summary>
        /// Solicita ajustes. EXIGE os campos/documentos pendentes além da justificativa.
        /// </summary>
        /// <remarks>
        /// A validação é do SERVIDOR, não do formulário: chamar a API direto sem
        /// <c>CamposPendentes</c> falha igual. Um pedido de ajuste sem dizer o que
        /// ajustar devolve o solicitante ao início sem informação, e é o tipo de coisa
        /// que só o frontend costuma impedir.
        /// </remarks>
        [HttpPost("analises/{id}/ajustes")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Ajustes(int id, [FromBody] KycDecisaoRequest request)
        {
            if (request?.CamposPendentes == null || request.CamposPendentes.Count == 0)
                return BadRequest(new
                {
                    message = "Indique ao menos um campo ou documento pendente para solicitar ajustes."
                });

            return await Decidir(id, EstadoOnboardingEnum.AjustesSolicitados, request,
                exigeJustificativa: true, aplicarVinculo: false);
        }

        [HttpPost("analises/{id}/rejeitar")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public Task<IActionResult> Rejeitar(int id, [FromBody] KycDecisaoRequest request) =>
            Decidir(id, EstadoOnboardingEnum.Rejeitado, request, exigeJustificativa: true, aplicarVinculo: false);

        /// <summary>
        /// Suspende uma organização já APROVADA. É ação pós-aprovação — e é por isso
        /// que <c>Suspenso</c> não aparece na fila de triagem (VEI-RD-80).
        /// </summary>
        [HttpPost("analises/{id}/suspender")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Suspender(int id, [FromBody] KycDecisaoRequest request)
        {
            var onboarding = await _tenant.Onboardings.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
            if (onboarding == null)
                return NotFound(new { message = "Análise não encontrada." });

            if (onboarding.Estado != EstadoOnboardingEnum.Aprovado)
                return Conflict(new
                {
                    message = "Só é possível suspender uma organização já aprovada."
                });

            return await Decidir(id, EstadoOnboardingEnum.Suspenso, request,
                exigeJustificativa: true, aplicarVinculo: false, inativarVinculo: true);
        }

        [HttpPost("analises/{id}/reativar")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Reativar(int id, [FromBody] KycDecisaoRequest request)
        {
            var onboarding = await _tenant.Onboardings.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
            if (onboarding == null)
                return NotFound(new { message = "Análise não encontrada." });

            if (onboarding.Estado != EstadoOnboardingEnum.Suspenso)
                return Conflict(new { message = "Só é possível reativar uma organização suspensa." });

            return await Decidir(id, EstadoOnboardingEnum.Aprovado, request,
                exigeJustificativa: false, aplicarVinculo: false, reativarVinculo: true);
        }

        // ------------------------------------------------------------------
        // VEI-RD-81 task d — URL temporária de documento
        // ------------------------------------------------------------------

        /// <summary>
        /// Emite uma URL TEMPORÁRIA para o documento. Nunca uma URL permanente.
        /// </summary>
        /// <remarks>
        /// <para>Documento de outra afiliada devolve 404 — mesmo para um operador com
        /// permissão de KYC. O recorte vem de <see cref="ITenantQueries.OnboardingDocumentos"/>,
        /// que filtra pela afiliada do onboarding pai, e não de uma checagem escrita
        /// aqui que alguém pudesse esquecer no próximo endpoint.</para>
        ///
        /// <para>Nada sensível vai para log: nem a chave do blob, nem o token, nem
        /// dados do documento (PRD §7).</para>
        /// </remarks>
        [HttpGet("documentos/{id}/url")]
        public async Task<IActionResult> UrlDocumento(int id)
        {
            var documento = await _tenant.OnboardingDocumentos.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id);

            if (documento == null)
                return NotFound(new { message = "Documento não encontrado." });

            var ttl = TimeSpan.FromMinutes(
                _config.GetValue<int?>("WlKyc:DocumentoUrlTtlMinutos") ?? 5);

            var token = _links.Assinar(
                WlLinkTemporario.PropositoDocumentoKyc,
                id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _tenant.AfiliadaId,
                ttl);

            return Ok(new
            {
                Url = Url.Action(nameof(ConteudoDocumento), "Kyc", new { id, token = token.Token }),
                ExpiraEm = token.ExpiraEm,
                TtlSegundos = (int)ttl.TotalSeconds
            });
        }

        /// <summary>Entrega o conteúdo do documento, só com um token válido e não expirado.</summary>
        [HttpGet("documentos/{id}/conteudo")]
        public async Task<IActionResult> ConteudoDocumento(int id, [FromQuery] string token, CancellationToken ct)
        {
            var jti = _links.Validar(
                token,
                WlLinkTemporario.PropositoDocumentoKyc,
                id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _tenant.AfiliadaId);

            if (jti == null)
                return NotFound(new { message = "Link expirado ou inválido." });

            var documento = await _tenant.OnboardingDocumentos.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id);

            if (documento == null || string.IsNullOrWhiteSpace(documento.ChaveArquivo))
                return NotFound(new { message = "Documento não encontrado." });

            var chave = documento.ChaveArquivo.StartsWith(WlUploadReferences.Prefix, StringComparison.Ordinal)
                ? documento.ChaveArquivo.Substring(WlUploadReferences.Prefix.Length)
                : documento.ChaveArquivo;

            var info = await _storage.InfoAsync(chave, ct);
            var conteudo = await _storage.ReadAsync(chave, ct);

            return File(conteudo, info.ContentType ?? "application/octet-stream", info.FileName);
        }

        // ------------------------------------------------------------------

        private IQueryable<OrganizacaoOnboarding> FiltrarFila(
            string tipo, string estado, int? analistaId, DateTime? dataEnvio, string busca)
        {
            // Rascunho nunca entra na fila: o solicitante ainda não enviou nada para
            // analisar. Suspenso é pós-aprovação, tratado no detalhe.
            var query = _tenant.Onboardings.Where(o => EstadosDaFila.Contains(o.Estado));

            if (Enum.TryParse<TipoOrganizacaoEnum>(tipo, ignoreCase: true, out var tipoOrganizacao))
                query = query.Where(o => o.TipoOrganizacao == tipoOrganizacao);

            if (Enum.TryParse<EstadoOnboardingEnum>(estado, ignoreCase: true, out var estadoFiltro)
                && EstadosDaFila.Contains(estadoFiltro))
                query = query.Where(o => o.Estado == estadoFiltro);

            if (analistaId.HasValue)
                query = query.Where(o => o.IdResponsavelAnalise == analistaId.Value);

            if (dataEnvio.HasValue)
            {
                // O filtro é por DIA, não por instante: DataEnvio guarda data e hora
                // (a fila mostra "10/08/2026 09:12"), então igualdade exata nunca casaria
                // com o que o operador digita em dd/mm/aaaa.
                var inicio = dataEnvio.Value.Date;
                var fim = inicio.AddDays(1);
                query = query.Where(o => o.DataEnvio >= inicio && o.DataEnvio < fim);
            }

            if (!string.IsNullOrWhiteSpace(busca))
            {
                var termo = busca.Trim();
                query = query.Where(o =>
                    _db.OrganizacaoResponsaveisLegais.Any(r =>
                        r.IdOnboarding == o.Id &&
                        (r.Nome.Contains(termo) || r.Cpf.Contains(termo))));
            }

            return query;
        }

        /// <summary>
        /// Caminho comum das decisões: valida, muda o estado, aplica o efeito no vínculo
        /// e grava a linha append-only de <c>OrganizacaoOnboardingDecisao</c>.
        /// </summary>
        /// <remarks>
        /// Tudo numa transação. A decisão e o efeito dela são o mesmo fato: gravar o
        /// histórico dizendo "aprovado" sem que o vínculo exista deixaria a auditoria
        /// afirmando algo que o sistema não fez.
        /// </remarks>
        private async Task<IActionResult> Decidir(
            int id,
            EstadoOnboardingEnum novoEstado,
            KycDecisaoRequest request,
            bool exigeJustificativa,
            bool aplicarVinculo,
            bool inativarVinculo = false,
            bool reativarVinculo = false)
        {
            if (exigeJustificativa && string.IsNullOrWhiteSpace(request?.Justificativa))
                return BadRequest(new { message = "A justificativa é obrigatória e fica registrada no histórico." });

            var operadorId = WlUsuarioId;
            if (operadorId == null)
                return Unauthorized();

            var onboarding = await _tenant.Onboardings.FirstOrDefaultAsync(o => o.Id == id);
            if (onboarding == null)
                return NotFound(new { message = "Análise não encontrada." });

            var usuario = await _db.Usuarios.FirstOrDefaultAsync(u => u.Id == operadorId.Value);
            if (usuario == null)
                return Unauthorized();

            using var transacao = _db.Database.BeginTransaction();
            try
            {
                if (aplicarVinculo)
                {
                    var erro = await CriarVinculoDaAprovacao(onboarding, usuario);
                    if (erro != null)
                    {
                        transacao.Rollback();
                        return Conflict(new { message = erro });
                    }
                }

                if (inativarVinculo || reativarVinculo)
                    await AlternarVinculo(onboarding, ativo: reativarVinculo);

                await _db.Database.ExecuteSqlCommandAsync(
                    "UPDATE OrganizacaoOnboarding SET Estado = @p0, DataDecisao = @p1 WHERE Id = @p2 AND IdAfiliada = @p3",
                    (int)novoEstado, DateTime.UtcNow, id, _tenant.AfiliadaId);

                var camposPendentes = request?.CamposPendentes == null || request.CamposPendentes.Count == 0
                    ? null
                    : JsonConvert.SerializeObject(request.CamposPendentes);

                var decisao = new OrganizacaoOnboardingDecisao(
                    onboarding, novoEstado, request?.Justificativa, camposPendentes, usuario);

                if (!decisao.IsValid())
                {
                    transacao.Rollback();
                    return BadRequest(new { message = "Decisão inválida.", detalhe = decisao.Notifications });
                }

                _db.OrganizacaoOnboardingDecisoes.Add(decisao);
                await _db.SaveChangesAsync();
                transacao.Commit();

                return Ok(new { Id = id, Estado = novoEstado });
            }
            catch
            {
                transacao.Rollback();
                throw;
            }
        }

        /// <summary>
        /// Cria o vínculo da aprovação: <c>AfiliadaAgencia</c> se o onboarding é de
        /// Agência, <c>AfiliadaCliente</c> se é de Anunciante.
        /// </summary>
        private async Task<string> CriarVinculoDaAprovacao(OrganizacaoOnboarding onboarding, Usuario usuario)
        {
            if (onboarding.IdOrganizacao == null)
                return "A organização ainda não foi criada no Core — não há o que vincular.";

            var afiliada = await _db.Afiliadas.FirstOrDefaultAsync(a => a.Id == _tenant.AfiliadaId);
            if (afiliada == null)
                return "Exibidora não encontrada.";

            if (onboarding.TipoOrganizacao == TipoOrganizacaoEnum.Agencia)
            {
                var agencia = await _db.Agencias.FirstOrDefaultAsync(a => a.Id == onboarding.IdOrganizacao.Value);
                if (agencia == null) return "Agência não encontrada.";

                var jaExiste = await _db.AfiliadaAgencias
                    .AnyAsync(v => v.IdAfiliada == afiliada.Id && v.IdAgencia == agencia.Id);
                if (jaExiste) return null;

                var vinculo = new AfiliadaAgencia(afiliada, agencia, usuario);
                vinculo.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, afiliada.Id, usuario.Id);
                if (!vinculo.IsValid()) return "Não foi possível criar o vínculo da agência.";
                _db.AfiliadaAgencias.Add(vinculo);
            }
            else
            {
                var cliente = await _db.Clientes.FirstOrDefaultAsync(c => c.Id == onboarding.IdOrganizacao.Value);
                if (cliente == null) return "Anunciante não encontrado.";

                var jaExiste = await _db.AfiliadaClientes
                    .AnyAsync(v => v.IdAfiliada == afiliada.Id && v.IdCliente == cliente.Id);
                if (jaExiste) return null;

                var vinculo = new AfiliadaCliente(afiliada, cliente, usuario);
                vinculo.RegistrarOrigem(FonteOrigemEnum.WhiteLabel, afiliada.Id, usuario.Id);
                if (!vinculo.IsValid()) return "Não foi possível criar o vínculo do anunciante.";
                _db.AfiliadaClientes.Add(vinculo);
            }

            return null;
        }

        private async Task AlternarVinculo(OrganizacaoOnboarding onboarding, bool ativo)
        {
            if (onboarding.IdOrganizacao == null) return;

            if (onboarding.TipoOrganizacao == TipoOrganizacaoEnum.Agencia)
            {
                var vinculo = await _tenant.AfiliadaAgencias
                    .FirstOrDefaultAsync(v => v.IdAgencia == onboarding.IdOrganizacao.Value);
                if (vinculo == null) return;
                if (ativo) vinculo.Ativar(); else vinculo.Inativar();
            }
            else
            {
                var vinculo = await _tenant.AfiliadaClientes
                    .FirstOrDefaultAsync(v => v.IdCliente == onboarding.IdOrganizacao.Value);
                if (vinculo == null) return;
                if (ativo) vinculo.Ativar(); else vinculo.Inativar();
            }
        }
    }

    public sealed class KycDecisaoRequest
    {
        public string Justificativa { get; set; }

        /// <summary>
        /// Campos e documentos pendentes. Obrigatório em <c>/ajustes</c>: sem isto o
        /// solicitante recebe "faltou algo" sem saber o quê.
        /// </summary>
        public List<string> CamposPendentes { get; set; }
    }
}
