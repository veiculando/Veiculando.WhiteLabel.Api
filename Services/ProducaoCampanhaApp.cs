using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using Veiculando.Data.Contexts;
using Veiculando.Domain.Entities;
using Veiculando.Domain.Enums;
using Veiculando.WhiteLabel.Api.Middleware;

namespace Veiculando.WhiteLabel.Api.Services;

/// <summary>Uma linha da aba Produção: peças do mesmo suporte com o mesmo formato de arte final.</summary>
public sealed record LinhaProducaoApp(
    string Suporte,
    string Midia,
    string Formato,
    string Dimensao,
    string Resolucao,
    int? Duracao,
    string ExtensoesAceitas,
    string EspecificacaoCriacao,
    string EspecificacaoArquivo,
    int Quantidade);

/// <summary>
/// Aba Produção de Minhas Campanhas (VEI-RD-165a): o que a agência ou o anunciante precisa
/// produzir para a campanha, agrupado por suporte e formato de arte final.
/// </summary>
/// <remarks>
/// <para>Mesmas regras do <c>ProducaoQueryHandler</c> do Core (pedidos fora de cancelado, revisado
/// e itens indisponíveis; itens fora de deletado e indisponível; agrupamento por suporte e formato),
/// com um recorte a mais: só itens de peças desta exibidora. O handler do Core lê a campanha
/// inteira, e um pedido pode ter peças de várias afiliadas.</para>
/// <para>Pelo mesmo motivo o PDF é gerado aqui e não no FileServer: a rota
/// <c>producao/relatorio-producao</c> não tem autenticação nem recorte de tenant.</para>
/// </remarks>
public sealed class ProducaoCampanhaApp
{
    /// <summary>Filtro de status dos pedidos, igual ao seletor do Veiculando.Web.</summary>
    public static readonly IReadOnlyDictionary<int, StatusPedidoEnum[]> Filtros = new Dictionary<int, StatusPedidoEnum[]>
    {
        [1] = new[] { StatusPedidoEnum.Aprovado, StatusPedidoEnum.Checking, StatusPedidoEnum.Veiculado, StatusPedidoEnum.Finalizado },
        [2] = new[]
        {
            StatusPedidoEnum.Solicitado, StatusPedidoEnum.Confirmado, StatusPedidoEnum.Aprovado,
            StatusPedidoEnum.Checking, StatusPedidoEnum.Veiculado, StatusPedidoEnum.Finalizado
        },
        [3] = new[]
        {
            StatusPedidoEnum.Simulacao, StatusPedidoEnum.Solicitado, StatusPedidoEnum.Confirmado, StatusPedidoEnum.Aprovado,
            StatusPedidoEnum.Checking, StatusPedidoEnum.Veiculado, StatusPedidoEnum.Finalizado
        }
    };

    public static readonly IReadOnlyDictionary<int, string> NomesFiltro = new Dictionary<int, string>
    {
        [1] = "Pedidos aprovados",
        [2] = "Pedidos aprovados e em andamento",
        [3] = "Pedidos aprovados, em andamento e simulações"
    };

    private static readonly StatusPedidoEnum[] PedidosFora =
        { StatusPedidoEnum.Cancelado, StatusPedidoEnum.Revisado, StatusPedidoEnum.ItensIndisponiveis };

    private static readonly StatusPedidoItemEnum[] ItensFora =
        { StatusPedidoItemEnum.Deletado, StatusPedidoItemEnum.Indisponivel };

    private readonly VeiculandoDataContext _db;
    private readonly ITenantQueries _tenant;

    public ProducaoCampanhaApp(VeiculandoDataContext db, ITenantQueries tenant) => (_db, _tenant) = (db, tenant);

