using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Veiculando.Data.Contexts;
using Veiculando.WhiteLabel.Api.Middleware;
using Veiculando.WhiteLabel.Api.Services;

namespace Veiculando.WhiteLabel.Api.Controllers
{
    /// <summary>
    /// Catálogo que o anunciante pode consultar antes de iniciar uma cotação.
    /// A reserva e a disponibilidade por período continuam sendo decisões do
    /// fluxo de checkout, nunca desta listagem.
    /// </summary>
    [ApiController]
    [Route("api/wl/app/inventory")]
    [AllowAnonymous]
    public sealed class AppInventoryController : ControllerBase
    {
        private readonly ITenantQueries _tenant;
        private readonly IWlUploadStorage _storage;
        private readonly VeiculandoDataContext _db;

        public AppInventoryController(ITenantQueries tenant, IWlUploadStorage storage, VeiculandoDataContext db)
        {
            _tenant = tenant;
            _storage = storage;
            _db = db;
        }

        /// <summary>
        /// Opções estáveis dos filtros, sempre derivadas do catálogo inteiro da
        /// exibidora. Uma busca sem resultados não deve apagar os tipos de mídia.
        /// </summary>
        [HttpGet("filters")]
        public async Task<IActionResult> Filters()
        {
            var pecas = await PecasAtivasAsync();
            var tipos = pecas.Select(p => p.PeriodicidadePadrao.Tipo).Distinct().ToArray();
            var now = DateTime.UtcNow;
            var periodos = await _db.Periodos.AsNoTracking()
                .Where(p => p.StatusExibicao == StatusExibicaoEnum.Ativo && p.DataFim >= now)
                .OrderBy(p => p.DataInicio)
                .ToListAsync();
            var idades = await _db.FaixaEtaria.AsNoTracking().OrderBy(x => x.Minimo)
                .Select(x => new { id = x.Id, name = x.Nome }).ToListAsync();
            var rendas = await _db.FaixaRenda.AsNoTracking().OrderBy(x => x.Minimo)
                .Select(x => new { id = x.Id, name = x.Nome }).ToListAsync();
            var perfis = await _db.PerfilPsicografico.AsNoTracking().OrderBy(x => x.Nome)
                .Select(x => new { id = x.Id, name = x.Nome }).ToListAsync();
            return Ok(new
            {
                mediaTypes = pecas.Select(p => p.Suporte?.Nome)
                    .Where(nome => !string.IsNullOrWhiteSpace(nome))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(nome => nome)
                    .ToList(),
                cities = pecas.Select(p => new { name = p.Local?.Cidade?.Nome, state = p.Local?.Cidade?.Estado?.Sigla })
                    .Where(c => !string.IsNullOrWhiteSpace(c.name))
                    .GroupBy(c => (c.name, c.state))
                    .Select(g => new { g.Key.name, g.Key.state })
                    .OrderBy(c => c.name)
                    .ToList(),
                periods = periodos.Where(p => tipos.Contains(p.Periodicidade.Tipo))
                    .Select(p => new { code = p.Codigo, name = p.Nome, periodicity = p.Periodicidade.Nome, startDate = p.DataInicio, endDate = p.DataFim })
                    .ToList(),
                audience = new { ageRanges = idades, incomeRanges = rendas, psychographicProfiles = perfis }
            });
        }

