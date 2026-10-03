// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Independently authored bounded local harness using public Pion APIs.
package main

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"errors"
	"io"
	"log"
	"math/big"
	"net"
	"sync"
	"time"

	"github.com/pion/dtls/v3"
	"github.com/pion/srtp/v3"
)

func identity() (tls.Certificate, string, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, "", err
	}
	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 120))
	if err != nil {
		return tls.Certificate{}, "", err
	}
	template := &x509.Certificate{SerialNumber: serial, Subject: pkix.Name{CommonName: "local Pion peer"}, NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour), KeyUsage: x509.KeyUsageDigitalSignature}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	fingerprint := sha256.Sum256(der)
	return tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key}, hex.EncodeToString(fingerprint[:]), err
}

// One ICE reader demultiplexes DTLS and protected media, with bounded queues.
// Read deadlines wake a pending PacketConn reader when changed.
type securePackets struct {
	conn         net.Conn
	dtls, media  chan []byte
	done         chan struct{}
	changed      chan struct{}
	once         sync.Once
	mu           sync.Mutex
	readDeadline time.Time
}

func newSecurePackets(conn net.Conn) *securePackets {
	p := &securePackets{conn: conn, dtls: make(chan []byte, 128), media: make(chan []byte, 128), done: make(chan struct{}), changed: make(chan struct{}, 1)}
	go func() {
		defer p.Close()
		buffer := make([]byte, 1200)
		for {
			n, err := conn.Read(buffer)
			if err != nil {
				return
			}
			if n == 0 {
				continue
			}
			dest := p.dtls
			if buffer[0] >= 128 && buffer[0] <= 191 {
				dest = p.media
			} else if buffer[0] < 20 || buffer[0] > 63 {
				continue
			}
			packet := append([]byte(nil), buffer[:n]...)
			select {
			case dest <- packet:
			default:
			}
		}
	}()
	return p
}
func (p *securePackets) ReadFrom(buffer []byte) (int, net.Addr, error) {
	for {
		p.mu.Lock()
		deadline := p.readDeadline
		p.mu.Unlock()
		var timer *time.Timer
		var timeout <-chan time.Time
		if !deadline.IsZero() {
			timer = time.NewTimer(time.Until(deadline))
			timeout = timer.C
		}
		select {
		case packet := <-p.dtls:
			if timer != nil {
				timer.Stop()
			}
			if len(packet) > len(buffer) {
				return 0, nil, io.ErrShortBuffer
			}
			return copy(buffer, packet), p.conn.RemoteAddr(), nil
		case <-p.done:
			if timer != nil {
				timer.Stop()
			}
			return 0, nil, net.ErrClosed
		case <-p.changed:
			if timer != nil {
				timer.Stop()
			}
			continue
		case <-timeout:
			return 0, nil, timeoutError{}
		}
	}
}

type timeoutError struct{}

func (timeoutError) Error() string   { return "local peer read deadline" }
func (timeoutError) Timeout() bool   { return true }
func (timeoutError) Temporary() bool { return true }
func (p *securePackets) WriteTo(buffer []byte, address net.Addr) (int, error) {
	if address.String() != p.conn.RemoteAddr().String() {
		return 0, errors.New("unbound DTLS destination")
	}
	return p.conn.Write(buffer)
}
func (p *securePackets) Close() error {
	p.once.Do(func() { close(p.done); p.conn.Close() })
	return nil
}
func (p *securePackets) LocalAddr() net.Addr { return p.conn.LocalAddr() }
func (p *securePackets) SetReadDeadline(t time.Time) error {
	p.mu.Lock()
	p.readDeadline = t
	p.mu.Unlock()
	select {
	case p.changed <- struct{}{}:
	default:
	}
	return nil
}
func (p *securePackets) SetWriteDeadline(t time.Time) error { return p.conn.SetWriteDeadline(t) }
func (p *securePackets) SetDeadline(t time.Time) error {
	p.SetReadDeadline(t)
	return p.SetWriteDeadline(t)
}

