using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;

namespace Veiculando.WhiteLabel.Api.Services;

public sealed class SnapsSupportProxyTiming
{
    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(180);
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed class SnapsSupportProxyService(IHttpClientFactory clients, IConfiguration configuration, SnapsSupportProxyTiming timing)
{
    public const string ClientName = "SnapsSupportProxy";
    public const long MaxRequestBytes = 6 * 1024 * 1024;
    private const long MaxFileBytes = 5 * 1024 * 1024;
    private const string ApiKey = "X-API-Key";
    private const string SessionHeader = "X-Support-Session";
    private static readonly byte[] Heartbeat = ": heartbeat\n\n"u8.ToArray();

    public async Task ForwardAsync(HttpContext context, string route)
    {
        var incoming = context.Request;
        context.Response.Headers.CacheControl = "no-store";
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var rawPath = string.IsNullOrEmpty(rawTarget) ? incoming.Path.ToUriComponent() : rawTarget.Split('?', 2)[0];
        if (!SnapsSupportProxyRoutes.Allows(incoming.Method, route, rawPath, incoming.Query, out var chatSession))
        {
            context.Response.StatusCode = 404;
            return;
        }

        var key = configuration["GOVERNANCE_API_KEY"];
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)
            || !Uri.TryCreate(configuration["GOVERNANCE_API_URL"], UriKind.Absolute, out var upstream)
            || upstream.Scheme != Uri.UriSchemeHttps || upstream.UserInfo.Length != 0
            || upstream.AbsolutePath != "/" || upstream.Query.Length != 0 || upstream.Fragment.Length != 0)
        {
            await Error(context, 503);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(timing.Deadline);
        var token = deadline.Token;
        try
        {
            if (incoming.ContentLength > MaxRequestBytes)
            {
                await Error(context, 413);
                return;
            }

            using var request = new HttpRequestMessage(new HttpMethod(incoming.Method),
                new Uri(upstream, route + incoming.QueryString.Value));
            request.Headers.Add(ApiKey, key);
            request.Headers.TryAddWithoutValidation("Accept", incoming.Headers.Accept.ToArray());
            if (chatSession && incoming.Headers.TryGetValue(SessionHeader, out var session))
            {
                if (session.Count != 1 || string.IsNullOrWhiteSpace(session[0]) || session[0].Length > 1024
                    || session[0].Any(c => char.IsControl(c) || c == ','))
                {
                    await Error(context, 400);
                    return;
                }
                request.Headers.TryAddWithoutValidation(SessionHeader, session[0]);
            }

            incoming.Body = new LimitedBody(incoming.Body, MaxRequestBytes);
            if (route.EndsWith("/support/upload", StringComparison.Ordinal))
                request.Content = await ReadUpload(incoming, token);
            else if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            {
                request.Content = new StreamContent(incoming.Body);
                if (incoming.ContentType != null)
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", incoming.ContentType);
            }

            using var client = clients.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // 401/403 do Snaps dizem respeito à credencial de servidor.
                await Error(context, status is >= 400 and <= 599 and not (401 or 403) ? status : 502);
                return;
            }

            await using var body = await response.Content.ReadAsStreamAsync(token);
            var media = response.Content.Headers.ContentType?.MediaType;
            context.Response.StatusCode = (int)response.StatusCode;
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            if (string.Equals(media, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.ContentType = "text/event-stream; charset=utf-8";
                context.Response.Headers.CacheControl = "no-store, no-transform";
                context.Response.Headers["X-Accel-Buffering"] = "no";
                await CopySse(body, context.Response.Body, timing.Heartbeat, token);
            }
            else
            {
                context.Response.ContentType = media == "application/json" ? "application/json" : "application/octet-stream";
                await body.CopyToAsync(context.Response.Body, token);
            }
        }
        catch (OperationCanceledException)
        {
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
                context.Abort();
            else
                await Error(context, 504);
        }
        catch (BadHttpRequestException ex)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await Error(context, ex.StatusCode == 413 ? 413 : 400);
        }
        catch (HttpRequestException ex) when (ex.InnerException is BadHttpRequestException { StatusCode: 413 })
        {
            await Error(context, 413);
        }
        catch (InvalidDataException)
        {
            if (context.Response.HasStarted)
                context.Abort();
            else
                await Error(context, 400);
        }
        catch (Exception)
        {
            // Mensagens do transporte podem conter dados da requisição.
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)
                context.Abort();
            else
                await Error(context, 502);
        }
    }

    private static async Task<MultipartFormDataContent> ReadUpload(HttpRequest incoming, CancellationToken token)
    {
        if (!MediaTypeHeaderValue.TryParse(incoming.ContentType, out var media)
            || !string.Equals(media.MediaType, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected multipart form.");

        var boundary = media.Parameters.FirstOrDefault(parameter =>
            string.Equals(parameter.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim('"');
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 70)
            throw new InvalidDataException("Invalid multipart boundary.");

        incoming.HttpContext.Features.Set<IFormFeature>(new FormFeature(incoming, new FormOptions
        {
            MemoryBufferThreshold = 64 * 1024,
            MultipartBodyLengthLimit = MaxRequestBytes,
            ValueLengthLimit = 8192,
            ValueCountLimit = 8,
            MultipartHeadersLengthLimit = 8192
        }));

        var form = await incoming.ReadFormAsync(token);
        if (form.Count != 0 || form.Files.Count != 1 || form.Files[0].Name != "file")
            throw new InvalidDataException("Expected a single file.");

        var file = form.Files[0];
        if (file.Length > MaxFileBytes)
            throw new BadHttpRequestException("Request too large.", 413);

        var extension = file.ContentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/webp" => ".webp",
            _ => null
        };
        if (extension == null || file.Length == 0)
            throw new InvalidDataException("Invalid image.");

        var multipart = new MultipartFormDataContent(boundary);
        var content = new StreamContent(file.OpenReadStream());
        content.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
        multipart.Add(content, "file", "upload" + extension);
        return multipart;
    }

    private static async Task CopySse(Stream source, Stream destination, TimeSpan interval, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var emptyLine = true;
        var boundary = true;
        var afterCr = false;
        using var timer = new PeriodicTimer(interval);
        var read = source.ReadAsync(buffer, token).AsTask();
        var tick = timer.WaitForNextTickAsync(token).AsTask();

        while (true)
        {
            await Task.WhenAny(read, tick);
            if (read.IsCompleted)
            {
                var count = await read;
                if (count == 0)
                    return;

                for (var i = 0; i < count; i++)
                {
                    var current = buffer[i];
                    if (afterCr && current == '\n')
                    {
                        afterCr = false;
                        continue;
                    }

                    afterCr = current == '\r';
                    if (current is (byte)'\n' or (byte)'\r')
                    {
                        boundary = emptyLine;
                        emptyLine = true;
                    }
                    else
                    {
                        boundary = false;
                        emptyLine = false;
                    }
                }

                await destination.WriteAsync(buffer.AsMemory(0, count), token);
                await destination.FlushAsync(token);
                read = source.ReadAsync(buffer, token).AsTask();
            }

            if (tick.IsCompleted)
            {
                if (!await tick)
                    return;

                // Um único escritor. O comentário só cabe entre eventos completos.
                if (boundary && !afterCr)
                {
                    await destination.WriteAsync(Heartbeat, token);
                    await destination.FlushAsync(token);
                }
                tick = timer.WaitForNextTickAsync(token).AsTask();
            }
        }
    }

    private static Task Error(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Remove("X-Accel-Buffering");
        return context.Response.WriteAsJsonAsync(new { error = "Support request failed." }, context.RequestAborted);
    }

    private sealed class LimitedBody(Stream inner, long limit) : Stream
    {
        private long _read;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var count = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - _read + 1)], token);
            _read += count;
            if (_read > limit)
                throw new BadHttpRequestException("Request too large.", 413);
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
