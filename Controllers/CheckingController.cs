using System;
using System.Data.Entity;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize(Policy = AuthorizationSetup.CheckingGerenciar)]
    public class CheckingController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly IFileValidationService _fileValidation;

        public CheckingController(VeiculandoDataContext db, ITenantQueries tenant, IFileValidationService fileValidation)
        {
            _db = db;
            _tenant = tenant;
            _fileValidation = fileValidation;
        }

        /// <summary>
        /// Mesma mensagem de VEI-RD-86/94: par Período Inicial/Final invertido.
        /// </summary>
        public const string MsgPeriodoInvertido = "Período inicial deve ser anterior ou igual ao período final";

        /// <summary>
        /// Listagem de Checkings (VEI-RD-91), estilo Admin — paginada, com filtros
        /// de Período, Status, Cidade e busca por campanha/anunciante. SEM coluna
        /// Afiliada: esta instância do BFF já é mono-tenant, e <see cref="ITenantQueries.Checkings"/>
        /// nunca deixa aparecer o checking de outra exibidora.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string busca,
            [FromQuery] string status,
            [FromQuery] int? idCidade,
            [FromQuery] int? idPeriodoInicial,
            [FromQuery] int? idPeriodoFinal,
            [FromQuery] WlPaginaRequest pagina)
        {
            var (page, pageSize) = WlPaginacao.Normalizar(pagina);
            var sort = WlPaginacao.Ordenacao(pagina?.Sort, "dataCadastro", "dataCadastro", "status");
            var desc = pagina?.Desc ?? true;

            var query = _tenant.Checkings.AsQueryable();

            // Status oficiais do checking (VEI-RD-91): Finalizado, Aprovado,
            // Iniciado, Recusado, Cancelado — o enum inteiro, sem rotulo inventado.
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<StatusCheckingEnum>(status, ignoreCase: true, out var statusEnum))
                {
                    return BadRequest(new
                    {
                        message = "Status inválido. Valores aceitos: Finalizado, Aprovado, Iniciado, Recusado, Cancelado."
                    });
                }
                query = query.Where(c => c.Status == statusEnum);
            }

            if (!string.IsNullOrWhiteSpace(busca))
            {
                var termo = busca.Trim();
                query = query.Where(c =>
                    c.PedidoInsercao.Pedido.Campanha.Nome.Contains(termo) ||
                    c.PedidoInsercao.Pedido.Campanha.Cliente.Nome.Contains(termo));
            }

            if (idCidade.HasValue && idCidade.Value > 0)
            {
                query = query.Where(c => c.Itens.Any(i => i.Peca.Local.IdCidade == idCidade.Value));
            }

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

                // Mesmo cuidado de VEI-RD-94: os dois limites precisam bater no
                // MESMO item, nao em itens diferentes do mesmo checking.
                query = query.Where(c => c.Itens.Any(i =>
                    (!dataInicio.HasValue || i.PedidoInsercaoItem.PedidoItem.Periodo.DataInicio >= dataInicio.Value) &&
                    (!dataFim.HasValue || i.PedidoInsercaoItem.PedidoItem.Periodo.DataInicio <= dataFim.Value)));
            }

            var total = await query.CountAsync();

            var ordenada = (sort, desc) switch
            {
                ("status", false) => query.OrderBy(c => c.Status).ThenBy(c => c.Id),
                ("status", true) => query.OrderByDescending(c => c.Status).ThenBy(c => c.Id),
                (_, false) => query.OrderBy(c => c.DataCadastro).ThenBy(c => c.Id),
                (_, true) => query.OrderByDescending(c => c.DataCadastro).ThenBy(c => c.Id),
            };

            // Include + materializar as entidades da PAGINA (nunca da lista
            // inteira) e so entao projetar Cidades/Status em memoria — a mesma
            // classe de cuidado do Status.ToString()/Periodo.Nome documentada nos
            // outros controllers, aqui aplicada tambem a colecao de cidades
            // distintas por checking, que e demais para um Select traduzido.
            var pagina_ = await ordenada
                .Include(c => c.PedidoInsercao.Pedido.Campanha.Cliente)
                .Include(c => c.Itens.Select(i => i.Peca.Local.Cidade))
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var itens = pagina_
                .Select(c => new
                {
                    c.Id,
                    Status = c.Status.ToString(),
                    c.DataCadastro,
                    c.DataAtualizacao,
                    PiCodigo = c.PedidoInsercao?.Codigo,
                    Campanha = c.PedidoInsercao?.Pedido?.Campanha?.Nome,
                    Anunciante = c.PedidoInsercao?.Pedido?.Campanha?.Cliente?.Nome,
                    ItensCount = c.Itens.Count,
                    Cidades = c.Itens
                        .Select(i => i.Peca?.Local?.Cidade?.Nome)
                        .Where(nome => nome != null)
                        .Distinct()
                        .ToList()
                })
                .ToList();

            return Ok(WlPaginacao.Montar(itens, page, pageSize, total));
        }

        /// <summary>
        /// Detalhe do checking (VEI-RD-91): fotos, geolocalização e o estado de
        /// avaliação de cada uma. SEM ação de aprovar/recusar — isso depende da
        /// permissão <c>CheckingGerenciar</c> de decisão, que não existe ainda
        /// (VEI-RD-91d, pendência humana explícita, fora de escopo nesta sprint).
        /// </summary>
        /// <remarks>
        /// "Histórico de avaliação" aqui é o retrato de cada FOTO — Status, Nota,
        /// Observação, quando foi enviada e quando foi avaliada pela última vez.
        /// Não é um log append-only: <c>CheckingFoto.AvaliarFoto</c> sobrescreve o
        /// estado anterior (ver <c>CheckingFoto.cs</c>), o domínio não guarda quem
        /// avaliou nem avaliações anteriores. É o que existe para mostrar — não o
        /// que o nome sugere num sistema com auditoria completa.
        /// </remarks>
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            var checking = await _tenant.Checkings
                .Include(c => c.PedidoInsercao.Pedido.Campanha.Cliente)
                .Include(c => c.Itens.Select(i => i.Peca.Local.Cidade))
                .Include(c => c.Itens.Select(i => i.Fotos))
                .Include(c => c.Itens.Select(i => i.PedidoInsercaoItem.PedidoItem.Periodo))
                .FirstOrDefaultAsync(c => c.Id == id);

            if (checking == null)
                return NotFound(new { message = "Checking não encontrado." });

            var itens = checking.Itens
                .Select(i => new
                {
                    i.IdPedidoItem,
                    PecaCodigo = i.Peca?.Codigo,
                    LocalCodigo = i.Peca?.Local?.Codigo,
                    LocalDescricao = i.Peca?.Local?.Descricao,
                    Cidade = i.Peca?.Local?.Cidade?.Nome,
                    Periodo = i.PedidoInsercaoItem?.PedidoItem?.Periodo?.Nome,
                    Status = i.Status.ToString(),
                    Fotos = i.Fotos
                        .OrderBy(f => f.DataCadastro)
                        .Select(f => new
                        {
                            f.Id,
                            DownloadUrl = $"/api/wl/checking/item/{i.IdPedidoItem}/fotos/{f.Id}/arquivo",
                            Status = f.Status.ToString(),
                            f.Nota,
                            f.ObservacaoAvaliacao,
                            f.ObservacaoPublicacao,
                            Geolocalizacao = (f.Geolocalizacao != null && !f.Geolocalizacao.IsNull())
                                ? new { f.Geolocalizacao.Latitude, f.Geolocalizacao.Longitude }
                                : null,
                            f.DistanciaPeca,
                            EnviadaEm = f.DataCadastro,
                            AvaliadaEm = f.DataAtualizacao
                        })
                        .ToList()
                })
                .ToList();

            return Ok(new
            {
                checking.Id,
                Status = checking.Status.ToString(),
                checking.DataCadastro,
                checking.DataAtualizacao,
                PiCodigo = checking.PedidoInsercao?.Codigo,
                Campanha = checking.PedidoInsercao?.Pedido?.Campanha?.Nome,
                Anunciante = checking.PedidoInsercao?.Pedido?.Campanha?.Cliente?.Nome,
                Itens = itens
            });
        }

        [HttpGet("pis-autorizadas")]
        public async Task<IActionResult> GetPisAutorizadas()
        {
            var afiliadaId = _tenant.AfiliadaId;

            var pis = await _tenant.PedidosInsercao
                .Where(pi => pi.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(pi => new
                {
                    pi.Id,
                    pi.Codigo,
                    pi.DataCadastro,
                    pi.ValorLiquidoVeiculacao
                })
                .ToListAsync();

            return Ok(pis);
        }

        [HttpGet("pi/{codigo}")]
        public async Task<IActionResult> GetPiByCodigo(string codigo)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var pi = await _tenant.PedidosInsercao
                .FirstOrDefaultAsync(p => p.Codigo == codigo && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (pi == null)
                return NotFound(new { message = "Pedido de Inserção não encontrado." });


            return Ok(new
            {
                pi.Id,
                pi.Codigo,
                pi.DataCadastro,
                pi.ValorLiquidoVeiculacao,
                ItensCount = pi.Itens != null ? pi.Itens.Count : 0
            });
        }

        /// <summary>
        /// Lista os itens de uma PI para a tela de checking (TP-R4).
        /// </summary>
        /// <remarks>
        /// Sem este endpoint a segunda tela do fluxo de checking era inalcançável:
        /// o <see cref="GetPiByCodigo"/> devolve apenas <c>ItensCount</c>, e o
        /// upload é endereçado por <c>idItemPI</c> — o frontend não tinha como
        /// descobrir esses ids. Espelha o que o <c>PedidosReservaController</c>
        /// já faz na projeção de <c>Itens</c>.
        ///
        /// Somente leitura, com a mesma validação de tenant dos demais.
        /// </remarks>
        [HttpGet("pi/{codigo}/itens")]
        public async Task<IActionResult> GetItensDaPi(string codigo)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var pi = await _tenant.PedidosInsercao
                .FirstOrDefaultAsync(p => p.Codigo == codigo && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (pi == null)
                return NotFound(new { message = "Pedido de Inserção não encontrado." });


            var itens = await _tenant.PedidoInsercaoItens
                .Where(i => i.IdPedidoInsercao == pi.Id)
                .Select(i => new
                {
                    i.IdPedidoItem,
                    i.IdPedidoInsercao,
                    Status = i.Status.ToString(),
                    PecaCodigo = i.PedidoItem.Peca.Codigo,
                    LocalCodigo = i.PedidoItem.Peca.Local.Codigo,
                    LocalDescricao = i.PedidoItem.Peca.Local.Descricao,
                    // Status do checking propriamente dito, quando ja houve envio.
                    StatusChecking = i.CheckingItem != null ? i.CheckingItem.Status.ToString() : null
                })
                .ToListAsync();

            return Ok(itens);
        }

        [HttpGet("item/{id}")]
        public async Task<IActionResult> GetItemById(int id)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var item = await _tenant.PedidoInsercaoItens
                .FirstOrDefaultAsync(i => i.IdPedidoItem == id);

            if (item == null)
                return NotFound(new { message = "Item de PI não encontrado." });


            return Ok(new
            {
                item.IdPedidoItem,
                item.IdPedidoInsercao,
                Status = item.Status.ToString()
            });
        }

    }
}
