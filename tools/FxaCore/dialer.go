package main

import (
	"context"
	"crypto/tls"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/netip"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

const (
	dialStagger       = 300 * time.Millisecond
	failedAddrFor     = 10 * time.Minute
	dohQueryTimeout   = 3 * time.Second
	tlsRecordHeaderSz = 5
)

var (
	knownMozillaIPs = map[string][]netip.Addr{
		"api.accounts.firefox.com": {
			netip.MustParseAddr("151.101.1.91"),
			netip.MustParseAddr("151.101.65.91"),
			netip.MustParseAddr("151.101.129.91"),
			netip.MustParseAddr("151.101.193.91"),
		},
		"accounts.firefox.com": {
			netip.MustParseAddr("151.101.1.91"),
			netip.MustParseAddr("151.101.65.91"),
			netip.MustParseAddr("151.101.129.91"),
			netip.MustParseAddr("151.101.193.91"),
		},
		"vpn.mozilla.org": {
			netip.MustParseAddr("151.101.1.91"),
			netip.MustParseAddr("151.101.65.91"),
			netip.MustParseAddr("151.101.129.91"),
			netip.MustParseAddr("151.101.193.91"),
		},
		"firefox.settings.services.mozilla.com": {
			netip.MustParseAddr("34.107.243.141"),
			netip.MustParseAddr("35.244.209.117"),
		},
	}

	iranBlockV6Prefix = netip.MustParsePrefix("2001:4188:2:600:10:10:34::/112")
	failedAddrs       sync.Map
)

func isBlockPageOrBogon(a netip.Addr) bool {
	a = a.Unmap()
	if !a.IsValid() || a.IsUnspecified() || a.IsLoopback() || a.IsLinkLocalUnicast() || a.IsLinkLocalMulticast() || a.IsMulticast() || a.IsPrivate() {
		return true
	}
	if a.Is4() {
		b := a.As4()
		if b[0] == 0 || b[0] >= 224 {
			return true
		}
	}
	if a.Is6() && iranBlockV6Prefix.Contains(a) {
		return true
	}
	return false
}

func markAddrFailed(a netip.Addr) {
	a = a.Unmap()
	if a.IsValid() {
		failedAddrs.Store(a, time.Now().Add(failedAddrFor))
	}
}

func clearAddrFailure(a netip.Addr) {
	failedAddrs.Delete(a.Unmap())
}

func orderByHealth(addrs []netip.Addr) []netip.Addr {
	now := time.Now()
	healthy := make([]netip.Addr, 0, len(addrs))
	demoted := make([]netip.Addr, 0)
	seen := make(map[netip.Addr]struct{}, len(addrs))

	for _, raw := range addrs {
		a := raw.Unmap()
		if !a.IsValid() {
			continue
		}
		if _, dup := seen[a]; dup {
			continue
		}
		seen[a] = struct{}{}

		if v, ok := failedAddrs.Load(a); ok {
			if until := v.(time.Time); now.Before(until) {
				demoted = append(demoted, a)
				continue
			}
			failedAddrs.Delete(a)
		}
		healthy = append(healthy, a)
	}
	return append(healthy, demoted...)
}

func resolveHostAddrs(ctx context.Context, host string) ([]netip.Addr, error) {
	if addr, err := netip.ParseAddr(strings.TrimSpace(host)); err == nil {
		return []netip.Addr{addr.Unmap()}, nil
	}

	if pinned, ok := knownMozillaIPs[strings.ToLower(strings.TrimSpace(host))]; ok {
		return orderByHealth(pinned), nil
	}

	dohCh := make(chan []netip.Addr, 1)
	sysCh := make(chan []netip.Addr, 1)

	go func() {
		dohCh <- queryDohParallel(ctx, host)
	}()
	go func() {
		sysCh <- querySystemFiltered(ctx, host)
	}()

	if doh := <-dohCh; len(doh) > 0 {
		return orderByHealth(doh), nil
	}
	if sys := <-sysCh; len(sys) > 0 {
		return orderByHealth(sys), nil
	}
	return nil, fmt.Errorf("no clean DNS addresses for %s", host)
}

func querySystemFiltered(ctx context.Context, host string) []netip.Addr {
	qctx, cancel := context.WithTimeout(ctx, dohQueryTimeout)
	defer cancel()
	addrs, err := net.DefaultResolver.LookupNetIP(qctx, "ip", host)
	if err != nil {
		return nil
	}
	out := make([]netip.Addr, 0, len(addrs))
	for _, a := range addrs {
		a = a.Unmap()
		if !isBlockPageOrBogon(a) {
			out = append(out, a)
		}
	}
	return out
}

func queryDohParallel(ctx context.Context, host string) []netip.Addr {
	qctx, cancel := context.WithTimeout(ctx, dohQueryTimeout)
	defer cancel()

	escaped := url.QueryEscape(host)
	endpoints := []string{
		"https://1.1.1.1/dns-query?name=" + escaped + "&type=A",
		"https://1.0.0.1/dns-query?name=" + escaped + "&type=A",
		"https://8.8.8.8/resolve?name=" + escaped + "&type=A",
		"https://8.8.4.4/resolve?name=" + escaped + "&type=A",
	}

	tr := &http.Transport{
		Proxy:           nil,
		TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12},
	}
	defer tr.CloseIdleConnections()
	client := &http.Client{Transport: tr, Timeout: dohQueryTimeout}

	type respMsg struct {
		addrs []netip.Addr
	}
	ch := make(chan respMsg, len(endpoints))
	for _, ep := range endpoints {
		go func(endpoint string) {
			req, err := http.NewRequestWithContext(qctx, http.MethodGet, endpoint, nil)
			if err != nil {
				ch <- respMsg{}
				return
			}
			req.Header.Set("Accept", "application/dns-json")
			resp, err := client.Do(req)
			if err != nil {
				ch <- respMsg{}
				return
			}
			defer resp.Body.Close()
			if resp.StatusCode != http.StatusOK {
				ch <- respMsg{}
				return
			}
			var payload struct {
				Answer []struct {
					Data string `json:"data"`
				} `json:"Answer"`
			}
			if err := json.NewDecoder(resp.Body).Decode(&payload); err != nil {
				ch <- respMsg{}
				return
			}
			out := make([]netip.Addr, 0, len(payload.Answer))
			for _, ans := range payload.Answer {
				if addr, err := netip.ParseAddr(strings.TrimSpace(ans.Data)); err == nil && !isBlockPageOrBogon(addr) {
					out = append(out, addr.Unmap())
				}
			}
			ch <- respMsg{addrs: out}
		}(ep)
	}

	for range endpoints {
		msg := <-ch
		if len(msg.addrs) > 0 {
			cancel()
			return msg.addrs
		}
	}
	return nil
}

