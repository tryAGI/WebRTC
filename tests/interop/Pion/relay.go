// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Authored local-only TURN test service using public APIs; no upstream examples imported.
package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"fmt"
	"math/big"
	"net"
	"time"

	"github.com/pion/logging"
	"github.com/pion/turn/v5"
	"github.com/pion/webrtc/v4"
)

func localRelay() (*turn.Server, webrtc.ICEServer, int, error) {
	server, config, port, _, err := localRelayWithTransport("udp")
	return server, config, port, err
}

func localRelayWithTransport(transport string) (*turn.Server, webrtc.ICEServer, int, []byte, error) {
	if transport != "udp" && transport != "tcp" && transport != "tls" {
		return nil, webrtc.ICEServer{}, 0, nil, fmt.Errorf("unsupported local transport")
	}
	var packet net.PacketConn
	var listener net.Listener
	var address net.Addr
	var root []byte
	var err error
	if transport == "udp" {
		packet, err = net.ListenPacket("udp4", "127.0.0.1:0")
		if err == nil {
			address = packet.LocalAddr()
		}
	} else {
		listener, err = net.Listen("tcp4", "127.0.0.1:0")
		if err == nil {
			address = listener.Addr()
			if transport == "tls" {
				var certificate tls.Certificate
				certificate, root, err = localTurnCertificate()
				if err == nil {
					listener = tls.NewListener(listener, &tls.Config{Certificates: []tls.Certificate{certificate}, MinVersion: tls.VersionTLS12})
				}
			}
		}
	}
	closeListener := func() {
		if packet != nil {
			_ = packet.Close()
		}
		if listener != nil {
			_ = listener.Close()
		}
	}
	if err != nil {
		closeListener()
		return nil, webrtc.ICEServer{}, 0, nil, err
	}
	secret := make([]byte, 32)
	if _, err = rand.Read(secret); err != nil {
		closeListener()
		return nil, webrtc.ICEServer{}, 0, nil, err
	}
	password := hex.EncodeToString(secret)
	const username, realm = "isolated-peer", "tryagi.local"
	key := turn.GenerateAuthKey(username, realm, password)
	config := turn.ServerConfig{
		Realm: realm, LoggerFactory: logging.NewDefaultLoggerFactory(),
		AuthHandler: func(attributes *turn.RequestAttributes) (string, []byte, bool) {
			var ip net.IP
			switch source := attributes.SrcAddr.(type) {
			case *net.UDPAddr:
				ip = source.IP
			case *net.TCPAddr:
				ip = source.IP
			}
			return username, key, ip != nil && ip.IsLoopback() && attributes.Username == username && attributes.Realm == realm
		},
	}
	generator := &turn.RelayAddressGeneratorStatic{RelayAddress: net.ParseIP("127.0.0.1"), Address: "127.0.0.1"}
	permission := func(_ net.Addr, peerIP net.IP) bool { return peerIP.IsLoopback() }
	if packet != nil {
		config.PacketConnConfigs = []turn.PacketConnConfig{{PacketConn: packet, RelayAddressGenerator: generator, PermissionHandler: permission}}
	} else {
		config.ListenerConfigs = []turn.ListenerConfig{{Listener: listener, RelayAddressGenerator: generator, PermissionHandler: permission}}
	}
	server, err := turn.NewServer(config)
	if err != nil {
		closeListener()
		return nil, webrtc.ICEServer{}, 0, nil, err
	}
	port := 0
	switch addr := address.(type) {
	case *net.UDPAddr:
		port = addr.Port
	case *net.TCPAddr:
		port = addr.Port
	}
	scheme, wire := "turn:", transport
	if transport == "tls" {
		scheme, wire = "turns:", "tcp"
	}
	return server, webrtc.ICEServer{URLs: []string{scheme + address.String() + "?transport=" + wire}, Username: username, Credential: password}, port, root, nil
}

// Ephemeral certificate authority/leaf authored with Go standard APIs. Nothing is persisted or imported.
func localTurnCertificate() (tls.Certificate, []byte, error) {
	rootKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, nil, err
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, nil, err
	}
	now := time.Now()
	root := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "Isolated TURN root"}, NotBefore: now.Add(-time.Hour), NotAfter: now.Add(time.Hour), IsCA: true, BasicConstraintsValid: true, KeyUsage: x509.KeyUsageCertSign}
	rootDER, err := x509.CreateCertificate(rand.Reader, root, root, &rootKey.PublicKey, rootKey)
	if err != nil {
		return tls.Certificate{}, nil, err
	}
	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if err != nil {
		return tls.Certificate{}, nil, err
	}
	leaf := &x509.Certificate{SerialNumber: serial, DNSNames: []string{"turn.fixture.local"}, NotBefore: now.Add(-time.Minute), NotAfter: now.Add(30 * time.Minute), KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	leafDER, err := x509.CreateCertificate(rand.Reader, leaf, root, &key.PublicKey, rootKey)
	if err != nil {
		return tls.Certificate{}, nil, err
	}
	return tls.Certificate{Certificate: [][]byte{leafDER, rootDER}, PrivateKey: key}, rootDER, nil
}
