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

        public AppInventoryController(ITenantQueries tenant, IWlUploadStorage storage)
        {
            _tenant = tenant;
            _storage = storage;
        }

        [HttpGet]
        public async Task<IActionResult> Search(
            [FromQuery(Name = "query")] string term,
            [FromQuery] string city,
            [FromQuery] string mediaType,
            [FromQuery] decimal? minPrice,
            [FromQuery] decimal? maxPrice)
        {
            var pecas = await PecasAtivasAsync();
            // A propriedade Query de um DTO chamado `query` colide com o prefixo
            // do model binder: ?query=Paulista chegava como filtro vazio.
            var filtros = new InventorySearchQuery
            {
                Query = term, City = city, MediaType = mediaType,
                MinPrice = minPrice, MaxPrice = maxPrice
            };
            var resultado = AplicarFiltros(pecas, filtros)
                .Select(Mapear)
                .ToList();
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

        private Task<List<Peca>> PecasAtivasAsync() => _tenant.Pecas
            .AsNoTracking()
            .Include(p => p.Local)
            .Include(p => p.Local.Cidade)
            .Include(p => p.Local.Cidade.Estado)
            .Include(p => p.Local.Publico)
            .Include(p => p.Suporte)
            .Where(p => p.StatusExibicao == StatusExibicaoEnum.Ativo &&
                        p.Local.StatusExibicao == StatusExibicaoEnum.Ativo)
            .ToListAsync();

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

        private static object Mapear(Peca peca)
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
                rating = peca.AvaliacaoQuantidade > 0 ? (decimal?)peca.AvaliacaoMedia : null,
                ratingCount = peca.AvaliacaoQuantidade,
                cpm = peca.CPM > 0 ? (decimal?)peca.CPM : null,
                // "available" aqui significa que o item está publicado no catálogo.
                // A disponibilidade temporal é revalidada ao cotar/finalizar o pedido.
                available = true,
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