func dialDirectBypass(ctx context.Context, network, address string) (net.Conn, error) {
	host, port, err := net.SplitHostPort(address)
	if err != nil {
		return nil, err
	}

	if ip, err := netip.ParseAddr(host); err == nil && ip.Unmap().IsLoopback() {
		return (&net.Dialer{Timeout: 10 * time.Second}).DialContext(ctx, network, address)
	}
	if strings.EqualFold(host, "localhost") {
		return (&net.Dialer{Timeout: 10 * time.Second}).DialContext(ctx, network, address)
	}

	addrs, err := resolveHostAddrs(ctx, host)
	if err != nil || len(addrs) == 0 {
		return nil, err
	}

	conn, err := dialRace(ctx, (&net.Dialer{Timeout: 15 * time.Second}).DialContext, network, port, addrs, dialStagger)
	if err != nil {
		return nil, err
	}
	if tc, ok := conn.(*net.TCPConn); ok {
		_ = tc.SetNoDelay(true)
	}
	return &helloSplitConn{Conn: conn}, nil
}

func dialRace(
	ctx context.Context,
	dial func(context.Context, string, string) (net.Conn, error),
	network, port string,
	addrs []netip.Addr,
	stagger time.Duration,
) (net.Conn, error) {
	if len(addrs) == 0 {
		return nil, errors.New("no addresses to connect to")
	}

	rctx, cancel := context.WithCancel(ctx)
	type attemptResult struct {
		conn net.Conn
		err  error
		addr netip.Addr
	}
	results := make(chan attemptResult, len(addrs))
	started, running := 0, 0

	startNext := func() {
		a := addrs[started]
		started++
		running++
		go func(target netip.Addr) {
			c, err := dial(rctx, network, net.JoinHostPort(target.String(), port))
			results <- attemptResult{conn: c, err: err, addr: target}
		}(a)
	}

	drainLosers := func(left int) {
		cancel()
		go func(n int) {
			for ; n > 0; n-- {
				if r := <-results; r.conn != nil {
					_ = r.conn.Close()
				}
			}
		}(left)
	}

	startNext()
	timer := time.NewTimer(stagger)
	defer timer.Stop()

	var lastErr error
	for running > 0 || started < len(addrs) {
		if started < len(addrs) {
			select {
			case r := <-results:
				running--
				if r.err == nil {
					clearAddrFailure(r.addr)
					drainLosers(running)
					return r.conn, nil
				}
				lastErr = r.err
				if ctx.Err() == nil {
					markAddrFailed(r.addr)
				}
				if running == 0 && started < len(addrs) {
					startNext()
					if !timer.Stop() {
						select {
						case <-timer.C:
						default:
						}
					}
					timer.Reset(stagger)
				}
			case <-timer.C:
				if started < len(addrs) {
					startNext()
					timer.Reset(stagger)
				}
			case <-ctx.Done():
				drainLosers(running)
				return nil, ctx.Err()
			}
		} else {
			select {
			case r := <-results:
				running--
				if r.err == nil {
					clearAddrFailure(r.addr)
					drainLosers(running)
					return r.conn, nil
				}
				lastErr = r.err
				if ctx.Err() == nil {
					markAddrFailed(r.addr)
				}
			case <-ctx.Done():
				drainLosers(running)
				return nil, ctx.Err()
			}
		}
	}

	if lastErr == nil {
		lastErr = errors.New("all connection attempts failed")
	}
	return nil, lastErr
}

