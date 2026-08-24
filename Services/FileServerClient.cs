using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Veiculando.WhiteLabel.Api.Services
{
    public enum FileServerResultStatus
    {
        Success,
        NotFound,
        UpstreamError,
    }

    public class FileServerPdfResult
    {
        public FileServerResultStatus Status { get; }
        public byte[] Content { get; }
        public string ContentType { get; }

        private FileServerPdfResult(FileServerResultStatus status, byte[] content, string contentType)
        {
            Status = status;
            Content = content;
            ContentType = contentType;
        }

        public static FileServerPdfResult Ok(byte[] content, string contentType) =>
            new FileServerPdfResult(FileServerResultStatus.Success, content, contentType);

        public static FileServerPdfResult NotFound() =>
            new FileServerPdfResult(FileServerResultStatus.NotFound, null, null);

        public static FileServerPdfResult Error() =>
            new FileServerPdfResult(FileServerResultStatus.UpstreamError, null, null);
    }

    public interface IFileServerClient
    {
        /// <summary>
        /// Busca o PDF de um PI no FileServer interno (TP-C §2). O tenant já
        /// deve ter sido validado pelo chamador — este método nunca recebe
        /// AfiliadaId porque o FileServer legado não o conhece; a barreira é
        /// inteiramente do BFF, antes de chegar aqui.
        /// </summary>
        Task<FileServerPdfResult> GetPedidoInsercaoPdfAsync(string codigo, CancellationToken cancellationToken);
    }

    public class FileServerClient : IFileServerClient
    {
        private const long MaxContentLengthBytes = 20 * 1024 * 1024; // 20 MB — limite razoável de PDF de PI.

        private readonly HttpClient _httpClient;
        private readonly ILogger<FileServerClient> _logger;

        public FileServerClient(HttpClient httpClient, ILogger<FileServerClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<FileServerPdfResult> GetPedidoInsercaoPdfAsync(string codigo, CancellationToken cancellationToken)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

                // §2 regra 3/4/6: upstream real do FileServer, timeout explícito,
                // nunca a URL interna repassada ao navegador — só os bytes.
                using var response = await _httpClient.GetAsync(
                    $"pedido-insercao/pi-exibidora/{Uri.EscapeDataString(codigo)}",
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return FileServerPdfResult.NotFound();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("FileServer respondeu {Status} para PI {Codigo}", response.StatusCode, codigo);
                    return FileServerPdfResult.Error();
                }

                if (response.Content.Headers.ContentLength is > MaxContentLengthBytes)
                {
                    _logger.LogWarning("PDF de PI {Codigo} excede o limite de tamanho", codigo);
                    return FileServerPdfResult.Error();
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token);
                return FileServerPdfResult.Ok(bytes, "application/pdf");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timeout ao buscar PDF do PI {Codigo} no FileServer", codigo);
                return FileServerPdfResult.Error();
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Falha de rede ao buscar PDF do PI {Codigo} no FileServer", codigo);
                return FileServerPdfResult.Error();
            }
        }
    }
}