    public async Task<List<LinhaProducaoApp>> ListarAsync(IQueryable<Campanha> campanha, int filtro, CancellationToken ct)
    {
        var status = Filtros[filtro];
        var ids = campanha.Select(c => c.Id);
        var afiliada = _tenant.AfiliadaId;

        var grupos = await _db.PedidoItens.AsNoTracking()
            .Where(i => ids.Contains(i.Pedido.IdCampanha) &&
                        i.Peca.Local.IdAfiliada == afiliada &&
                        !ItensFora.Contains(i.Status) &&
                        !PedidosFora.Contains(i.Pedido.Status) &&
                        status.Contains(i.Pedido.Status))
            .GroupBy(i => new { i.Peca.IdTipoSuporte, Suporte = i.Peca.Suporte.Nome, i.Peca.IdFormatoArteFinal })
            .Select(g => new { g.Key.Suporte, g.Key.IdFormatoArteFinal, Quantidade = g.Count() })
            .ToListAsync(ct);

        // Nome, Dimensao e Resolucao são calculados na entidade e não traduzem para SQL.
        var idsFormato = grupos.Where(g => g.IdFormatoArteFinal.HasValue).Select(g => g.IdFormatoArteFinal.Value).Distinct().ToList();
        var formatos = await _db.Formatos.AsNoTracking().Where(f => idsFormato.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);

        return grupos
            .Select(g =>
            {
                var f = g.IdFormatoArteFinal.HasValue && formatos.TryGetValue(g.IdFormatoArteFinal.Value, out var x) ? x : null;
                return new LinhaProducaoApp(
                    g.Suporte,
                    f == null ? null : f.MidiaTipo == MidiaTipoEnum.Digital ? "digital" : "impressa",
                    f?.Nome,
                    f?.Dimensao,
                    f?.Resolucao,
                    f != null && f.Duracao > 0 ? f.Duracao : null,
                    f?.ExtensoesAceitas,
                    f?.EspecificacaoCriacao,
                    f?.EspecificacaoArquivo,
                    g.Quantidade);
            })
            // Core: digital antes de impressa; aqui sem formato por último e, dentro, por suporte.
            .OrderBy(l => l.Midia == "digital" ? 0 : l.Midia == "impressa" ? 1 : 2)
            .ThenBy(l => l.Suporte, StringComparer.CurrentCulture)
            .ThenBy(l => l.Formato, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>PDF A4 paisagem com a mesma tabela da tela.</summary>
    public static byte[] GerarPdf(string codigo, string nomeCampanha, int filtro, IReadOnlyList<LinhaProducaoApp> linhas)
    {
        using var document = new PdfDocument();
        document.Info.Title = $"Produção {codigo}";
        PdfPage page = null;
        XGraphics gfx = null;

        var fontTitulo = new XFont("Arial", 14, XFontStyle.Bold);
        var fontTexto = new XFont("Arial", 9, XFontStyle.Regular);
        var fontCabecalho = new XFont("Arial", 8, XFontStyle.Bold);
        var fontCelula = new XFont("Arial", 8, XFontStyle.Regular);
        var linhaGrade = new XPen(XColors.LightGray, 0.5);

        const double margem = 32;
        const double alturaLinha = 18;
        double y = 0;

        var colunas = new (string Titulo, double Peso)[]
        {
            ("SUPORTE", 2.2), ("MÍDIA", 1), ("FORMATO", 2), ("DIMENSÃO", 1.3), ("RESOLUÇÃO", 1.3),
            ("DURAÇÃO", 0.9), ("EXTENSÕES", 1.6), ("QTD.", 0.7)
        };

        double[] Larguras()
        {
            var total = colunas.Sum(c => c.Peso);
            var util = page.Width.Point - margem * 2;
            return colunas.Select(c => util * c.Peso / total).ToArray();
        }

        void Celulas(IReadOnlyList<string> valores, XFont fonte)
        {
            var larguras = Larguras();
            var x = margem;
            for (var i = 0; i < valores.Count; i++)
            {
                var alinhamento = i == valores.Count - 1 ? XStringFormats.CenterRight : XStringFormats.CenterLeft;
                gfx.DrawString(Cortar(gfx, valores[i] ?? "—", fonte, larguras[i] - 6), fonte, XBrushes.Black,
                    new XRect(x + 3, y, larguras[i] - 6, alturaLinha), alinhamento);
                x += larguras[i];
            }
            y += alturaLinha;
            gfx.DrawLine(linhaGrade, margem, y, page.Width.Point - margem, y);
        }

        void NovaPagina()
        {
            page = document.AddPage();
            page.Size = PdfSharpCore.PageSize.A4;
            page.Orientation = PdfSharpCore.PageOrientation.Landscape;
            gfx?.Dispose();
            gfx = XGraphics.FromPdfPage(page);
            y = margem;
            gfx.DrawString($"Produção · {nomeCampanha} ({codigo})", fontTitulo, XBrushes.Black,
                new XRect(margem, y, page.Width.Point - margem * 2, 20), XStringFormats.TopLeft);
            y += 22;
            gfx.DrawString($"{NomesFiltro[filtro]} · gerado em {DateTime.Now:dd/MM/yyyy HH:mm}", fontTexto, XBrushes.Gray,
                new XRect(margem, y, page.Width.Point - margem * 2, 14), XStringFormats.TopLeft);
            y += 22;
            Celulas(colunas.Select(c => c.Titulo).ToList(), fontCabecalho);
        }

        NovaPagina();
        if (linhas.Count == 0)
        {
            y += 8;
            gfx.DrawString("Não existe nenhuma peça para produção nesta campanha.", fontTexto, XBrushes.Black,
                new XRect(margem, y, page.Width.Point - margem * 2, 14), XStringFormats.TopLeft);
        }

        foreach (var l in linhas)
        {
            if (y + alturaLinha > page.Height.Point - margem) NovaPagina();
            Celulas(new[]
            {
                l.Suporte, l.Midia == "digital" ? "Digital" : l.Midia == "impressa" ? "Impressa" : null, l.Formato,
                l.Dimensao, l.Resolucao, l.Duracao.HasValue ? $"{l.Duracao} seg" : null, l.ExtensoesAceitas,
                l.Quantidade.ToString()
            }, fontCelula);
        }

        if (linhas.Count > 0)
        {
            if (y + alturaLinha > page.Height.Point - margem) NovaPagina();
            gfx.DrawString($"Total de peças: {linhas.Sum(l => l.Quantidade)}", fontCabecalho, XBrushes.Black,
                new XRect(margem, y + 4, page.Width.Point - margem * 2, alturaLinha), XStringFormats.TopRight);
        }

        gfx.Dispose();
        using var stream = new System.IO.MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static string Cortar(XGraphics gfx, string texto, XFont fonte, double largura)
    {
        if (gfx.MeasureString(texto, fonte).Width <= largura) return texto;
        while (texto.Length > 1 && gfx.MeasureString(texto + "…", fonte).Width > largura)
            texto = texto[..^1];
        return texto + "…";
    }
}