type helloSplitConn struct {
	net.Conn
	done atomic.Bool
}

func (c *helloSplitConn) Write(p []byte) (int, error) {
	if c.done.CompareAndSwap(false, true) && isTLSClientHello(p) && len(p) > 1 {
		for _, seg := range buildHelloSplitSegments(p) {
			written := 0
			for written < len(seg) {
				n, err := c.Conn.Write(seg[written:])
				if err != nil {
					return written, err
				}
				written += n
			}
		}
		return len(p), nil
	}
	return c.Conn.Write(p)
}

func isTLSClientHello(p []byte) bool {
	return len(p) > tlsRecordHeaderSz && p[0] == 0x16 && p[1] == 0x03 && p[5] == 0x01
}

func findSNIServerNameOffset(p []byte) (int, bool) {
	if !isTLSClientHello(p) {
		return 0, false
	}
	at := 5 + 4 + 2 + 32
	skip := func(lenBytes int) bool {
		if at+lenBytes > len(p) {
			return false
		}
		n := 0
		for _, b := range p[at : at+lenBytes] {
			n = (n << 8) | int(b)
		}
		at += lenBytes + n
		return at <= len(p)
	}
	if !skip(1) || !skip(2) || !skip(1) || at+2 > len(p) {
		return 0, false
	}
	extEnd := at + 2 + int(binary.BigEndian.Uint16(p[at:]))
	at += 2
	if extEnd > len(p) {
		extEnd = len(p)
	}
	for at+4 <= extEnd {
		kind := binary.BigEndian.Uint16(p[at:])
		size := int(binary.BigEndian.Uint16(p[at+2:]))
		body := at + 4
		if body+size > extEnd {
			return 0, false
		}
		if kind == 0 {
			if size < 6 || p[body+2] != 0 {
				return 0, false
			}
			nameLen := int(binary.BigEndian.Uint16(p[body+3:]))
			if nameLen == 0 || body+5+nameLen > extEnd {
				return 0, false
			}
			return body + 5, true
		}
		at = body + size
	}
	return 0, false
}

func buildHelloSplitSegments(p []byte) [][]byte {
	if start, ok := findSNIServerNameOffset(p); ok && start > 0 && start+1 < len(p) {
		return [][]byte{p[:start], p[start : start+1], p[start+1:]}
	}
	return [][]byte{p[:1], p[1:]}
}
