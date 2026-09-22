using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Commands.Inputs.Agencias;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Veiculando.Domain.Services;
using Veiculando.Domain.ValueObjects;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Agências — VEI-RD-79. Tabela associativa AfiliadaAgencia (PRD §6.3): o mesmo
    /// CNPJ atende várias exibidoras sem duplicar a Agencia. Espelha
    /// <see cref="AnunciantesController"/>, que resolve o mesmo problema para Cliente.
    /// </summary>
    /// <remarks>
    /// Permissão: <c>ClienteGerenciar</c>. Não é um apelido infeliz — é o que a lista
    /// canônica de permissões já previa: o teste
    /// <c>WlPermissoesCanonicasTests.Lista_canonica_contem_as_permissoes_que_outros_cards_dependem</c>
    /// documenta, em comentário, "ClienteGerenciar: VEI-RD-46 (Anunciantes) e
    /// VEI-RD-79 (Agencias)". Criar uma <c>AgenciaGerenciar</c> aqui exigiria mexer em
    /// <c>WlPermissoesValidas</c> e numa migration de claims, que é escopo de outro card.
    /// </remarks>
    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize(Policy = AuthorizationSetup.ClienteGerenciar)]
    public class AgenciasController : WlCoreProxyControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly ICoreCadastroService _coreCadastro;

        public AgenciasController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            ICoreCadastroService coreCadastro)
        {
            _db = db;
            _tenant = tenant;
            _coreCadastro = coreCadastro;
        }

        /// <summary>
        /// Lista as agências vinculadas, com a CONTAGEM DE CAMPANHAS agregada em SQL
        /// (frame 233:11873, coluna "Campanhas").
        /// </summary>
        /// <remarks>
        /// A contagem é a razão de o <c>Select</c> acontecer ANTES do <c>ToListAsync</c>:
        /// materializar as agências e depois ler <c>a.Campanhas.Count</c> em memória
        /// dispararia uma query por linha (N+1) ou, pior, carregaria todas as campanhas
        /// da exibidora para contar. Invariante #5 do plano: agregado em SQL.
        ///
        /// <para>A contagem é recortada pela afiliada pela mesma cadeia de
        /// <see cref="ITenantQueries.Campanhas"/>: a agência pode atender várias
        /// exibidoras, e mostrar "14 campanhas" incluindo as de outra exibidora
        /// vazaria volume comercial alheio na coluna.</para>
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string status,
            [FromQuery] string busca,
            [FromQuery] WlPaginaRequest pagina)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var query = _tenant.AfiliadaAgencias.AsQueryable();

            if (string.Equals(status, "Ativo", StringComparison.OrdinalIgnoreCase))
                query = query.Where(v => v.Status == StatusVinculoEnum.Ativo);
            else if (string.Equals(status, "Inativo", StringComparison.OrdinalIgnoreCase))
                query = query.Where(v => v.Status == StatusVinculoEnum.Inativo);
            // "Todos" ou ausente: sem filtro de status.

            if (!string.IsNullOrWhiteSpace(busca))
            {
                var termo = busca.Trim();
                query = query.Where(v =>
                    v.Agencia.Nome.Contains(termo) ||
                    v.Agencia.RazaoSocial.Contains(termo) ||
                    v.Agencia.Cnpj.Numero.Contains(termo) ||
                    v.Agencia.Cidade.Contains(termo));
            }

            var (page, pageSize) = WlPaginacao.Normalizar(pagina);
            var total = await query.CountAsync();

            var itens = await query
                .OrderBy(v => v.Agencia.Nome)
                .ThenBy(v => v.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(v => new
                {
                    Id = v.IdAgencia,
                    v.Agencia.Nome,
                    v.Agencia.RazaoSocial,
                    Cnpj = v.Agencia.Cnpj.Numero,
                    v.Agencia.Cidade,
                    v.Agencia.Uf,
                    Email = v.Agencia.Email.Endereco,
                    Telefone = v.Agencia.Telefone.Numero,
                    v.Status,
                    Campanhas = v.Agencia.Campanhas.Count(c => c.Pedidos.Any(
                        p => p.PedidosReserva.Any(r => r.IdAfiliada == afiliadaId)
                          || p.PedidosInsercao.Any(i => i.IdAfiliada == afiliadaId)))
                })
                .ToListAsync();

            return Ok(WlPaginacao.Montar(itens, page, pageSize, total));
        }

        /// <summary>
        /// Detalhe da agência, com os anunciantes vinculados (AgenciaCliente).
        /// 404 — não 403 — para agência que existe mas não é desta afiliada.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var vinculo = await _tenant.AfiliadaAgencias
                .Include(v => v.Agencia.ContratosCliente.Select(cc => cc.Cliente))
                .FirstOrDefaultAsync(v => v.IdAgencia == id);

            if (vinculo == null)
                return NotFound(new { message = "Agência não encontrada." });

            var agencia = vinculo.Agencia;
            var afiliada = await _db.Afiliadas.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == _tenant.AfiliadaId);

            return Ok(new
            {
                agencia.Id,
                agencia.Nome,
                agencia.RazaoSocial,
                Cnpj = agencia.Cnpj?.Numero,
                agencia.Cidade,
                agencia.Uf,
                Telefone = agencia.Telefone?.Numero,
                Email = agencia.Email?.Endereco,
                agencia.Site,
                agencia.InscricaoEstadual,
                agencia.InscricaoMunicipal,
                agencia.LogoUrl,
                agencia.BonificacaoVolume,
                agencia.ObservacoesAfiliada,
                vinculo.Status,
                vinculo.DataVinculo,
                // O espelho de venda direta não pode ser inativado nem removido. A tela
                // usa isto para não desenhar a ação — controle proibido some do DOM
                // (invariante #8), em vez de vir desabilitado.
                VendaDireta = AgenciaVendaDiretaProvisionamento.EhVendaDireta(afiliada, agencia),
                AnunciantesVinculados = agencia.ContratosCliente
                    .Where(cc => cc.Cliente != null)
                    .Select(cc => new
                    {
                        cc.Cliente.Id,
                        cc.Cliente.Nome,
                        cc.Cliente.RazaoSocial,
                        cc.ComissaoAgencia,
                        cc.DataInicioContrato,
                        cc.DataExpiracaoContrato
                    })
                    .ToList()
            });
        }

        /// <summary>
        /// Consulta prévia por CNPJ. CNPJ já existente propõe vínculo — nunca duplica
        /// a Agencia (PRD §8.14). Busca cross-tenant deliberada, igual a Anunciantes:
        /// é exatamente onde se PRECISA enxergar além do próprio tenant para não
        /// recriar uma Agencia que já existe em outra exibidora.
        /// </summary>
        [HttpGet("por-cnpj/{cnpj}")]
        public async Task<IActionResult> PorCnpj(string cnpj)
        {
            var cnpjNumero = SomenteDigitos(cnpj);
            if (string.IsNullOrEmpty(cnpjNumero))
                return BadRequest(new { message = "Informe um CNPJ." });

            var agencia = await _db.Agencias
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Cnpj.Numero == cnpjNumero);

            if (agencia == null)
                return NotFound(new { message = "Nenhuma agência com este CNPJ. O cadastro pode prosseguir." });

            var jaVinculadaAquiAtiva = await _tenant.AfiliadaAgencias
                .AnyAsync(v => v.IdAgencia == agencia.Id && v.Status == StatusVinculoEnum.Ativo);

            return Ok(new
            {
                agencia.Id,
                agencia.Nome,
                agencia.RazaoSocial,
                Cnpj = agencia.Cnpj?.Numero,
                JaVinculadaAEstaAfiliada = jaVinculadaAquiAtiva,
                PropostaDeVinculo = !jaVinculadaAquiAtiva
            });
        }

        /// <summary>
        /// Cria a agência. Agencia e AfiliadaAgencia são gravadas na mesma transação
        /// pelo handler do core — nenhuma regra de negócio é reimplementada aqui.
        /// </summary>
        /// <remarks>
        /// Não existe "prazo de pagamento" neste payload. O PRD §5.5 é explícito e o
        /// código confirma: <c>AgenciaCadastroCommand</c> e a entidade <c>Agencia</c>
        /// não têm o campo — diferente de <c>Cliente</c>, que tem. Um campo de prazo
        /// aqui não teria onde ser gravado.
        /// </remarks>
        [HttpPost]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Create([FromBody] AgenciaCadastroRequest request)
        {
            if (request == null)
                return BadRequest(new { message = "Dados da agência são obrigatórios." });

            var cnpjNumero = SomenteDigitos(request.Cnpj);
            if (string.IsNullOrEmpty(cnpjNumero))
                return BadRequest(new { message = "Informe um CNPJ válido." });

            // A busca por CNPJ é obrigatória antes de o formulário abrir (regra de UI);
            // aqui é a garantia do servidor: nunca uma segunda Agencia para um CNPJ que
            // já existe, em nenhuma afiliada.
            var cnpjExistente = await _db.Agencias.AsNoTracking().AnyAsync(a => a.Cnpj.Numero == cnpjNumero);
            if (cnpjExistente)
                return Conflict(new { message = "Já existe uma agência com este CNPJ. Vincule em vez de cadastrar." });

            var command = new AgenciaCadastroCommand
            {
                Id = 0,
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
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : new Email(request.Email),
                Site = request.Site,
                InscricaoEstadual = request.InscricaoEstadual,
                InscricaoMunicipal = request.InscricaoMunicipal,
                BonificacaoVolume = request.BonificacaoVolume,
                ObservacoesAfiliada = request.ObservacoesAfiliada
            };

            var resposta = await _coreCadastro.SalvarAgenciaAsync(command, WlUsuarioId);
            return RepassarResposta(resposta);
        }

        /// <summary>
        /// Condições comerciais: Bonificação por Volume e Observações — os únicos campos
        /// editáveis por este BFF. O resto da Agencia é preservado como está, porque o
        /// core substitui o registro inteiro no Update.
        /// </summary>
        [HttpPut("{id}")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Update(int id, [FromBody] AgenciaAtualizacaoRequest request)
        {
            if (request == null)
                return BadRequest(new { message = "Dados são obrigatórios." });

            var agencia = await _tenant.Agencias.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id);
            if (agencia == null)
                return NotFound(new { message = "Agência não encontrada." });

            var command = new AgenciaCadastroCommand
            {
                Id = id,
                Nome = agencia.Nome,
                RazaoSocial = agencia.RazaoSocial,
                Cnpj = agencia.Cnpj,
                Endereco = agencia.Endereco,
                Cidade = agencia.Cidade,
                Uf = agencia.Uf,
                Telefone = agencia.Telefone,
                Email = agencia.Email,
                Site = agencia.Site,
                InscricaoEstadual = agencia.InscricaoEstadual,
                InscricaoMunicipal = agencia.InscricaoMunicipal,
                BonificacaoVolume = request.BonificacaoVolume,
                ObservacoesAfiliada = request.ObservacoesAfiliada,
                ObservacoesVeiculando = agencia.ObservacoesVeiculando
            };

            var resposta = await _coreCadastro.SalvarAgenciaAsync(command, WlUsuarioId);
            return RepassarResposta(resposta);
        }

        /// <summary>
        /// Vincula uma agência já existente (achada via por-cnpj) a esta afiliada.
        /// Nunca cria uma segunda Agencia.
        /// </summary>
        [HttpPost("{id}/vincular")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Vincular(int id)
        {
            var agenciaExiste = await _db.Agencias.AsNoTracking().AnyAsync(a => a.Id == id);
            if (!agenciaExiste)
                return NotFound(new { message = "Agência não encontrada." });

            var resposta = await _coreCadastro.VincularAgenciaAsync(id, WlUsuarioId);
            return RepassarResposta(resposta);
        }

        /// <summary>
        /// Inativa ou reativa o VÍNCULO com a afiliada — o que a tela chama de "excluir".
        /// </summary>
        /// <remarks>
        /// <para><b>O conflito entre Figma e PRD, resolvido a favor do PRD.</b> O frame
        /// 233:11873 desenha um <c>Btn-Delete</c> com ícone de lixeira na linha da
        /// agência. O PRD §8.5 e o padrão do domínio dizem inativação lógica, nunca
        /// exclusão física — <c>AfiliadaAgencia.Status</c> existe exatamente para isso,
        /// e a <c>Agencia</c> continua no Core, visível para as outras exibidoras que a
        /// usam. Não há, e não deve haver, DELETE físico de Agencia em caminho algum.</para>
        ///
        /// <para><b>A agência de venda direta é recusada.</b> Ela é o espelho da própria
        /// exibidora (VEI-RD-79 task b) e as telas de Anunciantes, Campanhas e PI assumem
        /// o vínculo: desligá-lo faria essas telas renderizarem travessão onde o design
        /// pede um nome.</para>
        /// </remarks>
        [HttpPatch("{id}/status")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> AlterarStatus(int id, [FromBody] AgenciaStatusRequest request)
        {
            var vinculo = await _tenant.AfiliadaAgencias
                .Include(v => v.Agencia)
                .FirstOrDefaultAsync(v => v.IdAgencia == id);

            if (vinculo == null)
                return NotFound(new { message = "Agência não encontrada." });

            var afiliada = await _db.Afiliadas.FirstOrDefaultAsync(a => a.Id == _tenant.AfiliadaId);

            if (AgenciaVendaDiretaProvisionamento.EhVendaDireta(afiliada, vinculo.Agencia))
                return Conflict(new
                {
                    message = "A agência de venda direta não pode ser inativada: ela representa as vendas feitas sem agência."
                });

            if (request?.Ativo == true)
                vinculo.Ativar();
            else
                vinculo.Inativar();

            await _db.SaveChangesAsync();

            return Ok(new { vinculo.IdAgencia, vinculo.Status });
        }

        private static string SomenteDigitos(string valor) =>
            valor == null ? null : new string(valor.Where(char.IsDigit).ToArray());
    }

    public sealed class AgenciaCadastroRequest
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
        public string Email { get; set; }
        public string Site { get; set; }
        public string InscricaoEstadual { get; set; }
        public string InscricaoMunicipal { get; set; }
        public decimal BonificacaoVolume { get; set; }
        public string ObservacoesAfiliada { get; set; }
    }

    public sealed class AgenciaAtualizacaoRequest
    {
        public decimal BonificacaoVolume { get; set; }
        public string ObservacoesAfiliada { get; set; }
    }

    public sealed class AgenciaStatusRequest
    {
        public bool Ativo { get; set; }
    }
}
