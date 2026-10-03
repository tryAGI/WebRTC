// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Independently authored local test peer; no Pion example source is imported.
package main

import (
	"context"
	"crypto/tls"
	"encoding/hex"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"os"
	"time"

	"github.com/pion/ice/v4"
)

type offer struct {
	MediaPackets         int    `json:"mediaPackets"`
	DataChannels         bool   `json:"dataChannels"`
	SctpClient           bool   `json:"sctpClient"`
	Reliability          int    `json:"reliability"`
	ReliabilityParameter uint32 `json:"reliabilityParameter"`
	ExtensionScenario    string `json:"extensionScenario"`
	PeerOpensChannels    bool   `json:"peerOpensChannels"`
	Unordered            bool   `json:"unordered"`
	Secure               bool   `json:"secure"`
	DtlsClient           bool   `json:"dtlsClient"`
	Fingerprint          string `json:"fingerprint"`
	Profile              uint16 `json:"profile"`
	Mtu                  int    `json:"mtu"`
	Controlling          bool   `json:"controlling"`
	Fragment             string `json:"fragment"`
	Password             string `json:"password"`
	Candidate            string `json:"candidate"`
}

type description struct {
	Fingerprint string `json:"fingerprint,omitempty"`
	Fragment    string `json:"fragment"`
	Password    string `json:"password"`
	Address     string `json:"address"`
	Port        int    `json:"port"`
	Priority    uint32 `json:"priority"`
}

func main() {
	if len(os.Args) == 2 && os.Args[1] == "--srtp-vectors" {
		writeSrtpVectors(os.Stdout)
		return
	}
	slots := make(chan struct{}, 4)
	mux := http.NewServeMux()
	registerTurnService(mux, slots)
	registerSrtp(mux)
	registerPeerConnections(mux, slots)
	mux.HandleFunc("GET /health", func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(http.StatusOK) })
	mux.HandleFunc("POST /peer", func(w http.ResponseWriter, r *http.Request) {
		select {
		case slots <- struct{}{}:
		default:
			http.Error(w, "session limit", 429)
			return
		}
		released := false
		release := func() {
			if !released {
				released = true
				<-slots
			}
		}
		var request offer
		decoder := json.NewDecoder(io.LimitReader(r.Body, 4096))
		decoder.DisallowUnknownFields()
		if decoder.Decode(&request) != nil || request.MediaPackets < 0 || request.MediaPackets > 512 {
			release()
			http.Error(w, "invalid offer", 400)
			return
		}
		var certificate tls.Certificate
		var fingerprint string
		if request.Secure {
			raw, e := hex.DecodeString(request.Fingerprint)
			if e != nil || len(raw) != 32 || (request.Profile != 1 && request.Profile != 7 && request.Profile != 8) || request.Mtu < 256 || request.Mtu > 1200 {
				release()
				http.Error(w, "invalid secure offer", 400)
				return
			}
			var e2 error
			certificate, fingerprint, e2 = identity()
			if e2 != nil {
				release()
				http.Error(w, "identity creation failed", 500)
				return
			}
		}
		remote, err := ice.UnmarshalCandidate(request.Candidate)
		if err != nil || !net.ParseIP(remote.Address()).IsLoopback() {
			release()
			http.Error(w, "local peer required", 400)
			return
		}
		agent, err := ice.NewAgent(&ice.AgentConfig{
			NetworkTypes:     []ice.NetworkType{ice.NetworkTypeUDP4},
			CandidateTypes:   []ice.CandidateType{ice.CandidateTypeHost},
			IncludeLoopback:  true,
			MulticastDNSMode: ice.MulticastDNSModeDisabled,
			IPFilter:         func(ip net.IP) bool { return ip.IsLoopback() },
			RemoteIPFilter:   func(ip net.IP) bool { return ip.IsLoopback() },
		})
		if err != nil {
			release()
			http.Error(w, "agent creation failed", 500)
			return
		}
		candidates := make(chan ice.Candidate, 16)
		err = agent.OnCandidate(func(candidate ice.Candidate) {
			if candidate == nil {
				close(candidates)
				return
			}
			candidates <- candidate
		})
		if err == nil {
			err = agent.GatherCandidates()
		}
		if err != nil {
			agent.Close()
			release()
			http.Error(w, "gathering failed", 500)
			return
		}
		var local ice.Candidate
		for candidate := range candidates {
			if local == nil {
				local = candidate
			}
		}
		if local == nil {
			agent.Close()
			release()
			http.Error(w, "no local candidate", 500)
			return
		}
		fragment, password, err := agent.GetLocalUserCredentials()
		if err == nil {
			err = agent.AddRemoteCandidate(remote)
		}
		if err != nil {
			agent.Close()
			release()
			http.Error(w, "candidate rejected", 400)
			return
		}
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		go func() { <-ctx.Done(); agent.Close() }()
		go func() {
			defer cancel()
			defer release()
			var connection *ice.Conn
			var connectionErr error
			if request.Controlling {
				connection, connectionErr = agent.Dial(ctx, request.Fragment, request.Password)
			} else {
				connection, connectionErr = agent.Accept(ctx, request.Fragment, request.Password)
			}
			if connectionErr != nil {
				return
			}
			if request.Secure {
				serveSecure(ctx, connection, certificate, request)
				return
			}
			buffer := make([]byte, 1200)
			n, readErr := connection.Read(buffer)
			if readErr == nil {
				connection.Write(append([]byte("pion:"), buffer[:n]...))
			}
		}()
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(description{Fingerprint: fingerprint, Fragment: fragment, Password: password, Address: local.Address(), Port: local.Port(), Priority: local.Priority()})
	})
	server := &http.Server{Addr: "127.0.0.1:8080", Handler: mux, ReadHeaderTimeout: 2 * time.Second, ReadTimeout: 3 * time.Second, WriteTimeout: 3 * time.Second}
	if err := server.ListenAndServe(); err != nil {
		panic("local test peer stopped")
	}
}
