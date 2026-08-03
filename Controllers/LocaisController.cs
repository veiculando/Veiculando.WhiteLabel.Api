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
    public class LocaisController : WlCoreProxyControllerBase
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

            // Cidade e Estado são navegações: com lazy loading desligado no contexto
            // do core, sem Include os campos Cidade/UF do detalhe voltavam sempre
            // null. O `?.` na projeção escondia isso — o formulário de edição abria
            // com a cidade em branco e salvava por cima.
            var local = await _db.Locais
                .AsNoTracking()
                .Include(l => l.Cidade.Estado)
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
    public class PecasController : WlCoreProxyControllerBase
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

            // Include necessário pelo mesmo motivo do GetById: sem ele `peca.Local`
            // vem null e a asserção de tenant abaixo vira no-op silencioso.
            var peca = await _db.Pecas
                .Include(p => p.Local)
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

            // O Include é obrigatório: o contexto do core tem LazyLoadingEnabled =
            // false, e o `p.Local.IdAfiliada` do WHERE vira JOIN no SQL sem popular
            // a navegação. Sem ele `peca.Local` vem null e o acesso a
            // `peca.Local.Codigo` logo abaixo estoura NullReferenceException — 500
            // em todo GET de detalhe de peça.
            var peca = await _db.Pecas
                .AsNoTracking()
                .Include(p => p.Local)
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

        /// <summary>
        /// Valida uma foto de peça. **Não persiste o arquivo** — ver o remarks.
        /// </summary>
        /// <remarks>
        /// Este endpoint respondia 200 com "Foto da peça recebida e validada com
        /// sucesso" sem gravar coisa alguma: a validação rodava, o nome sanitizado
        /// era devolvido e o arquivo era descartado no fim do request. Para quem
        /// usa o painel isso é indistinguível de um upload que funcionou — o
        /// operador só descobriria a perda quando a foto não aparecesse.
        ///
        /// <para>A gravação depende de integrar o BFF ao Veiculando.FileServer,
        /// que é escopo do TP-2 e traz decisões próprias (autenticação entre os
        /// dois serviços, isolamento de tenant nos diretórios, colisão de nomes —
        /// hoje o FileServer grava pelo nome original e aceita só .jpg até 2MB).
        /// Enquanto isso não existe, a resposta honesta é 501: o frontend já
        /// traduz esse status para "Recurso ainda não implementado no servidor"
        /// em <c>api-error.ts</c>.</para>
        ///
        /// <para>A validação foi mantida antes do 501 de propósito — assim um
        /// arquivo inválido continua sendo recusado com a mensagem específica, e
        /// o contrato de validação segue exercitado quando a persistência entrar.</para>
        /// </remarks>
        [HttpPost("locais/{idLocal}/pecas/{pecaId}/foto")]
        [Authorize(Policy = AuthorizationSetup.PecaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> UploadFotoPeca(int idLocal, int pecaId, IFormFile foto)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            // A peça precisa existir E pertencer ao local informado. Antes só o
            // local era verificado e o pecaId era ecoado de volta sem checagem
            // nenhuma: com a persistência do TP-2 no lugar, isso viraria gravação
            // de foto em peça de outra afiliada informando um id qualquer.
            var peca = await _db.Pecas
                .AsNoTracking()
                .Include(p => p.Local)
                .FirstOrDefaultAsync(p => p.Id == pecaId
                                       && p.IdLocal == idLocal
                                       && p.Local.IdAfiliada == afiliadaId
                                       && p.StatusExibicao != StatusExibicaoEnum.Deletado);

            if (peca == null)
                return NotFound(new { message = "Peça não encontrada neste local." });

            peca.Local.AssertTenantAccess(afiliadaId);

            const long maxBytes = 10 * 1024 * 1024; // Max 10MB conforme TP-2
            if (!_fileValidation.IsValidFile(foto, maxBytes, out var errorMessage))
            {
                return BadRequest(new { message = errorMessage });
            }

            return StatusCode(501, new
            {
                message = "O envio de fotos de peça ainda não está disponível: o arquivo " +
                          "foi validado, mas o armazenamento será entregue no TP-2. " +
                          "Nenhuma foto foi salva."
            });
        }
    }
}
