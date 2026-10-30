using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Veiculando.WhiteLabel.Api.Services
{
    /// <summary>Foto pronta para o FileServer, sempre em JPG.</summary>
    public sealed record FotoPecaPreparada(byte[] Jpg, bool Convertida);

    /// <summary>
    /// Valida a foto pelo conteúdo e entrega o JPG que o FileServer aceita.
    /// </summary>
    /// <remarks>
    /// JPG segue com os bytes originais. PNG vira JPG, com fundo branco onde houver
    /// transparência, para que Core e WhiteLabel continuem com um arquivo só por peça
    /// (<c>{Peca.Codigo}.jpg</c>).
    /// </remarks>
    public static class FotoPecaImagem
    {
        /// <summary>Teto de resolução: a imagem é decodificada em memória.</summary>
        public const int PixelsMaximos = 50_000_000;

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
                    return new FotoPecaPreparada(original, false);
                }
                if (formato is not PngFormat) return null;

                using var png = Image.Load<Rgba32>(original);
                png.Mutate(x => x.AutoOrient().BackgroundColor(Color.White));
                using var saida = new MemoryStream();
                png.SaveAsJpeg(saida, new JpegEncoder { Quality = QualidadeJpg });
                return new FotoPecaPreparada(saida.ToArray(), true);
            }
            catch (Exception ex) when (ex is ImageFormatException or ImageProcessingException or NotSupportedException) { return null; }
        }
    }
}
