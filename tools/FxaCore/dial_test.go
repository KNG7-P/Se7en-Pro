package main

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"net"
	"net/netip"
	"testing"
	"time"
)

func buildTestClientHelloGo(sniHost string) []byte {
	hostBytes := []byte(sniHost)

	extPayload := make([]byte, 0, 5+len(hostBytes))
	extPayload = binary.BigEndian.AppendUint16(extPayload, uint16(1+2+len(hostBytes)))
	extPayload = append(extPayload, 0x00)
	extPayload = binary.BigEndian.AppendUint16(extPayload, uint16(len(hostBytes)))
	extPayload = append(extPayload, hostBytes...)

	exts := make([]byte, 0, 4+len(extPayload))
	exts = binary.BigEndian.AppendUint16(exts, 0x0000)
	exts = binary.BigEndian.AppendUint16(exts, uint16(len(extPayload)))
	exts = append(exts, extPayload...)

	body := make([]byte, 0, 64+len(exts))
	body = append(body, 0x03, 0x03)
	body = append(body, make([]byte, 32)...)
	body = append(body, 0x00)
	body = binary.BigEndian.AppendUint16(body, 2)
	body = binary.BigEndian.AppendUint16(body, 0x1301)
	body = append(body, 0x01, 0x00)
	body = binary.BigEndian.AppendUint16(body, uint16(len(exts)))
	body = append(body, exts...)

	hs := make([]byte, 0, 4+len(body))
	hs = append(hs, 0x01, byte(len(body)>>16), byte(len(body)>>8), byte(len(body)))
	hs = append(hs, body...)

	rec := make([]byte, 0, 5+len(hs))
	rec = append(rec, 0x16, 0x03, 0x01)
	rec = binary.BigEndian.AppendUint16(rec, uint16(len(hs)))
	rec = append(rec, hs...)
	return rec
}

func TestIsBlockPageOrBogon(t *testing.T) {
	blocked := []string{
		"10.10.34.34", "10.10.34.35", "10.10.34.36",
		"127.0.0.1", "192.168.1.1", "172.16.0.5", "169.254.1.1",
		"::1", "fc00::1", "2001:4188:2:600:10:10:34:35",
	}
	for _, s := range blocked {
		a := netip.MustParseAddr(s)
		if !isBlockPageOrBogon(a) {
			t.Fatalf("expected %s to be rejected as bogon/blockpage", s)
		}
	}
	clean := []string{"151.101.1.91", "151.101.193.91", "8.8.8.8", "2606:4700:4700::1111"}
	for _, s := range clean {
		a := netip.MustParseAddr(s)
		if isBlockPageOrBogon(a) {
			t.Fatalf("expected %s to be allowed", s)
		}
	}
}

func TestHelloSplitConn(t *testing.T) {
	hello := buildTestClientHelloGo("api.accounts.firefox.com")
	start, ok := findSNIServerNameOffset(hello)
	if !ok || !bytes.Equal(hello[start:start+len("api.accounts.firefox.com")], []byte("api.accounts.firefox.com")) {
		t.Fatalf("unexpected SNI offset: ok=%v start=%d", ok, start)
	}

	c1, c2 := net.Pipe()
	defer c1.Close()
	defer c2.Close()

	split := &helloSplitConn{Conn: c1}
	done := make(chan [][]byte, 1)
	go func() {
		var chunks [][]byte
		buf := make([]byte, 4096)
		for len(chunks) < 4 {
			n, err := c2.Read(buf)
			if err != nil {
				break
			}
			cp := make([]byte, n)
			copy(cp, buf[:n])
			chunks = append(chunks, cp)
		}
		done <- chunks
	}()

	if _, err := split.Write(hello); err != nil {
		t.Fatalf("write hello: %v", err)
	}
	if _, err := split.Write([]byte("NEXT")); err != nil {
		t.Fatalf("write next: %v", err)
	}

	chunks := <-done
	if len(chunks) != 4 {
		t.Fatalf("expected 4 writes (3 SNI segments + 1 passthrough), got %d", len(chunks))
	}
	if string(chunks[1]) != "a" || string(chunks[3]) != "NEXT" {
		t.Fatalf("unexpected chunks: second=%q fourth=%q", chunks[1], chunks[3])
	}
	joined := append(append(append([]byte{}, chunks[0]...), chunks[1]...), chunks[2]...)
	if !bytes.Equal(joined, hello) {
		t.Fatalf("reconstructed ClientHello mismatch")
	}
}

func TestDialRaceAndCooldown(t *testing.T) {
	failedAddrs.Range(func(k, _ any) bool { failedAddrs.Delete(k); return true })
	defer failedAddrs.Range(func(k, _ any) bool { failedAddrs.Delete(k); return true })

	bad := netip.MustParseAddr("192.0.2.1")
	slow := netip.MustParseAddr("198.51.100.1")
	good := netip.MustParseAddr("151.101.1.91")

	conn, err := dialRace(context.Background(), func(ctx context.Context, _, addr string) (net.Conn, error) {
		host, _, _ := net.SplitHostPort(addr)
		switch host {
		case bad.String():
			return nil, errors.New("connection refused")
		case slow.String():
			<-ctx.Done()
			return nil, ctx.Err()
		default:
			a, b := net.Pipe()
			_ = b.Close()
			return a, nil
		}
	}, "tcp", "443", []netip.Addr{slow, bad, good}, 20*time.Millisecond)
	if err != nil {
		t.Fatalf("dialRace failed: %v", err)
	}
	_ = conn.Close()

	ordered := orderByHealth([]netip.Addr{bad, good})
	if len(ordered) != 2 || ordered[0] != good || ordered[1] != bad {
		t.Fatalf("expected failed address %s to be demoted behind %s, got %v", bad, good, ordered)
	}
}
