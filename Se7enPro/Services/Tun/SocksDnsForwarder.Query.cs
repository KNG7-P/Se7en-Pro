using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Se7enPro.Services;

internal sealed partial class SocksDnsForwarder
{
        internal List<DnsServerEntry> OrderRelayTargets(IReadOnlyList<DnsServerEntry> targets, long nowTicks)
        {
            var preferredKey = _preferredTargetKey;
            var healthy = new List<DnsServerEntry>(targets.Count);
            var cooledDown = new List<(DnsServerEntry Entry, long UntilTicks)>();

            foreach (var entry in targets)
            {
                if (_targetCooldownUntilTicks.TryGetValue(entry.TargetHostPort, out var untilTicks) && untilTicks > nowTicks)
                {
                    cooledDown.Add((entry, untilTicks));
                }
                else
                {
                    healthy.Add(entry);
                }
            }

            if (!string.IsNullOrEmpty(preferredKey) && healthy.Count > 1)
            {
                var idx = healthy.FindIndex(t => string.Equals(t.TargetHostPort, preferredKey, StringComparison.OrdinalIgnoreCase));
                if (idx > 0)
                {
                    var best = healthy[idx];
                    healthy.RemoveAt(idx);
                    healthy.Insert(0, best);
                }
            }

            if (cooledDown.Count > 0)
            {
                cooledDown.Sort(static (a, b) => a.UntilTicks.CompareTo(b.UntilTicks));
                foreach (var item in cooledDown)
                {
                    healthy.Add(item.Entry);
                }
            }

            return healthy;
        }

        internal void MarkTargetResult(DnsServerEntry target, bool succeeded, long? nowTicks = null)
        {
            var ticks = nowTicks ?? DateTime.UtcNow.Ticks;
            if (succeeded)
            {
                _targetFailureStreaks.TryRemove(target.TargetHostPort, out _);
                _targetCooldownUntilTicks.TryRemove(target.TargetHostPort, out _);
                _preferredTargetKey = target.TargetHostPort;
            }
            else
            {
                var streak = _targetFailureStreaks.AddOrUpdate(target.TargetHostPort, 1, static (_, c) => c + 1);
                if (streak >= FailuresBeforeCooldown)
                {
                    _targetCooldownUntilTicks[target.TargetHostPort] = ticks + TimeSpan.FromSeconds(CooldownSeconds).Ticks;
                    if (string.Equals(_preferredTargetKey, target.TargetHostPort, StringComparison.OrdinalIgnoreCase))
                    {
                        _preferredTargetKey = null;
                    }
                }
            }
        }

        private async Task<byte[]?> QueryUpstreamCoalescedAsync(byte[] query, string? cacheKey, CancellationToken ct)
        {
            var stopToken = _cts?.Token ?? CancellationToken.None;
            if (cacheKey is null || query.Length < 2 || _stopped)
            {
                return await QueryUpstreamAsync(query, ct);
            }

            var queryCopy = (byte[])query.Clone();
            var sharedTask = _inFlightQueries.GetOrAdd(
                cacheKey,
                _ => RunSharedUpstreamQueryAsync(queryCopy, cacheKey, stopToken));

            byte[]? result;
            try
            {
                result = await sharedTask.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }

            if (result is null || result.Length < 2) return result;
            var cloned = (byte[])result.Clone();
            cloned[0] = query[0];
            cloned[1] = query[1];
            return cloned;
        }

        private async Task<byte[]?> RunSharedUpstreamQueryAsync(byte[] query, string cacheKey, CancellationToken stopToken)
        {
            using var sharedCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            var sharedTimeoutMs = Math.Max(15000.0, _queryTimeout.TotalMilliseconds * 2);
            sharedCts.CancelAfter(TimeSpan.FromMilliseconds(sharedTimeoutMs));
            try
            {
                return await QueryUpstreamAsync(query, sharedCts.Token);
            }
            finally
            {
                _inFlightQueries.TryRemove(cacheKey, out _);
            }
        }

