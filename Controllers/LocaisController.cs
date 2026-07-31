using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Commands.Inputs;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize]
    [ServiceFilter(typeof(InputSanitizationFilter))]
    public class LocaisController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantContext _tenantContext;
        private readonly ICoreCadastroService _coreCadastro;

        public LocaisController(
            VeiculandoDataContext db,
            ITenantContext tenantContext,
            ICoreCadastroService coreCadastro)
        {
            _db = db;
            _tenantContext = tenantContext;
            _coreCadastro = coreCadastro;
        }

        /// <summary>
        /// Id do operador WhiteLabel autenticado, para a trilha de origem.
        /// </summary>
        private int? WlUsuarioId =>
            int.TryParse(User.FindFirst("WlUsuarioId")?.Value, out var id) ? id : (int?)null;

        /// <summary>
        /// Cadastra um local pela Exibidora.
        /// </summary>
        /// <remarks>
        /// O local nasce em <c>AprovacaoPendente</c> e a liberação acontece no
        /// Admin — mas isso não é decidido aqui: o core aplica a transição
        /// sozinho ao identificar que quem cadastrou é um <c>UsuarioAfiliada</c>
        /// (a conta de serviço da instância). Ver ADR-WL-004.
        ///
        /// <para><c>IdAfiliada</c> e a trilha de origem são preenchidos pelo
        /// <see cref="ICoreCadastroService"/> a partir do tenant e do JWT; o que
        /// vier no payload para esses campos é ignorado.</para>
        /// </remarks>
        [HttpPost]
        [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Create([FromBody] LocalCadastroCommand command)
        {
            if (command == null)
                return BadRequest(new { message = "Dados do local são obrigatórios." });

            // Id > 0 seria uma edição disfarçada de criação, escapando da
            // verificação de propriedade que o Update faz.
            command.Id = 0;

            var resposta = await _coreCadastro.SalvarLocalAsync(command, WlUsuarioId);
            return RepassarResposta(resposta);
        }

        /// <summary>
        /// Atualiza um local da própria exibidora.
        /// </summary>
        /// <remarks>
        /// A verificação de propriedade abaixo não é redundante com a do core.
        /// O <c>LocalCadastroHandler</c>, no ramo de edição, chama
        /// <c>local.SetAfiliada(afiliada)</c> — ou seja, editar um local de outra
        /// exibidora não seria recusado: ele seria <b>transferido</b> para a
        /// afiliada de quem chamou. Como todas as instâncias WhiteLabel usam
        /// contas de serviço equivalentes, sem esta checagem um operador poderia
        /// sequestrar o inventário alheio informando um id qualquer.
        /// </remarks>
        [HttpPut("{id}")]
        [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Update(int id, [FromBody] LocalCadastroCommand command)
        {
            if (command == null)
                return BadRequest(new { message = "Dados do local são obrigatórios." });

            var afiliadaId = _tenantContext.AfiliadaId;

            var local = await _db.Locais
                .FirstOrDefaultAsync(l => l.Id == id
                                       && l.IdAfiliada == afiliadaId
                                       && l.StatusExibicao != StatusExibicaoEnum.Deletado);

            if (local == null)
                return NotFound(new { message = "Local não encontrado." });

            local.AssertTenantAccess(afiliadaId);

            command.Id = id;

            var resposta = await _coreCadastro.SalvarLocalAsync(command, WlUsuarioId);
            return RepassarResposta(resposta);
        }

        /// <summary>
        /// Repassa a resposta do core preservando o status e o corpo, para que as
        /// notificações do domínio (mensagens de validação) cheguem à tela em vez
        /// de virarem um 500 genérico.
        /// </summary>
        private IActionResult RepassarResposta(CoreRespostaCadastro resposta)
        {
            if (resposta.Sucesso)
                return Content(resposta.Corpo ?? "{}", "application/json");

            return StatusCode(resposta.StatusCode, new
            {
                message = "O cadastro foi recusado pelo Veiculando Core.",
                detalhe = resposta.Corpo
            });
        }

        /// <summary>
        /// Lista os locais da afiliada ativa, incluindo os que aguardam aprovação.
        /// </summary>
        /// <remarks>
        /// O filtro original era <c>StatusExibicao == Ativo</c>, o que escondia
        /// justamente os locais criados pela Exibidora: eles nascem em
        /// <c>AprovacaoPendente</c> e só passam a Ativo quando o Admin aprova
        /// (ADR-WL-004). O operador cadastrava e o registro não aparecia em
        /// lugar nenhum — indistinguível de uma falha no cadastro.
        ///
        /// Deletados (-1) e Inativos (0) continuam fora. O <c>StatusExibicao</c>
        /// passou a ser projetado para o frontend poder rotular a situação em vez
        /// de assumir que tudo que veio está ativo.
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var locais = await _db.Locais
                .AsNoTracking()
                .Where(l => l.IdAfiliada == afiliadaId
                         && (l.StatusExibicao == StatusExibicaoEnum.Ativo
                          || l.StatusExibicao == StatusExibicaoEnum.AprovacaoPendente))
                .Select(l => new
                {
                    l.Id,
                    l.Codigo,
                    l.Descricao,
                    Cidade = l.Cidade.Nome,
                    UF = l.Cidade.Estado.Sigla,
                    l.FonteOrigem,
                    l.FonteTimestamp,
                    l.StatusExibicao
                })
                .ToListAsync();

            return Ok(locais);
        }

        /// <summary>
        /// Detalhe do local, com todos os campos editáveis.
        /// </summary>
        /// <remarks>
        /// A projeção precisa devolver endereço, geolocalização, código interno e
        /// palavras-chave — e não apenas o resumo da listagem. O ramo de edição do
        /// <c>LocalCadastroHandler</c> aplica <c>SetEndereco</c>,
        /// <c>SetGeolocalizacao</c>, <c>SetCodigoInterno</c> e
        /// <c>SetPalavrasChave</c> com o que vier no command, sem mesclar com o
        /// que já existe: um formulário preenchido a partir de um payload
        /// incompleto <b>apagaria</b> esses dados ao salvar.
        ///
        /// O filtro também aceita <c>AprovacaoPendente</c>, senão um local
        /// recém-cadastrado apareceria na lista mas não abriria para edição.
        /// </remarks>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var local = await _db.Locais
                .AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == id
                                       && l.IdAfiliada == afiliadaId
                                       && (l.StatusExibicao == StatusExibicaoEnum.Ativo
                                        || l.StatusExibicao == StatusExibicaoEnum.AprovacaoPendente));

            if (local == null)
                return NotFound(new { message = "Local não encontrado." });

            local.AssertTenantAccess(afiliadaId);

            return Ok(new
            {
                local.Id,
                local.Codigo,
                local.Descricao,
                local.IdCidade,
                Cidade = local.Cidade?.Nome,
                UF = local.Cidade?.Estado?.Sigla,
                local.CodigoInterno,
                local.PalavrasChave,
                local.StatusExibicao,
                Endereco = new
                {
                    local.Endereco?.Logradouro,
                    local.Endereco?.Numero,
                    local.Endereco?.Bairro,
                    local.Endereco?.Complemento,
                    local.Endereco?.Referencia,
                    Cep = new { Numero = local.Endereco?.Cep?.Numero }
                },
                Geolocalizacao = new
                {
                    Latitude = local.GeoLocalizacao != null ? local.GeoLocalizacao.Latitude : 0,
                    Longitude = local.GeoLocalizacao != null ? local.GeoLocalizacao.Longitude : 0
                },
                local.FonteOrigem,
                local.FonteUsuarioId,
                local.FonteTimestamp
            });
        }

        [HttpDelete("{id}")]
        [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Delete(int id)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var local = await _db.Locais
                .FirstOrDefaultAsync(l => l.Id == id && l.IdAfiliada == afiliadaId && l.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (local == null)
                return NotFound(new { message = "Local não encontrado." });

            local.AssertTenantAccess(afiliadaId);

            local.Delete();
            await _db.SaveChangesAsync();

            return NoContent();
        }
    }

    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize]
    [ServiceFilter(typeof(InputSanitizationFilter))]
    public class PecasController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantContext _tenantContext;
        private readonly IFileValidationService _fileValidation;
        private readonly ICoreCadastroService _coreCadastro;

        public PecasController(
            VeiculandoDataContext db,
            ITenantContext tenantContext,
            IFileValidationService fileValidation,
            ICoreCadastroService coreCadastro)
        {
            _db = db;
            _tenantContext = tenantContext;
            _fileValidation = fileValidation;
            _coreCadastro = coreCadastro;
        }

        private int? WlUsuarioId =>
            int.TryParse(User.FindFirst("WlUsuarioId")?.Value, out var id) ? id : (int?)null;

        /// <summary>
        /// Cadastra uma peça em um local da própria exibidora.
        /// </summary>
        /// <remarks>
        /// A peça também nasce aguardando aprovação, pela mesma razão do local: o
        /// <c>PecaCadastroHandler</c> chama <c>EnviarParaAprovacao()</c> quando
        /// quem cadastra é um <c>UsuarioAfiliada</c>.
        /// </remarks>
        [HttpPost]
        [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Create([FromBody] PecaCadastroCommand command)
        {
            if (command == null)
                return BadRequest(new { message = "Dados da peça são obrigatórios." });

            command.Id = 0;

            var erro = await ValidarLocalDaAfiliadaAsync(command.IdLocal);
            if (erro != null) return erro;

            var resposta = await _coreCadastro.SalvarPecaAsync(command, WlUsuarioId);
            return RepassarResposta(resposta);
        }

        /// <summary>
        /// Atualiza uma peça da própria exibidora.
        /// </summary>
        [HttpPut("{id}")]
        [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Update(int id, [FromBody] PecaCadastroCommand command)
        {
            if (command == null)
                return BadRequest(new { message = "Dados da peça são obrigatórios." });

            var afiliadaId = _tenantContext.AfiliadaId;

            var peca = await _db.Pecas
                .FirstOrDefaultAsync(p => p.Id == id
                                       && p.Local.IdAfiliada == afiliadaId
                                       && p.StatusExibicao != StatusExibicaoEnum.Deletado);

            if (peca == null)
                return NotFound(new { message = "Peça não encontrada." });

            peca.Local.AssertTenantAccess(afiliadaId);

            // O local de destino também precisa ser da exibidora: sem isso uma
            // edição poderia mover a peça para o inventário de outra afiliada.
            var erro = await ValidarLocalDaAfiliadaAsync(command.IdLocal);
            if (erro != null) return erro;

            command.Id = id;

            var resposta = await _coreCadastro.SalvarPecaAsync(command, WlUsuarioId);
            return RepassarResposta(resposta);
        }

        private async Task<IActionResult> ValidarLocalDaAfiliadaAsync(int idLocal)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var local = await _db.Locais
                .FirstOrDefaultAsync(l => l.Id == idLocal
                                       && l.IdAfiliada == afiliadaId
                                       && l.StatusExibicao != StatusExibicaoEnum.Deletado);

            if (local == null)
                return NotFound(new { message = "Local não encontrado." });

            local.AssertTenantAccess(afiliadaId);
            return null;
        }

        private IActionResult RepassarResposta(CoreRespostaCadastro resposta)
        {
            if (resposta.Sucesso)
                return Content(resposta.Corpo ?? "{}", "application/json");

            return StatusCode(resposta.StatusCode, new
            {
                message = "O cadastro foi recusado pelo Veiculando Core.",
                detalhe = resposta.Corpo
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var pecas = await _db.Pecas
                .AsNoTracking()
                .Where(p => p.Local.IdAfiliada == afiliadaId && p.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(p => new
                {
                    p.Id,
                    p.Codigo,
                    p.IdLocal,
                    LocalCodigo = p.Local.Codigo,
                    FormatoDimensao = p.Formato != null ? p.Formato.ToString() : null,
                    p.ValorPadrao,
                    p.FonteOrigem
                })
                .ToListAsync();

            return Ok(pecas);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var peca = await _db.Pecas
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id && p.Local.IdAfiliada == afiliadaId && p.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (peca == null)
                return NotFound(new { message = "Peça não encontrada." });

            peca.Local.AssertTenantAccess(afiliadaId);

            return Ok(new
            {
                peca.Id,
                peca.Codigo,
                peca.IdLocal,
                LocalCodigo = peca.Local.Codigo,
                FormatoDimensao = peca.Formato != null ? peca.Formato.ToString() : null,
                peca.ValorPadrao,
                peca.FonteOrigem
            });
        }

        [HttpPost("locais/{idLocal}/pecas/{pecaId}/foto")]
        [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> UploadFotoPeca(int idLocal, int pecaId, IFormFile foto)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var local = await _db.Locais
                .FirstOrDefaultAsync(l => l.Id == idLocal && l.IdAfiliada == afiliadaId && l.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (local == null)
                return NotFound(new { message = "Local não encontrado." });

            local.AssertTenantAccess(afiliadaId);

            const long maxBytes = 10 * 1024 * 1024; // Max 10MB conforme TP-2
            if (!_fileValidation.IsValidFile(foto, maxBytes, out var errorMessage))
            {
                return BadRequest(new { message = errorMessage });
            }

            var safeFilename = _fileValidation.SanitizeFileName(foto.FileName);

            return Ok(new
            {
                message = "Foto da peça recebida e validada com sucesso.",
                fileName = safeFilename,
                idLocal = idLocal,
                pecaId = pecaId
            });
        }
    }
}
