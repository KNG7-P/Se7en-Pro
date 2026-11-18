package main

import (
	"bufio"
	"context"
	"crypto/tls"
	"encoding/base64"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"
	"sync"
	"time"

	utls "github.com/refraction-networking/utls"
	"golang.org/x/net/http2"
	"golang.org/x/net/proxy"
)

var (
	sessionCache  = utls.NewLRUClientSessionCache(64)
	transportPool sync.Map
)

func main() {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		fmt.Fprintf(os.Stderr, "listen failed: %v\n", err)
		os.Exit(1)
	}
	fmt.Printf("READY %s\n", ln.Addr().String())
	_ = os.Stdout.Sync()

	go func() {
		_, _ = io.Copy(io.Discard, os.Stdin)
		os.Exit(0)
	}()

	srv := &http.Server{
		Handler:           http.HandlerFunc(handleForward),
		ReadHeaderTimeout: 15 * time.Second,
	}
	if err := srv.Serve(ln); err != nil && err != http.ErrServerClosed {
		fmt.Fprintf(os.Stderr, "serve failed: %v\n", err)
		os.Exit(1)
	}
}

func handleForward(w http.ResponseWriter, r *http.Request) {
	targetRaw := r.Header.Get("X-Fxa-Target")
	if targetRaw == "" {
		http.Error(w, "missing X-Fxa-Target header", http.StatusBadRequest)
		return
	}
	targetURL, err := url.Parse(targetRaw)
	if err != nil || (targetURL.Scheme != "https" && targetURL.Scheme != "http") {
		http.Error(w, "invalid X-Fxa-Target", http.StatusBadRequest)
		return
	}

	proxyRaw := r.Header.Get("X-Fxa-Proxy")
	useH2 := r.Header.Get("X-Fxa-H2") == "1"

	outReq, err := http.NewRequestWithContext(r.Context(), r.Method, targetURL.String(), r.Body)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadGateway)
		return
	}
	for k, vals := range r.Header {
		lk := strings.ToLower(k)
		if lk == "host" || lk == "connection" || lk == "proxy-connection" ||
			lk == "keep-alive" || lk == "transfer-encoding" || lk == "upgrade" ||
			strings.HasPrefix(lk, "x-fxa-") {
			continue
		}
		for _, v := range vals {
			outReq.Header.Add(k, v)
		}
	}
	if outReq.Header.Get("Accept-Encoding") == "" {
		outReq.Header.Set("Accept-Encoding", "identity")
	}

	rt := getRoundTripper(proxyRaw, useH2)
	resp, err := rt.RoundTrip(outReq)
	if err != nil {
		http.Error(w, "upstream error: "+err.Error(), http.StatusBadGateway)
		return
	}
	defer resp.Body.Close()

	for k, vals := range resp.Header {
		lk := strings.ToLower(k)
		if lk == "connection" || lk == "transfer-encoding" || lk == "keep-alive" {
			continue
		}
		for _, v := range vals {
			w.Header().Add(k, v)
		}
	}
	w.WriteHeader(resp.StatusCode)
	_, _ = io.Copy(w, resp.Body)
}

func getRoundTripper(proxyRaw string, useH2 bool) http.RoundTripper {
	key := proxyRaw + "|0"
	if useH2 {
		key = proxyRaw + "|1"
	}
	if v, ok := transportPool.Load(key); ok {
		return v.(http.RoundTripper)
	}

	var rt http.RoundTripper
	if useH2 {
		rt = &http2.Transport{
			DisableCompression: true,
			DialTLSContext: func(ctx context.Context, network, addr string, _ *tls.Config) (net.Conn, error) {
				return dialFirefoxTLS(ctx, network, addr, proxyRaw, "h2", "http/1.1")
			},
		}
	} else {
		rt = &http.Transport{
			DisableCompression:  true,
			DisableKeepAlives:   false,
			MaxIdleConns:        32,
			MaxIdleConnsPerHost: 8,
			IdleConnTimeout:     90 * time.Second,
			DialTLSContext: func(ctx context.Context, network, addr string) (net.Conn, error) {
				return dialFirefoxTLS(ctx, network, addr, proxyRaw, "http/1.1")
			},
			DialContext: func(ctx context.Context, network, addr string) (net.Conn, error) {
				return dialWithOptionalProxy(ctx, network, addr, proxyRaw)
			},
		}
	}

	actual, _ := transportPool.LoadOrStore(key, rt)
	return actual.(http.RoundTripper)
}

