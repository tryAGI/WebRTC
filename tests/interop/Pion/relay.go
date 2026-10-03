// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Authored local-only TURN test service using public APIs; no upstream examples imported.
package main

import (
	"crypto/rand"
	"encoding/hex"
	"net"

	"github.com/pion/logging"
	"github.com/pion/turn/v5"
	"github.com/pion/webrtc/v4"
)

func localRelay() (*turn.Server, webrtc.ICEServer, int, error) {
	listener, err := net.ListenPacket("udp4", "127.0.0.1:0")
	if err != nil {
		return nil, webrtc.ICEServer{}, 0, err
	}
	secret := make([]byte, 32)
	if _, err = rand.Read(secret); err != nil {
		_ = listener.Close()
		return nil, webrtc.ICEServer{}, 0, err
	}
	password := hex.EncodeToString(secret)
	const username, realm = "isolated-peer", "tryagi.local"
	key := turn.GenerateAuthKey(username, realm, password)
	server, err := turn.NewServer(turn.ServerConfig{
		Realm: realm, LoggerFactory: logging.NewDefaultLoggerFactory(),
		AuthHandler: func(attributes *turn.RequestAttributes) (string, []byte, bool) {
			source, ok := attributes.SrcAddr.(*net.UDPAddr)
			return username, key, ok && source.IP.IsLoopback() && attributes.Username == username && attributes.Realm == realm
		},
		PacketConnConfigs: []turn.PacketConnConfig{{
			PacketConn:            listener,
			RelayAddressGenerator: &turn.RelayAddressGeneratorStatic{RelayAddress: net.ParseIP("127.0.0.1"), Address: "127.0.0.1"},
			PermissionHandler:     func(_ net.Addr, peerIP net.IP) bool { return peerIP.IsLoopback() },
		}},
	})
	if err != nil {
		_ = listener.Close()
		return nil, webrtc.ICEServer{}, 0, err
	}
	return server, webrtc.ICEServer{URLs: []string{"turn:" + listener.LocalAddr().String() + "?transport=udp"}, Username: username, Credential: password}, listener.LocalAddr().(*net.UDPAddr).Port, nil
}