        private async Task<byte[]?> QueryUpstreamAsync(byte[] query, CancellationToken outerCt)
        {
            var snapshot = _relayTargets;
            if (snapshot.Count == 0) return null;

            var name = ParseQuestion(query)?.Name ?? "";
            byte[]? sinkholed = null;
            var ordered = OrderRelayTargets(snapshot, DateTime.UtcNow.Ticks);

            for (var i = 0; i < ordered.Count; i++)
            {
                var target = ordered[i];
                string detail;

                var hasMoreTargets = i + 1 < ordered.Count;
                var perTargetBudgetMs = hasMoreTargets
                    ? (target.Transport == DnsTransport.Udp
                        ? Math.Min(_queryTimeout.TotalMilliseconds, 1500)
                        : Math.Min(_queryTimeout.TotalMilliseconds, 4500))
                    : _queryTimeout.TotalMilliseconds;

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                linked.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(500, perTargetBudgetMs)));
                var ct = linked.Token;

                try
                {
                    var answer = target.Transport switch
                    {
                        DnsTransport.Doh => await QueryDohAsync(target, query, ct),
                        DnsTransport.Dot => await QueryDotAsync(target, query, ct),
                        _ => await QueryOneAsync(target, query, ct)
                    };
                    if (answer is null)
                    {
                        MarkTargetResult(target, succeeded: false);
                        detail = "did not answer";
                    }
                    else if (!IsSinkholedAnswer(answer))
                    {
                        MarkTargetResult(target, succeeded: true);
                        return answer;
                    }
                    else
                    {
                        sinkholed ??= answer;
                        detail = $"null-routed '{name}' to {DescribeAnswerAddresses(answer)}";
                    }
                }
                catch (OperationCanceledException)
                {
                    if (outerCt.IsCancellationRequested) return sinkholed;
                    MarkTargetResult(target, succeeded: false);
                    if (target.Transport == DnsTransport.Doh) DisposeDohClient();
                    detail = "timed out";
                }
                catch (Exception ex)
                {
                    MarkTargetResult(target, succeeded: false);
                    if (target.Transport == DnsTransport.Doh) DisposeDohClient();
                    detail = "failed - " + ex.GetType().Name;
                }

