using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Entities.OrdensServico;
using Veiculando.Domain.Enums;
using Veiculando.Domain.Repositories;
using Veiculando.Domain.ValueObjects;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Ordem de Serviço (VEI-RD-88) — geração, listagem, detalhe, PDF e histórico.
    /// </summary>
    /// <remarks>
    /// <para><b>Permissão.</b> Reusa <c>PecaGerenciar</c>: uma OS é, no fim, uma
    /// operação sobre peças (selecionar quais vão para a rua). Uma permissão
    /// dedicada (<c>OrdemServicoGerenciar</c>) exigiria uma entrada nova em
    /// <c>WlPermissoesValidas</c> (Core) e uma migration de claims para os
    /// operadores existentes — o mesmo tipo de mudança que VEI-RD-93 fez para
    /// <c>ProgramacaoVisualizar</c>. Isso é decisão de produto/Core, fora do
    /// escopo deste BFF nesta sprint; documentado aqui para quem for revisar.</para>
    ///
    /// <para><b>Sem /atribuir nem /reatribuir.</b> Decisão humana fechada
    /// (ver instruções da sprint): atribuição a colador está fora de escopo.
    /// <c>OrdemServico.Responsavel</c> nasce sempre nulo e não existe, em
    /// nenhum lugar deste controller, um caminho que o preencha.</para>
    ///
    /// <para><b>Quem cria, no Core.</b> <c>OrdemServico</c> não tem
    /// Command/Handler no core — nasce direto por EF6, como
    /// <c>UploadsController.FotoChecking</c> já faz para <c>Checking</c>. O
    /// <c>UsuarioCadastro</c> registrado é a CONTA DE SERVIÇO do tenant, não o
    /// operador WL que clicou "gerar" — mesma limitação que já existe para
    /// Checking, não uma nova.</para>
    /// </remarks>
    [ApiController]
    [Route("api/wl/ordens-servico")]
    [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
    public class OrdensServicoController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly IOrdemServicoRepository _ordemServicoRepo;
        private readonly ISeedAccountResolver _seed;
        private readonly ILogger<OrdensServicoController> _logger;

        private static readonly string[] MesesAbrev =
            { "Jan", "Fev", "Mar", "Abr", "Mai", "Jun", "Jul", "Ago", "Set", "Out", "Nov", "Dez" };

        public OrdensServicoController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            IOrdemServicoRepository ordemServicoRepo,
            ISeedAccountResolver seed,
            ILogger<OrdensServicoController> logger)
        {
            _db = db;
            _tenant = tenant;
            _ordemServicoRepo = ordemServicoRepo;
            _seed = seed;
            _logger = logger;
        }

        public const string MsgPeriodoInvertido = "Período inicial deve ser anterior ou igual ao período final";

        /// <summary>
        /// Listagem paginada: OS Nº, Período, Cidade(s), Responsável, Peças,
        /// Status, Criada em. Filtros: período (intervalo), status, responsável.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string status,
            [FromQuery] int? idResponsavel,
            [FromQuery] int? idPeriodoInicial,
            [FromQuery] int? idPeriodoFinal,
            [FromQuery] WlPaginaRequest pagina)
        {
            var (page, pageSize) = WlPaginacao.Normalizar(pagina);
            var sort = WlPaginacao.Ordenacao(pagina?.Sort, "criadaEm", "criadaEm", "numero", "status");
            var desc = pagina?.Desc ?? true;

            var query = _tenant.OrdensServico.AsQueryable();

            // Status oficiais (VEI-RD-88a): Aberta/Atribuida/EmExecucao/Concluida
            // — mas so "Aberta" e alcancavel nesta sprint, entao filtrar pelos
            // outros tres sempre devolve pagina vazia. Isso e esperado, nao bug.
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<StatusOrdemServicoEnum>(status, ignoreCase: true, out var statusEnum))
                {
                    return BadRequest(new
                    {
                        message = "Status inválido. Valores aceitos: Aberta, Atribuida, EmExecucao, Concluida."
                    });
                }
                query = query.Where(o => o.Status == statusEnum);
            }

            if (idResponsavel.HasValue)
                query = query.Where(o => o.IdResponsavel == idResponsavel.Value);

            if (idPeriodoInicial.HasValue || idPeriodoFinal.HasValue)
            {
                DateTime? dataInicio = null;
                DateTime? dataFim = null;

                if (idPeriodoInicial.HasValue)
                {
                    dataInicio = await _db.Periodos
                        .Where(p => p.Id == idPeriodoInicial.Value)
                        .Select(p => (DateTime?)p.DataInicio)
                        .FirstOrDefaultAsync();

                    if (dataInicio == null)
                        return BadRequest(new { message = "Período inicial não encontrado." });
                }

                if (idPeriodoFinal.HasValue)
                {
                    dataFim = await _db.Periodos
                        .Where(p => p.Id == idPeriodoFinal.Value)
                        .Select(p => (DateTime?)p.DataInicio)
                        .FirstOrDefaultAsync();

                    if (dataFim == null)
                        return BadRequest(new { message = "Período final não encontrado." });
                }

                if (dataInicio.HasValue && dataFim.HasValue && dataInicio.Value > dataFim.Value)
                    return BadRequest(new { message = MsgPeriodoInvertido });

                // Uma OS tem um UNICO periodo (nao uma colecao de itens), entao
                // os dois limites comparam direto contra o mesmo campo — sem o
                // risco de "Any" combinando limites de linhas diferentes que
                // apareceu em VEI-RD-94/91.
                if (dataInicio.HasValue)
                    query = query.Where(o => o.Periodo.DataInicio >= dataInicio.Value);

                if (dataFim.HasValue)
                    query = query.Where(o => o.Periodo.DataInicio <= dataFim.Value);
            }

            var total = await query.CountAsync();

            var ordenada = (sort, desc) switch
            {
                ("numero", false) => query.OrderBy(o => o.Numero).ThenBy(o => o.Id),
                ("numero", true) => query.OrderByDescending(o => o.Numero).ThenBy(o => o.Id),
                ("status", false) => query.OrderBy(o => o.Status).ThenBy(o => o.Id),
                ("status", true) => query.OrderByDescending(o => o.Status).ThenBy(o => o.Id),
                (_, false) => query.OrderBy(o => o.DataCadastro).ThenBy(o => o.Id),
                (_, true) => query.OrderByDescending(o => o.DataCadastro).ThenBy(o => o.Id),
            };

            // Include + materializar so a PAGINA e projetar em memoria — mesma
            // cautela de CheckingController.GetAll com a colecao de cidades
            // distintas, que e demais para um Select traduzido para SQL.
            var entidadesPagina = await ordenada
                .Include(o => o.Periodo)
                .Include(o => o.Responsavel)
                .Include(o => o.Cidades.Select(c => c.Cidade))
                .Include(o => o.Pecas)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var itens = entidadesPagina
                .Select(o => new
                {
                    o.Id,
                    o.NumeroFormatado,
                    Periodo = o.Periodo?.Nome,
                    Cidades = o.Cidades
                        .Select(c => c.Cidade?.Nome)
                        .Where(nome => nome != null)
                        .Distinct()
                        .ToList(),
                    // Nunca populado nesta sprint (sem atribuicao a colador) —
                    // null e o dado correto aqui, nao um "null silencioso"
                    // escondendo um valor real como a Agencia de VEI-RD-79. O
                    // rotulo "—"/"Não atribuída" é decisão de exibição do front.
                    Responsavel = o.Responsavel?.Nome,
                    PecasCount = o.Pecas.Count,
                    Status = o.Status.ToString(),
                    o.DataCadastro
                })
                .ToList();

            return Ok(WlPaginacao.Montar(itens, page, pageSize, total));
        }

        /// <summary>
        /// Gera a OS a partir das peças selecionadas (filtros de período/cidade/
        /// status/opções de exibição acontecem ANTES disso, na tela — via
        /// <c>/api/wl/programacao/listar</c> e os lookups já existentes; este
        /// endpoint só recebe os ids finais). Valida no servidor que toda peça
        /// pertence a um local da afiliada do tenant — nunca confia no cliente.
        /// </summary>
        [HttpPost]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Create([FromBody] OrdemServicoCriarRequest request, CancellationToken ct)
        {
            if (request?.IdPecas == null || request.IdPecas.Count == 0)
                return BadRequest(new { message = "Selecione ao menos uma peça." });

            var periodo = await _db.Periodos.FirstOrDefaultAsync(p => p.Id == request.IdPeriodo, ct);
            if (periodo == null)
                return BadRequest(new { message = "Período não encontrado." });

            var idsSolicitados = request.IdPecas.Distinct().ToList();

            // `_tenant.Pecas` ja vem recortado por `Local.IdAfiliada == AfiliadaId`
            // (ITenantQueries) — uma peca de outra afiliada simplesmente nao
            // aparece aqui, entao a contagem abaixo detecta o desvio sem
            // precisar reescrever o filtro de tenant.
            var pecas = await _tenant.Pecas
                .Include(p => p.Local.Cidade)
                .Where(p => idsSolicitados.Contains(p.Id))
                .ToListAsync(ct);

            if (pecas.Count != idsSolicitados.Count)
            {
                return BadRequest(new
                {
                    message = "Uma ou mais peças selecionadas não existem ou não pertencem a esta exibidora."
                });
            }

            var afiliada = await _db.Afiliadas.FirstOrDefaultAsync(a => a.Id == _tenant.AfiliadaId, ct);
            if (afiliada == null)
                return StatusCode(503, new { message = "Afiliada não disponível. Nenhuma OS foi criada." });

            // Mesmo padrao de UploadsController.FotoChecking: OrdemServico nao
            // tem Command/Handler no core, entao quem grava e este BFF direto
            // por EF6, atribuindo a conta de servico do tenant.
            UsuarioAfiliada actor;
            try
            {
                var account = _seed.Resolve();
                actor = await _db.UsuariosAfiliada.SingleOrDefaultAsync(u => u.IdAfiliada == _tenant.AfiliadaId
                    && u.Email.Endereco == account.Email && u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
                if (actor == null) throw new InvalidOperationException("Conta de serviço Core ausente no tenant.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "WL_OS_CORE_ACTOR_UNAVAILABLE tenant={Tenant}", _tenant.AfiliadaId);
                return StatusCode(503, new
                {
                    message = "A conta de serviço da exibidora não está disponível. Nenhuma OS foi criada."
                });
            }

            var numero = _ordemServicoRepo.ProximoNumero(_tenant.AfiliadaId);

            var cidades = pecas
                .Where(p => p.Local?.Cidade != null)
                .Select(p => p.Local.Cidade)
                .GroupBy(c => c.Id)
                .Select(g => g.First())
                .ToList();

            var ordem = new OrdemServico(afiliada, periodo, numero, cidades, pecas, actor);

            if (!ordem.IsValid())
            {
                return BadRequest(new
                {
                    message = "Não foi possível criar a ordem de serviço.",
                    notificacoes = ordem.Notifications.Select(n => n.Message)
                });
            }

            _ordemServicoRepo.Save(ordem);
            await _db.SaveChangesAsync(ct);

            return CreatedAtAction(nameof(GetById), new { id = ordem.Id }, new
            {
                ordem.Id,
                ordem.NumeroFormatado,
                PecasCount = pecas.Count
            });
        }

        /// <summary>
        /// Detalhe: info card (status, peças na OS, criada por — sem campos de
        /// colador), tabela de peças (sem Colagem: Data Colagem "—", Status
        /// Colagem "Pendente" para todas, porque colagem não existe nesta
        /// sprint) e o histórico embutido (espelha <see cref="GetHistorico"/>).
        /// </summary>
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id, CancellationToken ct)
        {
            var ordem = await CarregarComDetalhesAsync(id, ct);
            if (ordem == null)
                return NotFound(new { message = "Ordem de serviço não encontrada." });

            return Ok(new
            {
                ordem.Id,
                ordem.NumeroFormatado,
                Status = ordem.Status.ToString(),
                Periodo = ordem.Periodo?.Nome,
                Cidades = ordem.Cidades.Select(c => c.Cidade?.Nome).Where(n => n != null).Distinct().ToList(),
                Responsavel = ordem.Responsavel?.Nome,
                // Conta de serviço do tenant, não o operador WL que gerou a OS
                // — ver o comentário na classe sobre a ausência de Command no
                // core para este módulo.
                CriadaPor = ordem.UsuarioCadastro?.Nome,
                ordem.DataCadastro,
                Pecas = MontarPecasDto(ordem),
                Historico = MontarHistoricoDto(ordem)
            });
        }

        /// <summary>
        /// Histórico append-only da OS. Nesta sprint só existe o evento de
        /// criação ("OS gerada com N peças selecionadas" — texto do BFF, ver
        /// remarks) porque não há nenhuma transição de status alcançável.
        /// </summary>
        /// <remarks>
        /// O construtor de <c>OrdemServico</c> no core já registra um evento
        /// próprio ("Ordem de servico criada", texto fixo, sem o N de peças) —
        /// esse eu não controlo e não edito o core para trocar. Este endpoint
        /// devolve o histórico exatamente como ele está gravado; se o texto
        /// exato "OS gerada com N peças selecionadas" for um requisito rígido
        /// de produto, o ajuste é no construtor de <c>OrdemServico</c>
        /// (Core), não aqui.
        /// </remarks>
        [HttpGet("{id:int}/historico")]
        public async Task<IActionResult> GetHistorico(int id, CancellationToken ct)
        {
            var ordem = await CarregarComDetalhesAsync(id, ct);
            if (ordem == null)
                return NotFound(new { message = "Ordem de serviço não encontrada." });

            return Ok(MontarHistoricoDto(ordem));
        }

        /// <summary>
        /// PDF A4: cabeçalho com dados da Exibidora, agrupamento
        /// cidade/bairro/endereço → peça, colunas Código/Campanha Anterior/
        /// Campanha Atual/Data Colado/Foto/Motivo — Data Colado e Motivo
        /// sempre em branco (preenchimento manual, sem colagem nesta sprint).
        /// </summary>
        /// <remarks>
        /// Gerado neste BFF com PdfSharpCore (MIT, sem dependência nativa) —
        /// diferente do PDF de PI, que só é um proxy para o FileServer
        /// (<c>WlPiPdfSource</c> não gera nada, só busca bytes prontos). Não
        /// existe rota de OS no FileServer para proxear, então geração própria
        /// era a única opção sem esperar por outro serviço.
        /// </remarks>
        [HttpGet("{id:int}/pdf")]
        public async Task<IActionResult> GetPdf(int id, CancellationToken ct)
        {
            var ordem = await CarregarComDetalhesAsync(id, ct);
            if (ordem == null)
                return NotFound(new { message = "Ordem de serviço não encontrada." });

            var periodoAnterior = await _db.Periodos
                .Where(p => p.Periodicidade.Tipo == ordem.Periodo.Periodicidade.Tipo
                    && p.DataInicio < ordem.Periodo.DataInicio)
                .OrderByDescending(p => p.DataInicio)
                .FirstOrDefaultAsync(ct);

            var idsPeriodo = new List<int> { ordem.Periodo.Id };
            if (periodoAnterior != null)
                idsPeriodo.Add(periodoAnterior.Id);

            var idsPeca = ordem.Pecas.Select(p => p.IdPeca).ToList();

            var statusRows = await _db.PecaPeriodoStatus
                .Where(pps => idsPeca.Contains(pps.IdPeca) && idsPeriodo.Contains(pps.IdPeriodo))
                .Include(pps => pps.Pedido.Campanha)
                .Include(pps => pps.PedidoReserva.Pedido.Campanha)
                .Include(pps => pps.PedidoInsercao.Pedido.Campanha)
                .ToListAsync(ct);

            var bytes = GerarPdf(ordem, periodoAnterior, statusRows);

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            var nomeArquivo = "OS-" + ordem.Numero.ToString("D4") + ".pdf";
            return File(bytes, "application/pdf", nomeArquivo);
        }

        private Task<OrdemServico> CarregarComDetalhesAsync(int id, CancellationToken ct) =>
            _tenant.OrdensServico
                .Include(o => o.Afiliada)
                .Include(o => o.Periodo)
                .Include(o => o.Responsavel)
                .Include(o => o.UsuarioCadastro)
                .Include(o => o.Cidades.Select(c => c.Cidade))
                // `Local.Endereco` é ComplexType (ver EnderecoMap/LocalMap), não
                // uma entidade relacionada — vem junto do Local automaticamente.
                // Um `.Include` explícito nele lança
                // "does not declare a navigation property with the name 'Endereco'".
                .Include(o => o.Pecas.Select(p => p.Peca.Local.Cidade))
                .Include(o => o.Historico.Select(h => h.Usuario))
                .FirstOrDefaultAsync(o => o.Id == id, ct);

        private static List<object> MontarPecasDto(OrdemServico ordem) =>
            ordem.Pecas
                .Where(p => p.Peca != null)
                .Select(p => new
                {
                    p.Peca.Codigo,
                    LocalCodigo = p.Peca.Local?.Codigo,
                    LocalDescricao = p.Peca.Local?.Descricao,
                    Cidade = p.Peca.Local?.Cidade?.Nome,
                    // Sem colagem nesta sprint (VEI-RD-88): estes dois campos
                    // são sempre este valor fixo — nunca preenchidos por
                    // nenhum fluxo do BFF ou do core.
                    DataColagem = (DateTime?)null,
                    StatusColagem = "Pendente"
                })
                .Cast<object>()
                .ToList();

        private static List<object> MontarHistoricoDto(OrdemServico ordem) =>
            ordem.Historico
                .OrderBy(h => h.DataHora)
                .Select(h => new
                {
                    h.Evento,
                    h.DataHora,
                    Usuario = h.Usuario?.Nome
                })
                .Cast<object>()
                .ToList();

        private static string ResolveCampanhaNome(PecaPeriodoStatus pps)
        {
            if (pps == null) return null;
            var campanha = pps.PedidoInsercao?.Pedido?.Campanha
                ?? pps.PedidoReserva?.Pedido?.Campanha
                ?? pps.Pedido?.Campanha;
            return campanha?.Nome;
        }

        private static string FormatoMesAno(DateTime data) => $"{MesesAbrev[data.Month - 1]}/{data:yy}";

        private static string FormatarCampanhaAnterior(PecaPeriodoStatus anterior, Periodo periodoAnterior)
        {
            var nome = ResolveCampanhaNome(anterior);
            if (nome == null || periodoAnterior == null)
                return "—";
            return $"{nome} — {FormatoMesAno(periodoAnterior.DataInicio)}";
        }

        private static string FormatarEndereco(Endereco endereco)
        {
            if (endereco == null || string.IsNullOrWhiteSpace(endereco.Logradouro))
                return "—";
            return string.IsNullOrWhiteSpace(endereco.Numero)
                ? endereco.Logradouro
                : $"{endereco.Logradouro}, {endereco.Numero}";
        }

        private byte[] GerarPdf(OrdemServico ordem, Periodo periodoAnterior, List<PecaPeriodoStatus> statusRows)
        {
            var afiliada = ordem.Afiliada;

            using var document = new PdfDocument();
            var page = document.AddPage();
            page.Size = PdfSharpCore.PageSize.A4;
            var gfx = XGraphics.FromPdfPage(page);

            var fontTitulo = new XFont("Arial", 16, XFontStyle.Bold);
            var fontTexto = new XFont("Arial", 10, XFontStyle.Regular);
            var fontSecao = new XFont("Arial", 12, XFontStyle.Bold);
            var fontSubSecao = new XFont("Arial", 10, XFontStyle.Bold);
            var fontCabecalhoTabela = new XFont("Arial", 8, XFontStyle.Bold);
            var fontCelula = new XFont("Arial", 8, XFontStyle.Regular);

            const double margem = 40;
            double y = margem;
            var largura = page.Width.Point - margem * 2;

            void GarantirEspaco(double alturaNecessaria)
            {
                if (y + alturaNecessaria <= page.Height.Point - margem) return;
                page = document.AddPage();
                page.Size = PdfSharpCore.PageSize.A4;
                gfx = XGraphics.FromPdfPage(page);
                y = margem;
            }

            void Texto(string texto, XFont fonte, double altura)
            {
                gfx.DrawString(texto ?? string.Empty, fonte, XBrushes.Black,
                    new XRect(margem, y, largura, altura), XStringFormats.TopLeft);
                y += altura;
            }

            // Cabecalho: dados da Exibidora/Afiliada.
            Texto(afiliada?.Nome ?? "Exibidora", fontTitulo, 22);
            if (afiliada?.Endereco != null)
                Texto(
                    $"{FormatarEndereco(afiliada.Endereco)} — {afiliada.Cidade}/{afiliada.Uf}".Trim(' ', '—'),
                    fontTexto, 14);
            if (afiliada?.Cnpj != null)
                Texto($"CNPJ: {afiliada.Cnpj.Numero}", fontTexto, 14);
            y += 6;

            Texto($"{ordem.NumeroFormatado} — Período: {ordem.Periodo?.Nome}", fontSecao, 18);
            y += 4;

            var colunas = new[] { "CÓDIGO", "CAMPANHA ANTERIOR", "CAMPANHA ATUAL", "DATA COLADO", "FOTO", "MOTIVO" };
            var proporcoes = new[] { 0.12, 0.24, 0.24, 0.13, 0.1, 0.17 };

            void DesenharCabecalhoTabela(double x0, double larguraTabela)
            {
                GarantirEspaco(14);
                var x = x0;
                for (var i = 0; i < colunas.Length; i++)
                {
                    var w = larguraTabela * proporcoes[i];
                    gfx.DrawString(colunas[i], fontCabecalhoTabela, XBrushes.Black,
                        new XRect(x, y, w, 12), XStringFormats.TopLeft);
                    x += w;
                }
                y += 13;
                gfx.DrawLine(XPens.Gray, x0, y, x0 + larguraTabela, y);
                y += 2;
            }

            var grupos = ordem.Pecas
                .Where(p => p.Peca?.Local != null)
                .GroupBy(p => p.Peca.Local.Cidade?.Nome ?? "—")
                .OrderBy(g => g.Key);

            foreach (var grupoCidade in grupos)
            {
                GarantirEspaco(20);
                Texto($"Cidade: {grupoCidade.Key}", fontSecao, 18);

                var porBairro = grupoCidade
                    .GroupBy(p => p.Peca.Local.Endereco?.Bairro ?? "—")
                    .OrderBy(g => g.Key);

                foreach (var grupoBairro in porBairro)
                {
                    GarantirEspaco(16);
                    Texto($"Bairro: {grupoBairro.Key}", fontSubSecao, 15);

                    var porEndereco = grupoBairro
                        .GroupBy(p => FormatarEndereco(p.Peca.Local.Endereco))
                        .OrderBy(g => g.Key);

                    foreach (var grupoEndereco in porEndereco)
                    {
                        GarantirEspaco(14);
                        Texto(grupoEndereco.Key, fontTexto, 13);

                        var larguraTabela = largura - 20;
                        DesenharCabecalhoTabela(margem + 20, larguraTabela);

                        foreach (var osPeca in grupoEndereco)
                        {
                            GarantirEspaco(13);

                            var atual = statusRows.FirstOrDefault(r =>
                                r.IdPeca == osPeca.IdPeca && r.IdPeriodo == ordem.Periodo.Id);
                            var anterior = periodoAnterior != null
                                ? statusRows.FirstOrDefault(r =>
                                    r.IdPeca == osPeca.IdPeca && r.IdPeriodo == periodoAnterior.Id)
                                : null;

                            var valores = new[]
                            {
                                osPeca.Peca.Codigo,
                                FormatarCampanhaAnterior(anterior, periodoAnterior),
                                ResolveCampanhaNome(atual) ?? "—",
                                "—", // DATA COLADO — sempre em branco (preenchimento manual)
                                "",  // FOTO — sem colagem nesta sprint
                                ""   // MOTIVO — sempre em branco (preenchimento manual)
                            };

                            var x = margem + 20;
                            for (var i = 0; i < colunas.Length; i++)
                            {
                                var w = larguraTabela * proporcoes[i];
                                gfx.DrawString(valores[i] ?? string.Empty, fontCelula, XBrushes.Black,
                                    new XRect(x, y, w, 12), XStringFormats.TopLeft);
                                x += w;
                            }
                            y += 13;
                        }
                        y += 6;
                    }
                }
            }

            using var stream = new MemoryStream();
            document.Save(stream, false);
            return stream.ToArray();
        }
    }

    public sealed class OrdemServicoCriarRequest
    {
        public int IdPeriodo { get; set; }
        public List<int> IdPecas { get; set; } = new();
    }
}
