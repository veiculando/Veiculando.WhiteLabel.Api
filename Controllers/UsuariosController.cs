using System;
using System.Data.Entity;
using System.Data.Entity.Infrastructure;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
    public class UsuariosController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;
        private readonly WlPublicLinks _publicLinks;
        private readonly IWlTenantResolver _tenantResolver;
        private readonly IWlPasswordEmailSender _emailSender;
        private readonly ILogger<UsuariosController> _logger;
        private readonly WlPasswordEmailOptions _emailOptions;

        public UsuariosController(
            VeiculandoDataContext db,
            ITenantQueries tenant,
            WlPublicLinks publicLinks,
            IWlTenantResolver tenantResolver,
            IWlPasswordEmailSender emailSender,
            ILogger<UsuariosController> logger,
            IOptions<WlPasswordEmailOptions> emailOptions)
        {
            _db = db;
            _tenant = tenant;
            _publicLinks = publicLinks;
            _tenantResolver = tenantResolver;
            _emailSender = emailSender;
            _logger = logger;
            _emailOptions = emailOptions.Value;
        }

        /// <summary>
        /// Lista os operadores WlUsuarioAfiliada pertencentes à afiliada ativa.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var afiliadaId = _tenant.AfiliadaId;

            // `String.Split` não tem tradução para SQL: deixá-lo dentro do `Select`
            // faz o EF6 lançar NotSupportedException ao montar a query, e a listagem
            // de operadores quebrava por inteiro — não é um detalhe de performance.
            //
            // A projeção continua existindo (e não `ToListAsync()` na entidade) para
            // que SenhaHash e TokenRecuperacaoHash não sejam trazidos para memória.
            // O split acontece depois da materialização, já em LINQ to Objects.
            var brutos = await _tenant.UsuariosAfiliada
                .AsNoTracking()
                .Where(u => u.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(u => new
                {
                    u.Id,
                    u.Nome,
                    Email = u.Email.Endereco,
                    u.Cargo,
                    u.Departamento,
                    u.TelefoneComercial,
                    u.DataUltimoLogin,
                    u.StatusConvite,
                    u.PermissoesRaw
                })
                .ToListAsync();

            // Mesma semântica de WlUsuario.ObterPermissoes().
            var usuarios = brutos
                .Select(u => new WlUsuarioDto
                {
                    Id = u.Id,
                    Nome = u.Nome,
                    Email = u.Email,
                    Cargo = u.Cargo,
                    Departamento = u.Departamento,
                    TelefoneComercial = u.TelefoneComercial,
                    DataUltimoLogin = u.DataUltimoLogin,
                    StatusConvite = u.StatusConvite.ToString(),
                    Permissoes = string.IsNullOrWhiteSpace(u.PermissoesRaw)
                        ? Array.Empty<string>()
                        : u.PermissoesRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                })
                .ToList();

            return Ok(usuarios);
        }

        /// <summary>
        /// Obtém o detalhe de um operador por ID com validação Anti-IDOR.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var afiliadaId = _tenant.AfiliadaId;

            var u = await _tenant.UsuariosAfiliada
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id && x.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (u == null)
                return NotFound(new { message = "Usuário não encontrado." });


            return Ok(new WlUsuarioDto
            {
                Id = u.Id,
                Nome = u.Nome,
                Email = u.Email.Endereco,
                Cargo = u.Cargo,
                Departamento = u.Departamento,
                TelefoneComercial = u.TelefoneComercial,
                DataUltimoLogin = u.DataUltimoLogin,
                StatusConvite = u.StatusConvite.ToString(),
                Permissoes = u.ObterPermissoes()
            });
        }

        /// <summary>
        /// Cadastra um novo operador com validação de Whitelist de permissões no servidor.
        /// </summary>
        [HttpPost]
        [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Create([FromBody] WlUsuarioCreateDto dto, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(dto?.Email) || string.IsNullOrWhiteSpace(dto?.Nome))
                return BadRequest(new { message = "Nome e email são obrigatórios." });

            if (dto.Permissoes != null && !WlPermissoesValidas.ValidarPermissoes(dto.Permissoes, out var invalidas))
            {
                return BadRequest(new { message = $"Permissões inválidas detectadas: {string.Join(", ", invalidas)}" });
            }

            var afiliadaId = _tenant.AfiliadaId;
            var normalizedEmail = dto.Email.ToLowerInvariant().Trim();

            // A checagem cobre TODOS os status, não só Ativo — inclusive operadores
            // excluídos.
            //
            // A exclusão é soft (StatusExibicao = Deletado), mas o índice
            // UK_WlUsuario_Email_Afiliada é único sobre (Email, AfiliadaId) sem
            // filtro: o EF6 IndexAttribute não expressa índice filtrado, então a
            // linha excluída continua ocupando o e-mail no banco. Filtrando por
            // Ativo aqui, recriar um operador excluído passava nesta validação e
            // estourava violação de constraint no SaveChanges — o operador via um
            // 500 sem explicação em vez da mensagem de e-mail duplicado.
            //
            // O e-mail de um operador excluído permanece reservado. É o
            // comportamento desejado: preserva a trilha de auditoria do registro
            // antigo e evita que um novo operador herde a identidade de um
            // desligado.
            var emailExiste = await _tenant.Usuarios
                .AnyAsync(u => u.Email.Endereco == normalizedEmail);

            if (emailExiste)
                return BadRequest(new { message = "Já existe um usuário cadastrado com este e-mail nesta instância." });

            var novoUsuario = new WlUsuarioAfiliada(
                dto.Nome,
                normalizedEmail,
                null,
                afiliadaId,
                dto.Cargo,
                dto.Departamento,
                dto.TelefoneComercial,
                dto.Permissoes
            );

            if (!novoUsuario.IsValid())
                return BadRequest(novoUsuario.Notifications);

            // O usuário pendente permanece recuperável se o provedor estiver fora.
            // Não manter transação SQL aberta durante uma chamada de rede.
            _db.WlUsuariosAfiliada.Add(novoUsuario);
            await _db.SaveChangesAsync(ct);
            var erroEnvio = await EnviarConvite(novoUsuario, ct);
            if (erroEnvio != null) return erroEnvio;

            return CreatedAtAction(nameof(GetById), new { id = novoUsuario.Id },
                new { id = novoUsuario.Id, message = "Usuário criado. Enviamos o convite para criação de senha." });
        }

        [HttpPost("{id}/reenviar-convite")]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> ReenviarConvite(int id, CancellationToken ct)
        {
            var usuario = await _tenant.UsuariosAfiliada
                .FirstOrDefaultAsync(u => u.Id == id && u.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
            if (usuario == null) return NotFound(new { message = "Usuário não encontrado." });
            if (usuario.StatusConvite != StatusConviteWlEnum.Pendente)
                return Conflict(new { message = "Este operador já concluiu o primeiro acesso. Utilize a recuperação de senha." });

            return await EnviarConvite(usuario, ct)
                ?? Ok(new { message = "Novo convite enviado. O link anterior não é mais válido." });
        }

        private async Task<IActionResult> EnviarConvite(WlUsuarioAfiliada usuario, CancellationToken ct)
        {
            var tokenBruto = usuario.GerarTokenConvite(TimeSpan.FromHours(_emailOptions.ConviteValidadeHoras));
            var hashDesteEnvio = usuario.TokenConviteHash;
            try
            {
                // Rowversion impede que reenvio e aceite sobrescrevam um ao outro.
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new { message = "O operador foi atualizado por outra solicitação. Recarregue a lista." });
            }

            bool enviado;
            try
            {
                var branding = await _tenantResolver.ObterBrandingAsync(_tenant.AfiliadaId);
                var link = _publicLinks.Convite(tokenBruto, usuario.Email.Endereco);
                await _emailSender.EnviarConviteAsync(usuario.Email.Endereco, branding?.NomeExibicao, link, ct);
                enviado = true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                // Não registrar exception/link: transportes podem incluir PII na mensagem.
                _logger.LogError("Falha de convite. AfiliadaId={AfiliadaId} UsuarioId={UsuarioId} Tipo={Tipo}",
                    _tenant.AfiliadaId, usuario.Id, ex.GetType().Name);
                enviado = false;
            }

            await _db.Entry(usuario).ReloadAsync(ct);
            if (usuario.TokenConviteHash == hashDesteEnvio)
            {
                if (enviado) usuario.RegistrarEnvioConvite();
                else usuario.InvalidarTokenConvite();
                try { await _db.SaveChangesAsync(ct); }
                catch (DbUpdateConcurrencyException)
                {
                    // Um aceite/reenvio concorrente venceu. Nunca invalidar seu token.
                    return Conflict(new { message = "O convite foi atualizado por outra solicitação. Recarregue a lista." });
                }
            }

            return enviado ? null : StatusCode(503, new
            {
                id = usuario.Id,
                conviteEnviado = false,
                message = "Operador cadastrado, mas o convite não foi enviado. Use Reenviar convite na lista."
            });
        }

        /// <summary>
        /// Atualiza dados cadastrais e permissões de um
        /// operador, com validação Anti-IDOR e whitelist de permissões.
        /// </summary>
        /// <remarks>
        /// A versão anterior aceitava nome, cargo, departamento, telefone e senha no
        /// DTO mas aplicava **somente** <c>AtualizarPermissoes</c>: o resto era
        /// descartado sem erro e a API respondia "atualizado com sucesso". O
        /// operador via o formulário salvar e o dado voltar como antes.
        ///
        /// <para>As entidades já expunham <c>AtualizarDados</c> e
        /// <c>AlterarSenha</c>; faltava chamá-las.</para>
        ///
        /// <para>Campos ausentes no payload são preservados — a atualização é
        /// parcial por campo, não substituição do registro. Assim uma tela que
        /// edite só permissões não zera o cargo do operador.</para>
        /// </remarks>
        [HttpPut("{id}")]
        [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Update(int id, [FromBody] WlUsuarioUpdateDto dto)
        {
            if (dto == null)
                return BadRequest(new { message = "Dados do usuário são obrigatórios." });

            var afiliadaId = _tenant.AfiliadaId;

            // O recorte por afiliada vem de ITenantQueries, aplicado na própria
            // query: um id de outra exibidora simplesmente não existe aqui, e a
            // resposta é 404 — 403 confirmaria a existência do registro.
            var usuario = await _tenant.UsuariosAfiliada
                .FirstOrDefaultAsync(u => u.Id == id && u.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (usuario == null)
                return NotFound(new { message = "Usuário não encontrado." });


            if (dto.Permissoes != null && !WlPermissoesValidas.ValidarPermissoes(dto.Permissoes, out var invalidas))
            {
                return BadRequest(new { message = $"Permissões inválidas detectadas: {string.Join(", ", invalidas)}" });
            }

            usuario.AtualizarDados(
                // `??` e não `?? string.Empty`: omitir o campo mantém o valor atual.
                string.IsNullOrWhiteSpace(dto.Nome) ? usuario.Nome : dto.Nome.Trim(),
                dto.Cargo ?? usuario.Cargo,
                dto.Departamento ?? usuario.Departamento,
                dto.TelefoneComercial ?? usuario.TelefoneComercial);

            if (dto.Permissoes != null)
            {
                usuario.AtualizarPermissoes(dto.Permissoes);
            }

            if (!usuario.IsValid())
                return BadRequest(usuario.Notifications);

            await _db.SaveChangesAsync();
            return Ok(new { message = "Usuário atualizado com sucesso." });
        }

        /// <summary>
        /// Remove um operador da instância (Soft Delete) com validação Anti-IDOR.
        /// </summary>
        [HttpDelete("{id}")]
        [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Delete(int id)
        {
            var afiliadaId = _tenant.AfiliadaId;

            // Anti-IDOR: AfiliadaId filtrado diretamente na query SQL.
            var usuario = await _tenant.UsuariosAfiliada
                .FirstOrDefaultAsync(u => u.Id == id && u.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (usuario == null)
                return NotFound(new { message = "Usuário não encontrado." });


            usuario.Deletar();
            await _db.SaveChangesAsync();

            return NoContent();
        }
    }

    public class WlUsuarioDto
    {
        public int Id { get; set; }
        public string Nome { get; set; }
        public string Email { get; set; }
        public string Cargo { get; set; }
        public string Departamento { get; set; }
        public string TelefoneComercial { get; set; }
        public DateTime? DataUltimoLogin { get; set; }
        public string StatusConvite { get; set; }
        public string[] Permissoes { get; set; }
    }

    public class WlUsuarioCreateDto
    {
        public string Nome { get; set; }
        public string Email { get; set; }
        public string Cargo { get; set; }
        public string Departamento { get; set; }
        public string TelefoneComercial { get; set; }
        public string[] Permissoes { get; set; }
    }

    public class WlUsuarioUpdateDto
    {
        public string Nome { get; set; }
        public string Cargo { get; set; }
        public string Departamento { get; set; }
        public string TelefoneComercial { get; set; }
        public string[] Permissoes { get; set; }
    }
}
