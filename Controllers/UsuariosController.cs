using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Configurations;
using Veiculando.WhiteLabel.Api.Middleware;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
    public class UsuariosController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantQueries _tenant;

        /// <summary>Mesmo mínimo exigido no cadastro e no formulário do painel.</summary>
        private const int SenhaTamanhoMinimo = 8;

        public UsuariosController(VeiculandoDataContext db, ITenantQueries tenant)
        {
            _db = db;
            _tenant = tenant;
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
                Permissoes = u.ObterPermissoes()
            });
        }

        /// <summary>
        /// Cadastra um novo operador com validação de Whitelist de permissões no servidor.
        /// </summary>
        [HttpPost]
        [Authorize(Policy = AuthorizationSetup.UsuarioAfiliadaGerenciar)]
        [EnableRateLimiting(Startup.RateLimitEscrita)]
        public async Task<IActionResult> Create([FromBody] WlUsuarioCreateDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.Email) || string.IsNullOrWhiteSpace(dto?.Senha) || string.IsNullOrWhiteSpace(dto?.Nome))
                return BadRequest(new { message = "Nome, email e senha são obrigatórios." });

            if (dto.Permissoes != null && !WlPermissoesValidas.ValidarPermissoes(dto.Permissoes, out var invalidas))
            {
                return BadRequest(new { message = $"Permissões inválidas detectadas: {string.Join(", ", invalidas)}" });
            }

            // O formulário do painel já exige 8 caracteres, mas validação de UI não
            // é validação: sem esta checagem uma chamada direta cria operador com
            // senha de 1 caractere.
            if (dto.Senha.Trim().Length < SenhaTamanhoMinimo)
            {
                return BadRequest(new { message = $"A senha precisa ter no mínimo {SenhaTamanhoMinimo} caracteres." });
            }

            var afiliadaId = _tenant.AfiliadaId;
            var normalizedEmail = dto.Email.ToLower().Trim();

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

            var senhaHash = BC.HashPassword(dto.Senha);
            var novoUsuario = new WlUsuarioAfiliada(
                dto.Nome,
                normalizedEmail,
                senhaHash,
                afiliadaId,
                dto.Cargo,
                dto.Departamento,
                dto.TelefoneComercial,
                dto.Permissoes
            );

            if (!novoUsuario.IsValid())
                return BadRequest(novoUsuario.Notifications);

            _db.WlUsuariosAfiliada.Add(novoUsuario);
            await _db.SaveChangesAsync();

            return CreatedAtAction(nameof(GetById), new { id = novoUsuario.Id }, new { id = novoUsuario.Id, message = "Usuário cadastrado com sucesso." });
        }

        /// <summary>
        /// Atualiza dados cadastrais, permissões e — opcionalmente — a senha de um
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

            if (!string.IsNullOrWhiteSpace(dto.Senha) && dto.Senha.Trim().Length < SenhaTamanhoMinimo)
            {
                return BadRequest(new { message = $"A senha precisa ter no mínimo {SenhaTamanhoMinimo} caracteres." });
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

            if (!string.IsNullOrWhiteSpace(dto.Senha))
            {
                // Mesmo algoritmo do cadastro e do login do painel (BCrypt).
                usuario.AlterarSenha(BC.HashPassword(dto.Senha.Trim()));
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
        public string[] Permissoes { get; set; }
    }

    public class WlUsuarioCreateDto
    {
        public string Nome { get; set; }
        public string Email { get; set; }
        public string Senha { get; set; }
        public string Cargo { get; set; }
        public string Departamento { get; set; }
        public string TelefoneComercial { get; set; }
        public string[] Permissoes { get; set; }
    }

    public class WlUsuarioUpdateDto
    {
        public string Nome { get; set; }
        public string Senha { get; set; }
        public string Cargo { get; set; }
        public string Departamento { get; set; }
        public string TelefoneComercial { get; set; }
        public string[] Permissoes { get; set; }
    }
}
