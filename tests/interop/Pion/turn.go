// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Authored local TURN/UDP echo acceptance service, public APIs only.
package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"net"
	"net/http"
	"sync"
	"time"

	"github.com/pion/turn/v5"
)

type turnSession struct {
	server *turn.Server
	peer   net.PacketConn
	cancel context.CancelFunc
}

func registerTurnService(mux *http.ServeMux, slots chan struct{}) {
	var sessions sync.Map
	mux.HandleFunc("POST /turn", func(w http.ResponseWriter, r *http.Request) {
		select {
		case slots <- struct{}{}:
		default:
			http.Error(w, "session limit", 429)
			return
		}
		transport := r.URL.Query().Get("transport")
		if transport == "" {
			transport = "udp"
		}
		if transport != "udp" && transport != "tcp" && transport != "tls" {
			<-slots
			http.Error(w, "invalid transport", 400)
			return
		}
		server, configuration, port, root, err := localRelayWithTransport(transport)
		if err != nil {
			<-slots
			http.Error(w, "local TURN start failed", 500)
			return
		}
		peer, err := net.ListenPacket("udp4", "127.0.0.1:0")
		if err != nil {
			server.Close()
			<-slots
			http.Error(w, "local UDP start failed", 500)
			return
		}
		token := make([]byte, 16)
		if _, err = rand.Read(token); err != nil {
			peer.Close()
			server.Close()
			<-slots
			http.Error(w, "identity failed", 500)
			return
		}
		id := hex.EncodeToString(token)
		ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
		session := &turnSession{server: server, peer: peer, cancel: cancel}
		sessions.Store(id, session)
		go func() {
			<-ctx.Done()
			peer.Close()
			server.Close()
			sessions.Delete(id)
			<-slots
		}()
		go func() {
			bytes := make([]byte, 16384)
			for {
				n, source, readErr := peer.ReadFrom(bytes)
				if readErr != nil {
					return
				}
				if _, writeErr := peer.WriteTo(bytes[:n], source); writeErr != nil {
					return
				}
			}
		}()
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(struct {
			Id       string `json:"id"`
			Port     int    `json:"port"`
			PeerPort int    `json:"peerPort"`
			Username string `json:"username"`
			Password string `json:"password"`
			Root     []byte `json:"root"`
		}{id, port, peer.LocalAddr().(*net.UDPAddr).Port, configuration.Username, configuration.Credential.(string), root})
	})
	mux.HandleFunc("GET /turn/{id}", func(w http.ResponseWriter, r *http.Request) {
		value, ok := sessions.Load(r.PathValue("id"))
		if !ok {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(struct {
			Allocations int `json:"allocations"`
		}{value.(*turnSession).server.AllocationCount()})
	})
	mux.HandleFunc("DELETE /turn/{id}", func(w http.ResponseWriter, r *http.Request) {
		if value, ok := sessions.Load(r.PathValue("id")); ok {
			value.(*turnSession).cancel()
		}
		w.WriteHeader(http.StatusNoContent)
	})
}