        [HttpGet]
        public async Task<IActionResult> Search(
            [FromQuery(Name = "query")] string term,
            [FromQuery] string city,
            [FromQuery] string mediaType,
            [FromQuery] decimal? minPrice,
            [FromQuery] decimal? maxPrice,
            [FromQuery] string periodCode,
            [FromQuery] int? gender,
            [FromQuery] string ageRangeIds,
            [FromQuery] string incomeRangeIds,
            [FromQuery] string psychographicIds)
        {
            if (gender.HasValue && gender.Value != 1 && gender.Value != 2)
                return BadRequest(new { message = "Gênero inválido." });
            if (!TryIds(ageRangeIds, out var idades) || !TryIds(incomeRangeIds, out var rendas) ||
                !TryIds(psychographicIds, out var perfis))
                return BadRequest(new { message = "Filtro de público inválido." });
            Periodo periodo = null;
            if (!string.IsNullOrWhiteSpace(periodCode))
            {
                var now = DateTime.UtcNow;
                periodo = await _db.Periodos.AsNoTracking().FirstOrDefaultAsync(p =>
                    p.Codigo == periodCode && p.StatusExibicao == StatusExibicaoEnum.Ativo && p.DataFim >= now);
                if (periodo == null) return BadRequest(new { message = "Período inválido ou encerrado." });
            }
            var temPublico = gender.HasValue || idades.Length > 0 || rendas.Length > 0 || perfis.Length > 0;
            var pecas = await PecasAtivasAsync(temPublico);
            // A propriedade Query de um DTO chamado `query` colide com o prefixo
            // do model binder: ?query=Paulista chegava como filtro vazio.
            var filtros = new InventorySearchQuery
            {
                Query = term, City = city, MediaType = mediaType,
                MinPrice = minPrice, MaxPrice = maxPrice
            };
            var filtradas = AplicarFiltros(pecas, filtros)
                .Where(p => periodo == null || p.PeriodicidadePadrao.Tipo == periodo.Periodicidade.Tipo)
                .ToList();
            var indisponiveis = periodo == null ? new HashSet<int>() : new HashSet<int>(await _tenant.PecaPeriodoStatus
                .Where(s => s.IdPeriodo == periodo.Id && s.Status != StatusPecaPeriodoEnum.Disponivel)
                .Select(s => s.IdPeca).ToListAsync());
            var pontuacoes = filtradas.ToDictionary(p => p.Id, p => temPublico
                ? p.Local.IndicePublicoAlvo(gender ?? 0, idades, rendas, perfis, Array.Empty<int>(), Array.Empty<int>()) : 0);
            var resultado = filtradas.OrderByDescending(p => pontuacoes[p.Id])
                .Select(p => Mapear(p, !indisponiveis.Contains(p.Id), pontuacoes[p.Id])).ToList();
            return Ok(resultado);
        }

        [HttpGet("{code}")]
        public async Task<IActionResult> GetByCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return NotFound(new { message = "Peça não encontrada." });

            var peca = (await PecasAtivasAsync())
                .FirstOrDefault(p => string.Equals(p.Codigo, code.Trim(), StringComparison.OrdinalIgnoreCase));
            if (peca == null) return NotFound(new { message = "Peça não encontrada." });

            return Ok(Mapear(peca));
        }

        [HttpGet("{code}/photo")]
        public async Task<IActionResult> GetPhoto(string code, CancellationToken ct)
        {
            var peca = await _tenant.Pecas.AsNoTracking().FirstOrDefaultAsync(p =>
                p.Codigo == code && p.StatusExibicao == StatusExibicaoEnum.Ativo &&
                p.Local.StatusExibicao == StatusExibicaoEnum.Ativo, ct);
            if (peca == null || !HasWhiteLabelPhoto(peca)) return NotFound();

            var key = $"tenant-{_tenant.AfiliadaId}/pecas/{peca.Id}/{peca.Foto.ArquivoNome}";
            try
            {
                var info = await _storage.InfoAsync(key, ct);
                if (info.ContentType != "image/jpeg" && info.ContentType != "image/png") return NotFound();
                var stream = await _storage.ReadAsync(key, ct);
                Response.Headers.CacheControl = "public, max-age=300";
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(stream, info.ContentType);
            }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (StorageException ex) when (ex.RequestInformation?.HttpStatusCode == 404) { return NotFound(); }
        }

        private Task<List<Peca>> PecasAtivasAsync(bool incluirPublico = false)
        {
            var query = _tenant.Pecas.AsNoTracking()
                .Include(p => p.Local)
                .Include(p => p.Local.Cidade)
                .Include(p => p.Local.Cidade.Estado)
                .Include(p => p.Local.Publico)
                .Include(p => p.Suporte)
                .Where(p => p.StatusExibicao == StatusExibicaoEnum.Ativo &&
                            p.Local.StatusExibicao == StatusExibicaoEnum.Ativo);
            if (incluirPublico)
                query = query.Include(p => p.Local.Publico.DistribuicaoGenero)
                    .Include(p => p.Local.Publico.DistribuicaoEtaria)
                    .Include(p => p.Local.Publico.DistribuicaoRenda)
                    .Include(p => p.Local.Publico.PerfisPsicograficos);
            return query.ToListAsync();
        }