func dialFirefoxTLS(ctx context.Context, network, address, proxyRaw string, alpn ...string) (net.Conn, error) {
	host, _, err := net.SplitHostPort(address)
	if err != nil {
		return nil, err
	}
	raw, err := dialWithOptionalProxy(ctx, network, address, proxyRaw)
	if err != nil {
		return nil, err
	}

	uconn := utls.UClient(raw, &utls.Config{
		ServerName:         host,
		ClientSessionCache: sessionCache,
		OmitEmptyPsk:       true,
	}, utls.HelloCustom)
	if err := uconn.ApplyPreset(firefoxHello(alpn...)); err != nil {
		_ = raw.Close()
		return nil, err
	}
	if deadline, ok := ctx.Deadline(); ok {
		_ = uconn.SetDeadline(deadline)
	} else {
		_ = uconn.SetDeadline(time.Now().Add(15 * time.Second))
	}
	if err := uconn.HandshakeContext(ctx); err != nil {
		_ = raw.Close()
		return nil, err
	}
	_ = uconn.SetDeadline(time.Time{})
	return uconn, nil
}

func dialWithOptionalProxy(ctx context.Context, network, address, proxyRaw string) (net.Conn, error) {
	if proxyRaw == "" {
		return dialDirectBypass(ctx, network, address)
	}
	base := &net.Dialer{Timeout: 15 * time.Second}
	pu, err := url.Parse(proxyRaw)
	if err != nil {
		return nil, err
	}
	if pu.Scheme == "socks5" || pu.Scheme == "socks5h" {
		var auth *proxy.Auth
		if pu.User != nil {
			pw, _ := pu.User.Password()
			auth = &proxy.Auth{User: pu.User.Username(), Password: pw}
		}
		d, err := proxy.SOCKS5("tcp", pu.Host, auth, base)
		if err != nil {
			return nil, err
		}
		var cd proxy.ContextDialer
		if dcd, ok := d.(proxy.ContextDialer); ok {
			cd = dcd
		}
		dialFn := func(target string) (net.Conn, error) {
			if cd != nil {
				return cd.DialContext(ctx, network, target)
			}
			return d.Dial(network, target)
		}
		conn, dialErr := dialFn(address)
		if dialErr == nil {
			return conn, nil
		}
		if host, port, splitErr := net.SplitHostPort(address); splitErr == nil {
			if pinned, ok := knownMozillaIPs[strings.ToLower(strings.TrimSpace(host))]; ok {
				addrs := orderByHealth(pinned)
				if len(addrs) > 0 {
					if fbConn, fbErr := dialFn(net.JoinHostPort(addrs[0].String(), port)); fbErr == nil {
						return fbConn, nil
					}
				}
			}
		}
		return nil, dialErr
	}

	conn, err := base.DialContext(ctx, "tcp", pu.Host)
	if err != nil {
		return nil, err
	}
	reqLine := fmt.Sprintf("CONNECT %s HTTP/1.1\r\nHost: %s\r\n", address, address)
	if pu.User != nil {
		pw, _ := pu.User.Password()
		cred := base64.StdEncoding.EncodeToString([]byte(pu.User.Username() + ":" + pw))
		reqLine += "Proxy-Authorization: Basic " + cred + "\r\n"
	}
	reqLine += "\r\n"
	if _, err := io.WriteString(conn, reqLine); err != nil {
		_ = conn.Close()
		return nil, err
	}
	br := bufio.NewReader(conn)
	resp, err := http.ReadResponse(br, &http.Request{Method: http.MethodConnect})
	if err != nil {
		_ = conn.Close()
		return nil, err
	}
	_ = resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		_ = conn.Close()
		return nil, fmt.Errorf("proxy CONNECT returned HTTP %d", resp.StatusCode)
	}
	return conn, nil
}