func serveSecure(ctx context.Context, conn net.Conn, certificate tls.Certificate, request offer) {
	packets := newSecurePackets(conn)
	defer packets.Close()
	expected, err := hex.DecodeString(request.Fingerprint)
	if err != nil || len(expected) != 32 {
		return
	}
	profile := dtls.SRTPProtectionProfile(request.Profile)
	config := &dtls.Config{Certificates: []tls.Certificate{certificate}, CipherSuites: []dtls.CipherSuiteID{dtls.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256},
		SignatureSchemes: []tls.SignatureScheme{tls.ECDSAWithP256AndSHA256}, ExtendedMasterSecret: dtls.RequireExtendedMasterSecret,
		SRTPProtectionProfiles: []dtls.SRTPProtectionProfile{profile}, ClientAuth: dtls.RequireAnyClientCert,
		InsecureSkipVerify: true, // Self-signed WebRTC identity: authenticate exact signaled SHA-256 fingerprint below.
		VerifyPeerCertificate: func(raw [][]byte, _ [][]*x509.Certificate) error {
			if len(raw) == 0 {
				return errors.New("missing peer certificate")
			}
			actual := sha256.Sum256(raw[0])
			if subtle.ConstantTimeCompare(expected, actual[:]) != 1 {
				return errors.New("peer fingerprint mismatch")
			}
			return nil
		}, FlightInterval: 100 * time.Millisecond, MTU: request.Mtu, ReplayProtectionWindow: 64}
	var secure *dtls.Conn
	if request.DtlsClient {
		secure, err = dtls.Client(packets, conn.RemoteAddr(), config)
	} else {
		secure, err = dtls.Server(packets, conn.RemoteAddr(), config)
	}
	if err != nil {
		log.Printf("DTLS create: %v", err)
		return
	}
	defer secure.Close()
	if err = secure.HandshakeContext(ctx); err != nil {
		log.Printf("DTLS handshake: %v", err)
		return
	}
	if request.DataChannels {
		serveChannels(ctx, secure, request)
		return
	}
	selected, ok := secure.SelectedSRTPProtectionProfile()
	if !ok || selected != profile {
		return
	}
	state, ok := secure.ConnectionState()
	if !ok {
		return
	}
	keyLen, saltLen := 16, 12
	if request.Profile == 8 {
		keyLen = 32
	}
	if request.Profile == 1 {
		saltLen = 14
	}
	material, err := state.ExportKeyingMaterial("EXTRACTOR-dtls_srtp", nil, 2*(keyLen+saltLen))
	if err != nil {
		return
	}
	local, remote := 0, 1
	if !request.DtlsClient {
		local, remote = 1, 0
	}
	sender, err := srtp.CreateContext(material[local*keyLen:(local+1)*keyLen], material[2*keyLen+local*saltLen:2*keyLen+(local+1)*saltLen], srtp.ProtectionProfile(profile))
	if err != nil {
		return
	}
	receiver, err := srtp.CreateContext(material[remote*keyLen:(remote+1)*keyLen], material[2*keyLen+remote*saltLen:2*keyLen+(remote+1)*saltLen], srtp.ProtectionProfile(profile), srtp.SRTPReplayProtection(64), srtp.SRTCPReplayProtection(64))
	clear(material)
	if err != nil {
		return
	}
	appDone := make(chan error, 1)
	go func() {
		buffer := make([]byte, 1200)
		n, e := secure.Read(buffer)
		if e == nil {
			_, e = secure.Write(append([]byte("pion:"), buffer[:n]...))
		}
		appDone <- e
	}()
	for i := 0; i < 2; i++ {
		var packet []byte
		select {
		case packet = <-packets.media:
		case <-ctx.Done():
			return
		case <-packets.done:
			return
		}
		if len(packet) < 2 {
			return
		}
		rtcp := packet[1] >= 192 && packet[1] <= 223
		if rtcp {
			packet, err = receiver.DecryptRTCP(nil, packet, nil)
		} else {
			packet, err = receiver.DecryptRTP(nil, packet, nil)
		}
		if err != nil {
			log.Printf("SRTP decode: %v", err)
			return
		}
		if rtcp {
			packet, err = sender.EncryptRTCP(nil, packet, nil)
		} else {
			packet, err = sender.EncryptRTP(nil, packet, nil)
		}
		if err != nil {
			return
		}
		if _, err = conn.Write(packet); err != nil {
			return
		}
	}
	select {
	case <-appDone:
	case <-ctx.Done():
	}
}
