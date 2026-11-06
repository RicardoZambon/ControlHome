using System.Threading.Channels;
using ControlHome.Connectors.DahuaHttp;
using ControlHome.Connectors.DahuaHttp.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace ControlHome.Connectors.Tests.DahuaHttp;

/// <summary>The long-lived stream's liveness (heartbeat timeout) and the reconnect back-off.</summary>
public sealed class DahuaEventStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stream_that_goes_quiet_past_the_heartbeat_timeout_fails()
    {
        var time = new FakeTimeProvider();
        var body = new PushStream();
        await using var stream = new DahuaEventStream(new HttpResponseMessage(), body, "myboundary", time, TimeSpan.FromSeconds(20), "192.0.2.10:80");
        await using var parts = stream.ReadPartsAsync(Ct).GetAsyncEnumerator(Ct);

        body.Push(DahuaFixtures.Part("Heartbeat"));
        (await parts.MoveNextAsync()).ShouldBeTrue();
        parts.Current.ShouldBe("Heartbeat");

        // Heartbeats keep it alive ...
        var next = parts.MoveNextAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(15));
        body.Push(DahuaFixtures.Part("Code=VideoMotion;action=Start;index=0"));
        (await next).ShouldBeTrue();
        parts.Current.ShouldBe("Code=VideoMotion;action=Start;index=0");

        // ... silence does not.
        var quiet = parts.MoveNextAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(21));
        var failure = await Should.ThrowAsync<DahuaHttpException>(() => quiet);
        failure.Failure.ShouldBe(DahuaFailure.Timeout);
        failure.Message.ShouldContain("went quiet");
    }

    [Fact]
    public async Task The_end_of_the_body_ends_the_stream()
    {
        var body = new PushStream();
        await using var stream = new DahuaEventStream(new HttpResponseMessage(), body, null, TimeProvider.System, TimeSpan.FromSeconds(20), "x");
        body.Push(System.Text.Encoding.ASCII.GetBytes("--myboundary\r\nCode=AlarmLocal;action=Start;index=0\r\n"));
        body.Complete();

        var parts = new List<string>();
        await foreach (var part in stream.ReadPartsAsync(Ct))
        {
            parts.Add(part);
        }

        parts.ShouldBe(["Code=AlarmLocal;action=Start;index=0"]);
    }

    [Fact]
    public void Reconnect_delays_double_with_jitter_up_to_the_maximum()
    {
        var backoff = new ReconnectBackoff(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), 0.2, new Random(7));

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var delay = backoff.Next(attempt);
            var nominal = Math.Min(Math.Pow(2, attempt), 300);
            delay.TotalSeconds.ShouldBeInRange(nominal * 0.8, Math.Min(nominal * 1.2, 300));
        }

        backoff.Next(1000).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
        Enumerable.Range(0, 20).Select(_ => backoff.Next(3)).Distinct().Count().ShouldBeGreaterThan(1, "jittered, not lock-step");
    }

    /// <summary>A readable stream fed by the test: reads wait until bytes are pushed (or it completes).</summary>
    private sealed class PushStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Push(byte[] data) => _chunks.Writer.TryWrite(data);

        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
                {
                    return 0;
                }

                _chunks.Reader.TryRead(out var next);
                (_current, _offset) = (next ?? [], 0);
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
