using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace Veiculando.WhiteLabel.Api.Services
{
    public class FileValidationService : IFileValidationService
    {
        private static readonly Dictionary<string, byte[]> AllowedMagicBytes = new Dictionary<string, byte[]>
        {
            { "image/jpeg", new byte[] { 0xFF, 0xD8, 0xFF } },
            { "image/png",  new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A } },
            { "application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 } }
        };

        public bool IsValidFile(IFormFile file, long maxSizeBytes, out string errorMessage)
        {
            if (file == null || file.Length == 0)
            {
                errorMessage = "Arquivo inválido ou vazio.";
                return false;
            }

            if (file.Length > maxSizeBytes)
            {
                errorMessage = $"Tamanho do arquivo excede o limite máximo permitido ({maxSizeBytes / (1024 * 1024)}MB).";
                return false;
            }

            // Reabre o stream a cada chamada: OpenReadStream() sempre inicia em 0,
            // então não há risco de leitura parcial aqui. O stream criado neste
            // using é independente do que o caller vai usar para gravar o arquivo;
            // IFormFile.OpenReadStream() pode ser chamado múltiplas vezes.
            using var stream = file.OpenReadStream();
            var header = new byte[8];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            // Nota: o stream deste using é descartado ao sair do bloco.
            // O caller deve chamar file.OpenReadStream() novamente para obter
            // um stream posicionado em 0 para gravação no storage.

            foreach (var kvp in AllowedMagicBytes)
            {
                var magic = kvp.Value;
                if (read >= magic.Length && string.Equals(file.ContentType, kvp.Key, StringComparison.OrdinalIgnoreCase)
                    && header.Take(magic.Length).SequenceEqual(magic))
                {
                    errorMessage = null;
                    return true;
                }
            }

            errorMessage = "Tipo de arquivo não permitido. Somente JPG, PNG e PDF são aceitos.";
            return false;
        }

        public string SanitizeFileName(string originalFileName)
        {
            var safeName = Path.GetFileName(originalFileName);
            return $"{Guid.NewGuid():N}{Path.GetExtension(safeName).ToLowerInvariant()}";
        }

        /// <summary>Bytes de cabeçalho que <see cref="DetectarImagem"/> precisa ler.</summary>
        public const int CabecalhoImagem = 12;

        /// <summary>
        /// Tipo de imagem raster pelo conteúdo: JPEG, PNG ou WebP. Null para
        /// qualquer outra coisa.
        /// </summary>
        /// <remarks>
        /// <para>Usado pelo CMS (VEI-RD-19d), que decide o tipo só pelos bytes: o
        /// Content-Type e a extensão vêm do cliente e não valem nada. Um
        /// <c>logo.png</c> que é PDF cai em null.</para>
        ///
        /// <para>Por que não entrou em <see cref="IsValidFile"/>: aquele método
        /// atende os uploads que já existem (foto de peça, documentos), e pôr WebP
        /// na lista dele faria esses fluxos aceitarem um formato que ninguém pediu
        /// para eles.</para>
        ///
        /// <para>WebP é um contêiner RIFF: <c>RIFF</c> nos bytes 0–3, o tamanho nos
        /// bytes 4–7 e <c>WEBP</c> nos bytes 8–11. Só <c>RIFF</c> não basta, porque
        /// WAV e AVI começam igual.</para>
        /// </remarks>
        public static (string ContentType, string Extensao)? DetectarImagem(ReadOnlySpan<byte> cabecalho)
        {
            if (cabecalho.Length >= 3 && cabecalho[..3].SequenceEqual(AllowedMagicBytes["image/jpeg"]))
                return ("image/jpeg", "jpg");

            if (cabecalho.Length >= 8 && cabecalho[..8].SequenceEqual(AllowedMagicBytes["image/png"]))
                return ("image/png", "png");

            if (cabecalho.Length >= 12
                && cabecalho[..4].SequenceEqual("RIFF"u8)
                && cabecalho[8..12].SequenceEqual("WEBP"u8))
                return ("image/webp", "webp");

            return null;
        }
    }
}
