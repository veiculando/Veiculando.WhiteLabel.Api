using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Veiculando.WhiteLabel.Api.Services;
using Xunit;
using static Veiculando.WhiteLabel.Api.Tests.SnapsSupportProxyTests;

namespace Veiculando.WhiteLabel.Api.Tests;

[Collection("Snaps support")]
public sealed class SnapsSupportProxyStreamingTests
{
    private const string Messages = Prefix + Chat + "/messages";

    [Fact]
    public async Task Heartbeat_waits_for_the_end_of_a_fragmented_event()
    {
        using var fixture = new ProxyFixture
        {
            Timing = new SnapsSupportProxyTiming { Heartbeat = TimeSpan.FromMilliseconds(40), Deadline = TimeSpan.FromSeconds(5) }
        };
        using var client = fixture.CreateProxyClient();
        using var source = UseSse(fixture);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var partial = Encoding.UTF8.GetBytes("event: token\r\ndata: {\"text\":\"ação");
        await source.Push(partial);

        using var request = Authorized(HttpMethod.Post, Messages);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType.MediaType);
        Assert.Equal("no", Assert.Single(response.Headers.GetValues("X-Accel-Buffering")));
        Assert.Contains("no-transform", response.Headers.CacheControl.ToString());
        Assert.Null(response.Content.Headers.ContentLength);

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        var first = new byte[partial.Length];
        await body.ReadExactlyAsync(first, timeout.Token);
        Assert.Equal(partial, first);
        await Task.Delay(150, timeout.Token);

        var tail = Encoding.UTF8.GetBytes("\"}\r\n\r\n");
        await source.Push(tail);
        var end = new byte[tail.Length];
        await body.ReadExactlyAsync(end, timeout.Token);
        Assert.Equal(tail, end);

        var heartbeat = new byte[13];
        await body.ReadExactlyAsync(heartbeat, timeout.Token);
        Assert.Equal(": heartbeat\n\n", Encoding.UTF8.GetString(heartbeat));
        source.Complete();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Disconnect_or_deadline_cancels_the_upstream_body(bool disconnect)
    {
        using var fixture = new ProxyFixture
        {
            Timing = new SnapsSupportProxyTiming
            {
                Heartbeat = TimeSpan.FromMilliseconds(80),
                Deadline = TimeSpan.FromSeconds(disconnect ? 30 : 2)
            }
        };
        using var source = UseSse(fixture);
        using var host = await fixture.StartKestrel();
        using var client = ProxyFixture.CreateKestrelClient(host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await source.Push("data: first\n\n"u8.ToArray());

        using var request = Authorized(HttpMethod.Post, Messages);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        var first = new byte[13];
        await body.ReadExactlyAsync(first, timeout.Token);
        Assert.Equal("data: first\n\n", Encoding.UTF8.GetString(first));

        if (disconnect)
            response.Dispose();
        await source.Cancelled.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, fixture.Upstream.Calls);
    }

    [Fact]
    public async Task Deadline_before_upstream_headers_returns_a_generic_504()
    {
        using var fixture = new ProxyFixture
        {
            Timing = new SnapsSupportProxyTiming { Deadline = TimeSpan.FromMilliseconds(150) }
        };
        using var client = fixture.CreateProxyClient();
        fixture.Upstream.Send = async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        };
        using var request = Authorized(HttpMethod.Post, Messages);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("{\"error\":\"Support request failed.\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, fixture.Upstream.Calls);
    }

    [Fact]
    public async Task Kestrel_streams_first_event_and_stays_open_for_160_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(180), new SnapsSupportProxyTiming().Deadline);
        using var fixture = new ProxyFixture();
        using var source = UseSse(fixture);
        using var host = await fixture.StartKestrel();
        using var client = ProxyFixture.CreateKestrelClient(host);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        await source.Push("data: first\n\n"u8.ToArray());
        var elapsed = Stopwatch.StartNew();

        using var request = Authorized(HttpMethod.Post, Messages);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        Assert.Equal("data: first", await reader.ReadLineAsync(timeout.Token));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
        var producer = SendLast();

        var heartbeats = 0;
        string line;
        do
        {
            line = await reader.ReadLineAsync(timeout.Token);
            if (line == ": heartbeat")
                heartbeats++;
        }
        while (line != null && line != "data: last");

        Assert.Equal("data: last", line);
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(160));
        Assert.True(heartbeats >= 10);
        await producer;
        Assert.Equal(1, fixture.Upstream.Calls);

        async Task SendLast()
        {
            await Task.Delay(TimeSpan.FromSeconds(160), timeout.Token);
            await source.Push("data: last\n\n"u8.ToArray());
            source.Complete();
        }
    }

    [Theory]
    [InlineData(5 * 1024 * 1024, "image/png", 200)]
    [InlineData(5 * 1024 * 1024 + 1, "image/png", 413)]
    [InlineData(0, "image/png", 400)]
    [InlineData(10, "application/pdf", 400)]
    public async Task Upload_enforces_file_limits_and_preserves_boundary(int bytes, string mime, int expected)
    {
        using var fixture = new ProxyFixture();
        using var client = fixture.CreateProxyClient();
        const string boundary = "synthetic-boundary";
        fixture.Upstream.Send = async (request, token) =>
        {
            Assert.Equal(boundary, Assert.Single(request.Content.Headers.ContentType.Parameters).Value.Trim('"'));
            Assert.True((await request.Content.ReadAsByteArrayAsync(token)).Length > bytes);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        };
        using var request = Authorized(HttpMethod.Post, Prefix + "/support/upload");
        using var form = new MultipartFormDataContent(boundary);
        var file = new ByteArrayContent(new byte[bytes]);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", "synthetic.png");
        request.Content = form;
        using var response = await client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(expected == 200 ? 1 : 0, fixture.Upstream.Calls);
    }

    [Fact]
    public async Task Chunked_upload_over_request_limit_never_reaches_snaps()
    {
        using var fixture = new ProxyFixture();
        using var host = await fixture.StartKestrel();
        using var client = ProxyFixture.CreateKestrelClient(host);
        using var request = Authorized(HttpMethod.Post, Prefix + "/support/upload");
        request.Headers.TransferEncodingChunked = true;
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[7 * 1024 * 1024]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "synthetic.png");
        request.Content = form;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, fixture.Upstream.Calls);
    }

    private static FragmentStream UseSse(ProxyFixture fixture)
    {
        var source = new FragmentStream();
        fixture.Upstream.Send = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(source) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        };
        return source;
    }

    private sealed class FragmentStream : Stream
    {
        private readonly Channel<byte> _bytes = Channel.CreateUnbounded<byte>();
        internal readonly TaskCompletionSource Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal async Task Push(byte[] bytes)
        {
            foreach (var value in bytes)
                await _bytes.Writer.WriteAsync(value);
        }

        internal void Complete() => _bytes.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!await _bytes.Reader.WaitToReadAsync(cancellationToken))
                    return 0;
                var value = await _bytes.Reader.ReadAsync(cancellationToken);
                buffer.Span[0] = value;
                return 1;
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
