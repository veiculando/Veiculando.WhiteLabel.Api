using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities.WhiteLabel;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;
using BC = BCrypt.Net.BCrypt;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    [ApiController]
    [Route("api/wl/[controller]")]
    [Authorize]
    [ServiceFilter(typeof(InputSanitizationFilter))]
    public class UsuariosController : ControllerBase
    {
        private readonly VeiculandoDataContext _db;
        private readonly ITenantContext _tenantContext;

        public UsuariosController(VeiculandoDataContext db, ITenantContext tenantContext)
        {
            _db = db;
            _tenantContext = tenantContext;
        }

        /// <summary>
        /// Lista os operadores WlUsuarioAfiliada pertencentes à afiliada ativa.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var usuarios = await _db.WlUsuariosAfiliada
                .AsNoTracking()
                .Where(u => u.AfiliadaId == afiliadaId && u.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(u => new WlUsuarioDto
                {
                    Id = u.Id,
                    Nome = u.Nome,
                    Email = u.Email.Endereco,
                    Cargo = u.Cargo,
                    Departamento = u.Departamento,
                    TelefoneComercial = u.TelefoneComercial,
                    DataUltimoLogin = u.DataUltimoLogin,
                    Permissoes = u.PermissoesRaw != null ? u.PermissoesRaw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) : new string[0]
                })
                .ToListAsync();

            return Ok(usuarios);
        }

        /// <summary>
        /// Obtém o detalhe de um operador por ID com validação Anti-IDOR.
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var u = await _db.WlUsuariosAfiliada
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == id && x.AfiliadaId == afiliadaId && x.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (u == null)
                return NotFound(new { message = "Usuário não encontrado." });

            u.AssertTenantAccess(afiliadaId);

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
        public async Task<IActionResult> Create([FromBody] WlUsuarioCreateDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.Email) || string.IsNullOrWhiteSpace(dto?.Senha) || string.IsNullOrWhiteSpace(dto?.Nome))
                return BadRequest(new { message = "Nome, email e senha são obrigatórios." });

            if (dto.Permissoes != null && !WlPermissoesValidas.ValidarPermissoes(dto.Permissoes, out var invalidas))
            {
                return BadRequest(new { message = $"Permissões inválidas detectadas: {string.Join(", ", invalidas)}" });
            }

            var afiliadaId = _tenantContext.AfiliadaId;
            var normalizedEmail = dto.Email.ToLower().Trim();

            var emailExiste = await _db.WlUsuarios
                .AnyAsync(u => u.Email.Endereco == normalizedEmail && u.AfiliadaId == afiliadaId && u.StatusExibicao == StatusExibicaoEnum.Ativo);

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
        /// Atualiza os dados de um operador com validação Anti-IDOR e Whitelist de permissões.
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] WlUsuarioUpdateDto dto)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            // Anti-IDOR: AfiliadaId filtrado diretamente na query SQL — não via
            // AssertTenantAccess pós-materialização, que poderia vazar registros de
            // outras afiliadas em mensagens de erro ou logs intermediários.
            var usuario = await _db.WlUsuariosAfiliada
                .FirstOrDefaultAsync(u => u.Id == id && u.AfiliadaId == afiliadaId && u.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (usuario == null)
                return NotFound(new { message = "Usuário não encontrado." });

            usuario.AssertTenantAccess(afiliadaId);

            if (dto.Permissoes != null && !WlPermissoesValidas.ValidarPermissoes(dto.Permissoes, out var invalidas))
            {
                return BadRequest(new { message = $"Permissões inválidas detectadas: {string.Join(", ", invalidas)}" });
            }

            usuario.AtualizarPermissoes(dto.Permissoes);
            if (!usuario.IsValid())
                return BadRequest(usuario.Notifications);

            await _db.SaveChangesAsync();
            return Ok(new { message = "Usuário atualizado com sucesso." });
        }

        /// <summary>
        /// Remove um operador da instância (Soft Delete) com validação Anti-IDOR.
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            // Anti-IDOR: AfiliadaId filtrado diretamente na query SQL.
            var usuario = await _db.WlUsuariosAfiliada
                .FirstOrDefaultAsync(u => u.Id == id && u.AfiliadaId == afiliadaId && u.StatusExibicao == StatusExibicaoEnum.Ativo);

            if (usuario == null)
                return NotFound(new { message = "Usuário não encontrado." });

            usuario.AssertTenantAccess(afiliadaId);

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
