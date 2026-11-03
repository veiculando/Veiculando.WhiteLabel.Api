using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>
    /// Foto pronta para o FileServer, sempre em JPG. <see cref="ContentTypeOriginal"/>
    /// é o tipo do arquivo enviado; <see cref="GuardarOriginal"/> diz se ele deve ir
    /// para o histórico privado porque a versão publicada perdeu formato ou resolução.
    /// </summary>
    public sealed record FotoPecaPreparada(byte[] Jpg, string ContentTypeOriginal, bool GuardarOriginal);

    /// <summary>
    /// Valida a foto pelo conteúdo e entrega o JPG que o FileServer aceita.
    /// </summary>
    /// <remarks>
    /// <para>JPG e PNG são sempre recodificados para JPG: a foto fica pública no
    /// FileServer, e a de celular traz EXIF com GPS e aparelho. Saem sem EXIF, XMP e
    /// IPTC, com a orientação aplicada nos pixels. No PNG, a transparência vira fundo
    /// branco. Core e WhiteLabel continuam com um arquivo só por peça
    /// (<c>{Peca.Codigo}.jpg</c>).</para>
    ///
    /// <para>O lado maior fica limitado a <see cref="LadoMaximo"/>: sem isso, uma foto
    /// grande recodificada podia passar do limite do FileServer, que o original
    /// respeitava. Quando a foto é reduzida, e sempre no PNG, o original vai para o
    /// histórico privado. A recodificação comum de JPG só tira metadados, e não gera
    /// cópia.</para>
    /// </remarks>
    public static class FotoPecaImagem
    {
        /// <summary>Teto de resolução: a imagem é decodificada em memória.</summary>
        public const int PixelsMaximos = 50_000_000;

        /// <summary>Lado maior da foto publicada, em pixels.</summary>
        public const int LadoMaximo = 4096;

        private const int QualidadeJpg = 90;

        /// <returns>A foto preparada, ou <c>null</c> se o arquivo não for uma imagem aceitável.</returns>
        public static FotoPecaPreparada Preparar(byte[] original)
        {
            try
            {
                using var leitura = new MemoryStream(original, false);
                var info = Image.Identify(leitura, out var formato);
                if (info == null || (long)info.Width * info.Height > PixelsMaximos) return null;

                if (formato is JpegFormat)
                {
                    using var jpg = Image.Load<Rgb24>(original);
                    var reduzida = false;
                    jpg.Mutate(x => reduzida = Ajustar(x));
                    return new FotoPecaPreparada(SalvarJpgSemMetadados(jpg), "image/jpeg", reduzida);
                }
                if (formato is not PngFormat) return null;

                using var png = Image.Load<Rgba32>(original);
                png.Mutate(x => Ajustar(x.BackgroundColor(Color.White)));
                return new FotoPecaPreparada(SalvarJpgSemMetadados(png), "image/png", true);
            }
            catch (Exception ex) when (ex is ImageFormatException or ImageProcessingException or NotSupportedException) { return null; }
        }

        /// <returns>Se a foto foi reduzida.</returns>
        private static bool Ajustar(IImageProcessingContext imagem)
        {
            imagem.AutoOrient();
            var tamanho = imagem.GetCurrentSize();
            if (Math.Max(tamanho.Width, tamanho.Height) <= LadoMaximo) return false;
            imagem.Resize(new ResizeOptions { Mode = ResizeMode.Max, Size = new Size(LadoMaximo, LadoMaximo) });
            return true;
        }

        /// <remarks>
        /// O perfil de cor (ICC) fica: o ImageSharp 2.x não converte as cores para sRGB,
        /// e sem o perfil uma foto Display P3 (iPhone) sairia desbotada. Ele descreve
        /// cores, não traz localização.
        /// </remarks>
        private static byte[] SalvarJpgSemMetadados(Image imagem)
        {
            imagem.Metadata.ExifProfile = null;
            imagem.Metadata.IptcProfile = null;
            imagem.Metadata.XmpProfile = null;
            using var saida = new MemoryStream();
            imagem.SaveAsJpeg(saida, new JpegEncoder { Quality = QualidadeJpg });
            return saida.ToArray();
        }
    }
}