                Diag?.Invoke($"dns: resolver {i + 1}/{ordered.Count} "
                             + $"({target.TargetHostPort}) {detail}"
                             + (hasMoreTargets ? "; trying the next one" : ""));
            }

            return sinkholed;
        }

        private async Task<byte[]?> QueryOneAsync(DnsServerEntry target, byte[] query, CancellationToken ct)
        {
            await using var stream = await OpenSocksTunnelAsync(target, ct);
            if (stream is null) return null;

            var framed = new byte[2 + query.Length];
            framed[0] = (byte)(query.Length >> 8);
            framed[1] = (byte)(query.Length & 0xFF);
            Buffer.BlockCopy(query, 0, framed, 2, query.Length);
            await stream.WriteAsync(framed, ct);

            var len = new byte[2];
            await ReadExactlyAsync(stream, len, ct);
            var answerLen = (len[0] << 8) | len[1];

            if (answerLen is <= 0 or > 65535) return null;

            var answer = new byte[answerLen];
            await ReadExactlyAsync(stream, answer, ct);

            if (query.Length >= 2 && answerLen >= 3)
            {
                if (answer[0] != query[0] || answer[1] != query[1]) return null;
                if ((answer[2] & 0x80) == 0) return null;
            }

            return answer;
        }

        private Task<NetworkStream?> OpenSocksTunnelAsync(DnsServerEntry target, CancellationToken ct)
            => OpenSocksTunnelForEndpointAsync(target.Address, target.Host, target.Port, ct);

        private async Task<NetworkStream?> OpenSocksTunnelForEndpointAsync(
            IPAddress? address, string hostName, int port, CancellationToken ct)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                var connectTask = socket.ConnectAsync(IPAddress.Loopback, _socksPort, ct).AsTask();
                var finished = await Task.WhenAny(connectTask, Task.Delay(_connectTimeout, ct));
                if (finished != connectTask)
                {
                    socket.Dispose();
                    return null;
                }
                await connectTask;
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            var stream = new NetworkStream(socket, ownsSocket: true);
            var succeeded = false;
            try
            {
                var useAuth = _socksUser.Length > 0;
                await stream.WriteAsync(
                    useAuth ? new byte[] { 0x05, 0x02, 0x00, 0x02 } : new byte[] { 0x05, 0x01, 0x00 }, ct);
                var greeting = new byte[2];
                await ReadExactlyAsync(stream, greeting, ct);
                if (greeting[0] != 0x05) return null;

                if (greeting[1] == 0x02)
                {
                    var user = Encoding.UTF8.GetBytes(_socksUser);
                    var pass = Encoding.UTF8.GetBytes(_socksPass);
                    if (user.Length is 0 or > 255 || pass.Length > 255) return null;
                    var auth = new byte[3 + user.Length + pass.Length];
                    auth[0] = 0x01;
                    auth[1] = (byte)user.Length;
                    Buffer.BlockCopy(user, 0, auth, 2, user.Length);
                    auth[2 + user.Length] = (byte)pass.Length;
                    Buffer.BlockCopy(pass, 0, auth, 3 + user.Length, pass.Length);
                    await stream.WriteAsync(auth, ct);

                    var authResult = new byte[2];
                    await ReadExactlyAsync(stream, authResult, ct);
                    if (authResult[1] != 0x00) return null;
                }
                else if (greeting[1] != 0x00)
                {
                    return null;
                }

                byte[] connectReq;
                if (address is { } literal)
                {
                    var addr = literal.GetAddressBytes();
                    connectReq = new byte[4 + addr.Length + 2];
                    connectReq[0] = 0x05;
                    connectReq[1] = 0x01;
                    connectReq[3] = (byte)(addr.Length == 16 ? 0x04 : 0x01);
                    Buffer.BlockCopy(addr, 0, connectReq, 4, addr.Length);
                    connectReq[4 + addr.Length] = (byte)(port >> 8);
                    connectReq[4 + addr.Length + 1] = (byte)(port & 0xFF);
                }
                else
                {
                    var host = Encoding.ASCII.GetBytes(hostName);
                    connectReq = new byte[7 + host.Length];
                    connectReq[0] = 0x05;
                    connectReq[1] = 0x01;
                    connectReq[3] = 0x03;
                    connectReq[4] = (byte)host.Length;
                    Buffer.BlockCopy(host, 0, connectReq, 5, host.Length);
                    connectReq[5 + host.Length] = (byte)(port >> 8);
                    connectReq[6 + host.Length] = (byte)(port & 0xFF);
                }
                await stream.WriteAsync(connectReq, ct);

                var header = new byte[4];
                await ReadExactlyAsync(stream, header, ct);
                if (header[1] != 0x00) return null;

                var extra = header[3] switch
                {
                    0x01 => 4 + 2,
                    0x03 => -1,
                    0x04 => 16 + 2,
                    _ => 0,
                };
                if (extra < 0)
                {
                    var lenByte = new byte[1];
                    await ReadExactlyAsync(stream, lenByte, ct);
                    extra = lenByte[0] + 2;
                }
                if (extra > 0)
                {
                    var rest = new byte[extra];
                    await ReadExactlyAsync(stream, rest, ct);
                }

                succeeded = true;
                return stream;
            }
            finally
            {
                if (!succeeded)
                {
                    try { stream.Dispose(); } catch { }
                }
            }
        }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (n <= 0) throw new IOException("DNS relay closed the connection");
            offset += n;
        }
    }

    internal async Task<bool> ProbeAsync(CancellationToken outerCt)
    {
        if (_relayTargets.Count == 0)
        {
            Diag?.Invoke("dns: self-test skipped — no relay targets configured");
            return false;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
                linked.CancelAfter(TimeSpan.FromSeconds(attempt == 0 ? 9 : 12));
                var answer = await QueryUpstreamAsync(BuildProbeQuery(), linked.Token);
                if (answer is not null) return true;
            }
            catch (OperationCanceledException)
            {
                if (outerCt.IsCancellationRequested) return false;
            }
            catch (Exception ex)
            {
                Diag?.Invoke($"dns: self-test attempt {attempt + 1} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        Diag?.Invoke($"dns: self-test got no answer from {_relayTargets.Count} relay target(s) "
                     + $"through SOCKS 127.0.0.1:{_socksPort}");
        return false;
    }

    private static byte[] BuildProbeQuery()
    {
        const string name = "example.com";
        var query = new byte[12 + name.Length + 2 + 4];
        query[0] = 0x53;
        query[1] = 0x45;
        query[2] = 0x01;
        query[5] = 0x01;

        var offset = 12;
        foreach (var label in name.Split('.'))
        {
            query[offset++] = (byte)label.Length;
            foreach (var ch in label) query[offset++] = (byte)ch;
        }
        query[offset++] = 0;
        query[offset++] = 0x00;
        query[offset++] = 0x01;
        query[offset++] = 0x00;
        query[offset++] = 0x01;
        return query;
    }
}
