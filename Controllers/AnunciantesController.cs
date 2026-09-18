using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Commands.Inputs.Clientes;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Veiculando.Domain.ValueObjects;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Anunciantes (Clientes Diretos) — VEI-RD-46. Tabela associativa
    /// AfiliadaCliente (PRD §6.3): o mesmo CNPJ pode atender várias exibidoras
    /// sem duplicar o Cliente. Sem backfill — a tela nasce vazia por design; o
    /// vínculo passa a existir pelo uso, via por-cnpj + /vincular.
    /// </summary>
    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize(Policy = AuthorizationSetup.ClienteGerenciar)]
    public class AnunciantesController : WlCoreProxyControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly ICoreCadastroService _coreCadastro;

        public AnunciantesController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            ICoreCadastroService coreCadastro)
        {
            _db = db;
            _tenant = tenant;
            _coreCadastro = coreCadastro;
        }

        /// <summary>
        /// Lista os anunciantes vinculados à afiliada, com agências vinculadas
        /// agregadas no servidor (frame 233:12642). O frontend só renderiza —
        /// nada é somado no navegador.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string status,
            [FromQuery] WlPaginaRequest pagina)
        {
            var query = _tenant.AfiliadaClientes
                .Include(v => v.Cliente.AgenciasContratadas.Select(ac => ac.Agencia));

            if (string.Equals(status, "Ativo", StringComparison.OrdinalIgnoreCase))
                query = query.Where(v => v.Status == StatusVinculoEnum.Ativo);
            else if (string.Equals(status, "Inativo", StringComparison.OrdinalIgnoreCase))
                query = query.Where(v => v.Status == StatusVinculoEnum.Inativo);
            // "Todos" ou ausente: sem filtro de status.

            var (page, pageSize) = WlPaginacao.Normalizar(pagina);

            var total = await query.CountAsync();

            var vinculos = await query
                .OrderBy(v => v.Cliente.Nome)
                .ThenBy(v => v.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            var itens = vinculos.Select(v => new
            {
                v.Cliente.Id,
                v.Cliente.Codigo,
                v.Cliente.Nome,
                v.Cliente.RazaoSocial,
                Cnpj = v.Cliente.Cnpj?.Numero,
                v.Cliente.Cidade,
                v.Cliente.Uf,
                v.Status,
                AgenciasVinculadas = MontarAgenciasVinculadas(v.Cliente),
                // Pendência do Humano (ver plano §5): a fórmula de Investimento
                // Histórico não está definida. null (indisponível), nunca 0 como
                // se fosse investimento real — o cálculo, quando existir, é no
                // servidor, nunca no navegador.
                InvestimentoHistorico = (decimal?)null
            }).ToList();

            return Ok(WlPaginacao.Montar(itens, page, pageSize, total));
        }

        /// <summary>
        /// Detalhe do anunciante, com histórico de campanhas e pedidos. 404 (não
        /// 403) para anunciante que existe mas não é desta afiliada — não revela
        /// a existência do recurso alheio.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var vinculo = await _tenant.AfiliadaClientes
                .Include(v => v.Cliente.AgenciasContratadas.Select(ac => ac.Agencia))
                .Include(v => v.Cliente.Campanhas.Select(c => c.Pedidos))
                .FirstOrDefaultAsync(v => v.IdCliente == id);

            if (vinculo == null)
                return NotFound(new { message = "Anunciante não encontrado." });

            var cliente = vinculo.Cliente;

            return Ok(new
            {
                cliente.Id,
                cliente.Codigo,
                cliente.Nome,
                cliente.RazaoSocial,
                Cnpj = cliente.Cnpj?.Numero,
                cliente.Cidade,
                cliente.Uf,
                Telefone = cliente.Telefone?.Numero,
                cliente.InscricaoEstadual,
                cliente.InscricaoMunicipal,
                cliente.PracaPagamento,
                cliente.PrazoPagamento,
                cliente.DescontoNegociado,
                cliente.ObservacoesAfiliada,
                vinculo.Status,
                vinculo.DataVinculo,
                AgenciasVinculadas = MontarAgenciasVinculadas(cliente),
                InvestimentoHistorico = (decimal?)null,
                Historico = cliente.Campanhas.Select(c => new
                {
                    c.Id,
                    c.Nome,
                    c.DataInicioPrevisto,
                    c.DataFimPrevisto,
                    Pedidos = c.Pedidos.Select(p => new
                    {
                        p.Id,
                        p.Codigo,
                        p.Status
                    }).ToList()
                }).ToList()
            });
        }

        /// <summary>
        /// Consulta prévia por CNPJ. CNPJ já existente propõe vínculo — nunca
        /// duplica o Cliente (PRD §8.14). Busca cross-tenant deliberada: é
        /// exatamente o ponto em que se PRECISA ver além do próprio tenant para
        /// não recriar um Cliente que já existe em outra afiliada.
        /// </summary>
        [HttpGet("por-cnpj/{cnpj}")]
        public async Task<IActionResult> PorCnpj(string cnpj)
        {
            var cnpjNumero = SomenteDigitos(cnpj);
            if (string.IsNullOrEmpty(cnpjNumero))
                return BadRequest(new { message = "Informe um CNPJ." });

            var cliente = await _db.Clientes
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Cnpj.Numero == cnpjNumero);

            if (cliente == null)
                return NotFound(new { message = "Nenhum anunciante com este CNPJ. O cadastro pode prosseguir." });

            var jaVinculadoAquiAtivo = await _tenant.AfiliadaClientes
                .AnyAsync(v => v.IdCliente == cliente.Id && v.Status == StatusVinculoEnum.Ativo);

            return Ok(new
            {
                cliente.Id,
                cliente.Nome,
                cliente.RazaoSocial,
                Cnpj = cliente.Cnpj?.Numero,
                JaVinculadoAEstaAfiliada = jaVinculadoAquiAtivo,
                PropostaDeVinculo = !jaVinculadoAquiAtivo
            });
        }

        /// <summary>
        /// Cria o anunciante. Cliente e AfiliadaCliente são gravados na mesma
        /// transação pelo handler do core (VEI-RD-46 §3) — nenhuma regra de
        /// negócio é reimplementada aqui.
        /// </summary>
        [HttpPost]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Create([FromBody] AnuncianteCadastroRequest request)
        {
            if (request == null)
                return BadRequest(new { message = "Dados do anunciante são obrigatórios." });

            var cnpjNumero = SomenteDigitos(request.Cnpj);
            if (string.IsNullOrEmpty(cnpjNumero))
                return BadRequest(new { message = "Informe um CNPJ válido." });

            // A busca por CNPJ é obrigatória antes do formulário abrir (regra de
            // UI); aqui é a garantia do servidor: nunca um segundo Cliente para
            // um CNPJ que já existe, em nenhuma afiliada.
            var cnpjExistente = await _db.Clientes.AsNoTracking().AnyAsync(c => c.Cnpj.Numero == cnpjNumero);
            if (cnpjExistente)
                return Conflict(new { message = "Já existe um anunciante com este CNPJ. Vincule em vez de cadastrar." });

            var command = new ClienteCadastroCommand
            {
                Id = 0,
                // Código é gerado pelo servidor — o que vier no payload é sempre
                // ignorado, nem ecoado nem persistido.
                Codigo = GerarCodigoDoServidor(cnpjNumero),
                Nome = request.Nome,
                RazaoSocial = request.RazaoSocial,
                Cnpj = new Cnpj(cnpjNumero),
                Endereco = new Endereco(
                    request.Bairro, request.Logradouro, request.Numero,
                    request.Complemento, request.Referencia,
                    string.IsNullOrWhiteSpace(request.Cep) ? null : new Cep(request.Cep)),
                Cidade = request.Cidade,
                Uf = request.Uf,
                Telefone = string.IsNullOrWhiteSpace(request.Telefone) ? null : new Telefone(request.Telefone),
                InscricaoEstadual = request.InscricaoEstadual,
                InscricaoMunicipal = request.InscricaoMunicipal,
                PracaPagamento = request.PracaPagamento,
                PrazoPagamento = request.PrazoPagamento,
                DescontoNegociado = request.DescontoNegociado,
                ObservacoesAfiliada = request.ObservacoesAfiliada
            };

            var resposta = await _coreCadastro.SalvarAnuncianteAsync(command, WlUsuarioId);
            return RepassarResposta(resposta);
        }

        /// <summary>
        /// Atualiza Praça/Prazo de Pagamento, Desconto Negociado e Observações —
        /// os únicos campos editáveis por este BFF. O resto do Cliente é
        /// preservado exatamente como está: o core substitui o registro inteiro
        /// no Update, então os campos não editáveis aqui vêm do que já existe,
        /// não do payload.
        /// </summary>
        [HttpPut("{id}")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Update(int id, [FromBody] AnuncianteAtualizacaoRequest request)
        {
            if (request == null)
                return BadRequest(new { message = "Dados são obrigatórios." });

            var cliente = await _tenant.Clientes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
            if (cliente == null)
                return NotFound(new { message = "Anunciante não encontrado." });

            var command = new ClienteCadastroCommand
            {
                Id = id,
                Codigo = cliente.Codigo,
                Nome = cliente.Nome,
                RazaoSocial = cliente.RazaoSocial,
                Cnpj = cliente.Cnpj,
                Endereco = cliente.Endereco,
                Cidade = cliente.Cidade,
                Uf = cliente.Uf,
                Telefone = cliente.Telefone,
                InscricaoEstadual = cliente.InscricaoEstadual,
                InscricaoMunicipal = cliente.InscricaoMunicipal,
                PracaPagamento = request.PracaPagamento,
                PrazoPagamento = request.PrazoPagamento,
                DescontoNegociado = request.DescontoNegociado,
                ObservacoesAfiliada = request.ObservacoesAfiliada,
                ObservacoesVeiculando = cliente.ObservacoesVeiculando
            };

            var resposta = await _coreCadastro.SalvarAnuncianteAsync(command, WlUsuarioId);
            return RepassarResposta(resposta);
        }

        /// <summary>
        /// Vincula um anunciante já existente (achado via por-cnpj) a esta
        /// afiliada. Nunca cria um segundo Cliente.
        /// </summary>
        [HttpPost("{id}/vincular")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Vincular(int id)
        {
            var clienteExiste = await _db.Clientes.AsNoTracking().AnyAsync(c => c.Id == id);
            if (!clienteExiste)
                return NotFound(new { message = "Anunciante não encontrado." });

            var resposta = await _coreCadastro.VincularAnuncianteAsync(id, WlUsuarioId);
            return RepassarResposta(resposta);
        }

        /// <summary>
        /// Ativa/inativa o vínculo com a afiliada (invariante #7: nunca exclusão
        /// física). Isto muda o vínculo AfiliadaCliente, não o Cliente em si —
        /// gravado direto pelo BFF porque, diferente da criação, não há regra de
        /// negócio do core envolvida aqui.
        /// </summary>
        [HttpPatch("{id}/status")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> AlterarStatus(int id, [FromBody] AnuncianteStatusRequest request)
        {
            var vinculo = await _tenant.AfiliadaClientes.FirstOrDefaultAsync(v => v.IdCliente == id);
            if (vinculo == null)
                return NotFound(new { message = "Anunciante não encontrado." });

            if (request?.Ativo == true)
                vinculo.Ativar();
            else
                vinculo.Inativar();

            await _db.SaveChangesAsync();

            return Ok(new { vinculo.IdCliente, vinculo.Status });
        }

        private static string MontarAgenciasVinculadas(Cliente cliente)
        {
            var nomes = cliente.AgenciasContratadas?
                .Where(ac => ac.Agencia != null)
                .Select(ac => ac.Agencia.Nome)
                .ToList();

            // Nunca travessão nem campo vazio: "Venda Direta" é uma agência real
            // do sistema (VEI-RD-79) — este texto é o fallback até essa tela existir.
            return nomes == null || nomes.Count == 0
                ? "Venda Direta (Sem Agência)"
                : string.Join(", ", nomes);
        }

        private static string GerarCodigoDoServidor(string cnpjNumero)
        {
            var baseCodigo = cnpjNumero.Length >= Cliente.CodigoMaxLength
                ? cnpjNumero.Substring(0, Cliente.CodigoMaxLength)
                : cnpjNumero.PadLeft(Cliente.CodigoMaxLength, '0');
            return baseCodigo;
        }

        private static string SomenteDigitos(string valor) =>
            valor == null ? null : new string(valor.Where(char.IsDigit).ToArray());
    }

    public sealed class AnuncianteCadastroRequest
    {
        public string Nome { get; set; }
        public string RazaoSocial { get; set; }
        public string Cnpj { get; set; }
        public string Bairro { get; set; }
        public string Logradouro { get; set; }
        public string Numero { get; set; }
        public string Complemento { get; set; }
        public string Referencia { get; set; }
        public string Cep { get; set; }
        public string Cidade { get; set; }
        public string Uf { get; set; }
        public string Telefone { get; set; }
        public string InscricaoEstadual { get; set; }
        public string InscricaoMunicipal { get; set; }
        public string PracaPagamento { get; set; }
        public short? PrazoPagamento { get; set; }
        public decimal DescontoNegociado { get; set; }
        public string ObservacoesAfiliada { get; set; }
    }

    public sealed class AnuncianteAtualizacaoRequest
    {
        public string PracaPagamento { get; set; }
        public short? PrazoPagamento { get; set; }
        public decimal DescontoNegociado { get; set; }
        public string ObservacoesAfiliada { get; set; }
    }

    public sealed class AnuncianteStatusRequest
    {
        public bool Ativo { get; set; }
    }
}
