using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
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
    /// vínculo passa a existir pelo uso, via POST (CNPJ novo ou existente).
    /// </summary>
    /// <remarks>
    /// Sprint 10.5 (BE-1): segmento, contato e e-mail moram no VÍNCULO, não no
    /// Cliente — são dados desta exibidora com o anunciante, e o Cliente é
    /// compartilhado. Contrato final registrado no test plan cec4eea1.
    /// </remarks>
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
        /// Lista os anunciantes vinculados à afiliada (frame 233:12642). Busca,
        /// filtros, ordenação, paginação e o investimento histórico são resolvidos
        /// no SQL — o frontend só renderiza.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string busca,
            [FromQuery] string status,
            [FromQuery] int? segmentoId,
            [FromQuery] WlPaginaRequest pagina)
        {
            var query = _tenant.AfiliadaClientes.AsNoTracking();

            if (string.Equals(status, "Ativo", StringComparison.OrdinalIgnoreCase))
                query = query.Where(v => v.Status == StatusVinculoEnum.Ativo);
            else if (string.Equals(status, "Inativo", StringComparison.OrdinalIgnoreCase))
                query = query.Where(v => v.Status == StatusVinculoEnum.Inativo);
            // "Todos" ou ausente: sem filtro de status.

            if (segmentoId.HasValue)
                query = query.Where(v => v.IdSegmento == segmentoId.Value);

            if (!string.IsNullOrWhiteSpace(busca))
            {
                var termo = busca.Trim();
                var digitos = SomenteDigitos(termo);
                var temDigitos = !string.IsNullOrEmpty(digitos);

                query = query.Where(v =>
                    v.Cliente.Nome.Contains(termo)
                    || v.Cliente.RazaoSocial.Contains(termo)
                    || v.Cliente.Codigo.Contains(termo)
                    || (temDigitos && v.Cliente.Cnpj.Numero.Contains(digitos)));
            }

            var (page, pageSize) = WlPaginacao.Normalizar(pagina);
            var total = await query.CountAsync();

            var linhas = Projetar(query);
            var sort = WlPaginacao.Ordenacao(pagina?.Sort, "nome",
                "nome", "razaoSocial", "cidade", "dataVinculo", "investimentoHistorico");
            var desc = pagina?.Desc ?? false;

            linhas = sort switch
            {
                "razaoSocial" => desc ? linhas.OrderByDescending(l => l.RazaoSocial) : linhas.OrderBy(l => l.RazaoSocial),
                "cidade" => desc ? linhas.OrderByDescending(l => l.Cidade) : linhas.OrderBy(l => l.Cidade),
                "dataVinculo" => desc ? linhas.OrderByDescending(l => l.DataVinculo) : linhas.OrderBy(l => l.DataVinculo),
                "investimentoHistorico" => desc ? linhas.OrderByDescending(l => l.InvestimentoHistorico) : linhas.OrderBy(l => l.InvestimentoHistorico),
                _ => desc ? linhas.OrderByDescending(l => l.Nome) : linhas.OrderBy(l => l.Nome),
            };

            var itens = await ((IOrderedQueryable<AnuncianteLinha>)linhas)
                .ThenBy(l => l.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(WlPaginacao.Montar(itens.Select(ParaResposta).ToList(), page, pageSize, total));
        }

        /// <summary>
        /// Detalhe do anunciante. 404 (não 403) para anunciante que existe mas não
        /// é desta afiliada — não revela a existência do recurso alheio.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var linha = await Projetar(_tenant.AfiliadaClientes.AsNoTracking().Where(v => v.IdCliente == id))
                .FirstOrDefaultAsync();

            if (linha == null)
                return NotFound(new { message = "Anunciante não encontrado." });

            var cliente = await _tenant.Clientes.AsNoTracking().FirstAsync(c => c.Id == id);

            // Histórico recortado pelo tenant. Antes vinha de cliente.Campanhas
            // inteiro: um Cliente compartilhado mostrava as campanhas e pedidos que
            // ele tem com OUTRAS exibidoras.
            var afiliadaId = _tenant.AfiliadaId;
            var campanhas = await _tenant.Campanhas.AsNoTracking()
                .Where(c => c.IdCliente == id)
                .OrderByDescending(c => c.DataInicioPrevisto)
                .Select(c => new
                {
                    c.Id,
                    c.Nome,
                    c.DataInicioPrevisto,
                    c.DataFimPrevisto,
                    Pedidos = c.Pedidos
                        .Where(p => p.PedidosReserva.Any(r => r.IdAfiliada == afiliadaId)
                                 || p.PedidosInsercao.Any(i => i.IdAfiliada == afiliadaId))
                        .Select(p => new { p.Id, p.Codigo, p.Status })
                })
                .ToListAsync();

            var resposta = ParaResposta(linha);

            return Ok(new
            {
                resposta.Id,
                resposta.Codigo,
                resposta.Nome,
                resposta.RazaoSocial,
                resposta.Cnpj,
                resposta.Cidade,
                resposta.Uf,
                resposta.Email,
                resposta.Contato,
                resposta.Segmento,
                resposta.Status,
                resposta.DataVinculo,
                resposta.AgenciasVinculadas,
                resposta.InvestimentoHistorico,
                Telefone = cliente.Telefone?.Numero,
                cliente.InscricaoEstadual,
                cliente.InscricaoMunicipal,
                cliente.PracaPagamento,
                cliente.PrazoPagamento,
                cliente.DescontoNegociado,
                cliente.ObservacoesAfiliada,
                Historico = campanhas
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
        /// Cadastra o anunciante nesta exibidora.
        /// </summary>
        /// <remarks>
        /// <para><b>CNPJ novo:</b> Cliente e AfiliadaCliente são gravados na mesma
        /// transação pelo ClienteHandler do core (201, <c>criado: true</c>).</para>
        /// <para><b>CNPJ já existente em outra exibidora:</b> cria SÓ o vínculo, pelo
        /// handler de vínculo do core (200, <c>criado: false</c>) — PRD critério 14.
        /// Os dados cadastrais do payload são ignorados nesse caso: o Cliente é
        /// compartilhado e não é esta exibidora quem o reescreve.</para>
        /// <para><b>Já vinculado aqui:</b> 409 com o id, para o front abrir o registro.</para>
        /// <para>Segmento, contato e e-mail são validados ANTES de qualquer escrita e
        /// gravados no vínculo depois que o core confirma.</para>
        /// </remarks>
        [HttpPost]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Create([FromBody] AnuncianteCadastroRequest request)
        {
            if (request == null)
                return BadRequest(new { message = "Dados do anunciante são obrigatórios." });

            var cnpjNumero = SomenteDigitos(request.Cnpj);
            if (string.IsNullOrEmpty(cnpjNumero))
                return BadRequest(new { message = "Informe um CNPJ válido." });

            var erroComercial = await ValidarDadosComerciaisAsync(request.SegmentoId, request.Contato, request.Email);
            if (erroComercial != null) return erroComercial;

            var existente = await _db.Clientes.AsNoTracking()
                .Where(c => c.Cnpj.Numero == cnpjNumero)
                .Select(c => new { c.Id })
                .FirstOrDefaultAsync();

            if (existente != null)
            {
                var jaVinculado = await _tenant.AfiliadaClientes.AnyAsync(v => v.IdCliente == existente.Id);
                if (jaVinculado)
                    return Conflict(new { message = "Este anunciante já está cadastrado nesta exibidora.", id = existente.Id });

                var vinculo = await _coreCadastro.VincularAnuncianteAsync(existente.Id, WlUsuarioId);
                if (!vinculo.Sucesso)
                    return RepassarResposta(vinculo);

                return await GravarDadosComerciaisAsync(existente.Id, request.SegmentoId, request.Contato, request.Email)
                       ?? Ok(new { id = existente.Id, criado = false, vinculado = true });
            }

            var command = new ClienteCadastroCommand
            {
                Id = 0,
                // Código é gerado pelo servidor — o que vier no payload é sempre
                // ignorado, nem ecoado nem persistido.
                Codigo = GerarCodigoDoServidor(cnpjNumero),
                Nome = string.IsNullOrWhiteSpace(request.NomeFantasia) ? request.Nome : request.NomeFantasia,
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
            if (!resposta.Sucesso)
                return RepassarResposta(resposta);

            // O core não devolve um contrato estável com o id; o CNPJ é único e
            // acabou de ser vinculado a este tenant, então é a chave de volta.
            var idCriado = await _tenant.AfiliadaClientes
                .Where(v => v.Cliente.Cnpj.Numero == cnpjNumero)
                .Select(v => (int?)v.IdCliente)
                .FirstOrDefaultAsync();

            if (idCriado == null)
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "O Veiculando Core confirmou o cadastro, mas o vínculo com esta exibidora não foi encontrado."
                });

            return await GravarDadosComerciaisAsync(idCriado.Value, request.SegmentoId, request.Contato, request.Email)
                   ?? StatusCode(StatusCodes.Status201Created, new { id = idCriado.Value, criado = true, vinculado = true });
        }

        /// <summary>
        /// Atualiza as condições comerciais do Cliente (Praça/Prazo de Pagamento,
        /// Desconto Negociado, Observações) e os dados do vínculo (segmento,
        /// contato, e-mail). O resto do Cliente é preservado: o core substitui o
        /// registro inteiro no Update, então os campos não editáveis vêm do que já
        /// existe, não do payload. Substituição completa: campo omitido vira vazio.
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

            var erroComercial = await ValidarDadosComerciaisAsync(request.SegmentoId, request.Contato, request.Email);
            if (erroComercial != null) return erroComercial;

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
            if (!resposta.Sucesso)
                return RepassarResposta(resposta);

            return await GravarDadosComerciaisAsync(id, request.SegmentoId, request.Contato, request.Email)
                   ?? Ok(new { id });
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

            return Ok(new { id = vinculo.IdCliente, vinculo.Status });
        }

        /// <summary>
        /// Linha da listagem, projetada no SQL. O investimento histórico é a soma
        /// de <c>PedidoInsercao.ValorLiquidoVeiculacao</c> dos PIs do cliente NESTA
        /// afiliada (fórmula do test plan cec4eea1), sem PIs Cancelados nem
        /// Rejeitados — um PI que não vai veicular não é investimento.
        /// </summary>
        private IQueryable<AnuncianteLinha> Projetar(IQueryable<AfiliadaCliente> vinculos)
        {
            var pisQueContam = _tenant.PedidosInsercao.Where(pi =>
                pi.Status != StatusPedidoInsercaoEnum.Cancelado
                && pi.Status != StatusPedidoInsercaoEnum.Rejeitado);

            return vinculos.Select(v => new AnuncianteLinha
            {
                Id = v.IdCliente,
                Codigo = v.Cliente.Codigo,
                Nome = v.Cliente.Nome,
                RazaoSocial = v.Cliente.RazaoSocial,
                Cnpj = v.Cliente.Cnpj.Numero,
                Cidade = v.Cliente.Cidade,
                Uf = v.Cliente.Uf,
                Email = v.Email,
                Contato = v.Contato,
                SegmentoId = v.IdSegmento,
                SegmentoNome = v.Segmento.Nome,
                Status = v.Status,
                DataVinculo = v.DataVinculo,
                InvestimentoHistorico = pisQueContam
                    .Where(pi => pi.Pedido.Campanha.IdCliente == v.IdCliente)
                    .Sum(pi => (decimal?)pi.ValorLiquidoVeiculacao) ?? 0m,
                Agencias = v.Cliente.AgenciasContratadas
                    .Where(ac => ac.StatusExibicao != StatusExibicaoEnum.Deletado)
                    .Select(ac => new AgenciaRef { Id = ac.Agencia.Id, Nome = ac.Agencia.Nome })
            });
        }

        private static AnuncianteResposta ParaResposta(AnuncianteLinha l) => new()
        {
            Id = l.Id,
            Codigo = l.Codigo,
            Nome = l.Nome,
            RazaoSocial = l.RazaoSocial,
            Cnpj = l.Cnpj,
            Cidade = l.Cidade,
            Uf = l.Uf,
            Email = l.Email,
            Contato = l.Contato,
            Segmento = l.SegmentoId.HasValue ? new SegmentoRef { Id = l.SegmentoId.Value, Nome = l.SegmentoNome } : null,
            Status = l.Status,
            DataVinculo = l.DataVinculo,
            // Lista vazia = Venda Direta. O rótulo é do front; aqui é dado.
            AgenciasVinculadas = l.Agencias?.OrderBy(a => a.Nome).ToList() ?? new List<AgenciaRef>(),
            InvestimentoHistorico = l.InvestimentoHistorico
        };

        private async Task<IActionResult> ValidarDadosComerciaisAsync(int? segmentoId, string contato, string email)
        {
            if (segmentoId.HasValue && !await _db.Segmento.AnyAsync(s => s.Id == segmentoId.Value))
                return BadRequest(new { message = "Segmento não encontrado." });

            if (!string.IsNullOrWhiteSpace(contato) && contato.Trim().Length > AfiliadaCliente.ContatoMaxLength)
                return BadRequest(new { message = $"O contato deve ter no máximo {AfiliadaCliente.ContatoMaxLength} caracteres." });

            if (!string.IsNullOrWhiteSpace(email))
            {
                var normalizado = email.Trim();
                if (normalizado.Length > AfiliadaCliente.EmailMaxLength || !new Email(normalizado).IsValid())
                    return BadRequest(new { message = "Informe um e-mail válido." });
            }

            return null;
        }

        /// <summary>Grava segmento/contato/e-mail no vínculo. null = sucesso.</summary>
        private async Task<IActionResult> GravarDadosComerciaisAsync(int idCliente, int? segmentoId, string contato, string email)
        {
            var vinculo = await _tenant.AfiliadaClientes.FirstOrDefaultAsync(v => v.IdCliente == idCliente);
            if (vinculo == null)
                return NotFound(new { message = "Anunciante não encontrado." });

            vinculo.AtualizarDadosComerciais(segmentoId, contato, email);
            if (!vinculo.IsValid())
                return BadRequest(new
                {
                    message = "Dados comerciais inválidos.",
                    erros = vinculo.Notifications.Select(n => n.Message).ToList()
                });

            await _db.SaveChangesAsync();
            return null;
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

        // Classes nomeadas (e não anônimas) porque a projeção atravessa o
        // OrderBy dinâmico e volta tipada para ParaResposta.
        private sealed class AnuncianteLinha
        {
            public int Id { get; set; }
            public string Codigo { get; set; }
            public string Nome { get; set; }
            public string RazaoSocial { get; set; }
            public string Cnpj { get; set; }
            public string Cidade { get; set; }
            public string Uf { get; set; }
            public string Email { get; set; }
            public string Contato { get; set; }
            public int? SegmentoId { get; set; }
            public string SegmentoNome { get; set; }
            public StatusVinculoEnum Status { get; set; }
            public DateTime DataVinculo { get; set; }
            public decimal InvestimentoHistorico { get; set; }
            public IEnumerable<AgenciaRef> Agencias { get; set; }
        }
    }

    public sealed class AgenciaRef
    {
        public int Id { get; set; }
        public string Nome { get; set; }
    }

    public sealed class SegmentoRef
    {
        public int Id { get; set; }
        public string Nome { get; set; }
    }

    public sealed class AnuncianteResposta
    {
        public int Id { get; set; }
        public string Codigo { get; set; }
        public string Nome { get; set; }
        public string RazaoSocial { get; set; }
        public string Cnpj { get; set; }
        public string Cidade { get; set; }
        public string Uf { get; set; }
        public string Email { get; set; }
        public string Contato { get; set; }
        public SegmentoRef Segmento { get; set; }
        public StatusVinculoEnum Status { get; set; }
        public DateTime DataVinculo { get; set; }
        public List<AgenciaRef> AgenciasVinculadas { get; set; }
        public decimal InvestimentoHistorico { get; set; }
    }

    public sealed class AnuncianteCadastroRequest
    {
        /// <summary>Nome fantasia. <c>Nome</c> é aceito por compatibilidade.</summary>
        public string NomeFantasia { get; set; }
        public string Nome { get; set; }
        public string RazaoSocial { get; set; }
        public string Cnpj { get; set; }
        public int? SegmentoId { get; set; }
        public string Contato { get; set; }
        public string Email { get; set; }
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
        public int? SegmentoId { get; set; }
        public string Contato { get; set; }
        public string Email { get; set; }
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
