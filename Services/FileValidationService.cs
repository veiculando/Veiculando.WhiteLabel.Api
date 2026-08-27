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
    }
}