        private static IEnumerable<Peca> AplicarFiltros(IEnumerable<Peca> pecas, InventorySearchQuery query)
        {
            var termo = query?.Query?.Trim();
            var cidade = query?.City?.Trim();
            var tipoMidia = query?.MediaType?.Trim();

            return pecas.Where(p =>
                (string.IsNullOrWhiteSpace(termo) || Contem(p.Codigo, termo) || Contem(p.Local?.Codigo, termo) ||
                 Contem(p.Local?.Descricao, termo) || Contem(p.Local?.Endereco?.ToString(), termo) ||
                 Contem(p.Local?.Cidade?.Nome, termo) || Contem(p.Suporte?.Nome, termo)) &&
                (string.IsNullOrWhiteSpace(cidade) || Contem(p.Local?.Cidade?.Nome, cidade)) &&
                (string.IsNullOrWhiteSpace(tipoMidia) || string.Equals(p.Suporte?.Nome, tipoMidia, StringComparison.OrdinalIgnoreCase)) &&
                (!query.MinPrice.HasValue || p.ValorPadrao >= query.MinPrice.Value) &&
                (!query.MaxPrice.HasValue || p.ValorPadrao <= query.MaxPrice.Value));
        }

        private static bool Contem(string valor, string termo) =>
            !string.IsNullOrWhiteSpace(valor) && valor.IndexOf(termo, StringComparison.OrdinalIgnoreCase) >= 0;

        private static object Mapear(Peca peca, bool disponivel = true, int audienciaMatch = 0)
        {
            var local = peca.Local;
            return new
            {
                id = peca.Id,
                localId = peca.IdLocal,
                code = peca.Codigo,
                name = local?.Descricao ?? local?.Endereco?.ToString() ?? peca.Codigo,
                address = local?.Endereco?.ToString() ?? local?.Descricao ?? local?.Codigo ?? string.Empty,
                city = local?.Cidade?.Nome ?? string.Empty,
                state = local?.Cidade?.Estado?.Sigla ?? string.Empty,
                latitude = local?.GeoLocalizacao?.Latitude ?? 0,
                longitude = local?.GeoLocalizacao?.Longitude ?? 0,
                mediaType = peca.Suporte?.Nome ?? string.Empty,
                format = peca.Formato?.ToString() ?? string.Empty,
                price = peca.ValorPadrao,
                tablePrice = peca.ValorTabela,
                periodicity = peca.PeriodicidadePadrao?.Nome,
                imageUrl = HasWhiteLabelPhoto(peca) ? $"/api/wl/app/inventory/{Uri.EscapeDataString(peca.Codigo)}/photo" : null,
                audience = local?.Publico?.Audiencia,
                audienceMatch = audienciaMatch,
                rating = peca.AvaliacaoQuantidade > 0 ? (decimal?)peca.AvaliacaoMedia : null,
                ratingCount = peca.AvaliacaoQuantidade,
                cpm = peca.CPM > 0 ? (decimal?)peca.CPM : null,
                // "available" aqui significa que o item está publicado no catálogo.
                // A disponibilidade temporal é revalidada ao cotar/finalizar o pedido.
                available = disponivel,
                illuminated = peca.Iluminacao,
                viewAngle = peca.AnguloDeVisao,
                permit = peca.Alvara,
                trafficLight = peca.Semaforo,
                streetViewUrl = peca.StreetView?.Url,
                road = peca.Via == null ? null : new
                {
                    lanes = peca.Via.Faixas,
                    speed = peca.Via.Velociade,
                    pedestrians = peca.Via.Pedestre.ToString()
                }
            };
        }

        private static bool HasWhiteLabelPhoto(Peca peca) =>
            !string.IsNullOrEmpty(peca.Foto?.ArquivoNome) &&
            Regex.IsMatch(peca.Foto.ArquivoNome, @"^wl-[a-f0-9]{32}\.(jpg|png)$", RegexOptions.CultureInvariant);

        private static bool TryIds(string raw, out int[] ids)
        {
            ids = Array.Empty<int>();
            if (string.IsNullOrWhiteSpace(raw)) return true;
            var parts = raw.Split(',');
            if (parts.Length > 20) return false;
            var values = new List<int>();
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var id) || id <= 0) return false;
                values.Add(id);
            }
            ids = values.Distinct().ToArray();
            return true;
        }

        public sealed class InventorySearchQuery
        {
            public string Query { get; set; }
            public string City { get; set; }
            public string MediaType { get; set; }
            public decimal? MinPrice { get; set; }
            public decimal? MaxPrice { get; set; }
        }
    }
}
