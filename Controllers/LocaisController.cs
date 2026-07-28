using System;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
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

        public LocaisController(VeiculandoDataContext db, ITenantContext tenantContext)
        {
            _db = db;
            _tenantContext = tenantContext;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var locais = await _db.Locais
                .Where(l => l.IdAfiliada == afiliadaId && l.StatusExibicao == StatusExibicaoEnum.Ativo)
                .Select(l => new
                {
                    l.Id,
                    l.Codigo,
                    l.Descricao,
                    Cidade = l.Cidade.Nome,
                    UF = l.Cidade.Estado.Sigla,
                    l.FonteOrigem,
                    l.FonteTimestamp
                })
                .ToListAsync();

            return Ok(locais);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var local = await _db.Locais
                .FirstOrDefaultAsync(l => l.Id == id && l.IdAfiliada == afiliadaId && l.StatusExibicao == StatusExibicaoEnum.Ativo);

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
                local.FonteOrigem,
                local.FonteTimestamp
            });
        }

        [HttpDelete("{id}")]
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

        public PecasController(VeiculandoDataContext db, ITenantContext tenantContext, IFileValidationService fileValidation)
        {
            _db = db;
            _tenantContext = tenantContext;
            _fileValidation = fileValidation;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var afiliadaId = _tenantContext.AfiliadaId;

            var pecas = await _db.Pecas
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
