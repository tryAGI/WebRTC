// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Independently authored local test peer; no Pion example source is imported.
package main

import (
	"context"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"os"
	"time"

	"github.com/pion/ice/v4"
)

type offer struct {
	Controlling bool   `json:"controlling"`
	Fragment    string `json:"fragment"`
	Password    string `json:"password"`
	Candidate   string `json:"candidate"`
}

type description struct {
	Fragment string `json:"fragment"`
	Password string `json:"password"`
	Address  string `json:"address"`
	Port     int    `json:"port"`
	Priority uint32 `json:"priority"`
}

func main() {
	if len(os.Args) == 2 && os.Args[1] == "--srtp-vectors" {
		writeSrtpVectors(os.Stdout)
		return
	}
	slots := make(chan struct{}, 4)
	mux := http.NewServeMux()
	registerSrtp(mux)
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
		if decoder.Decode(&request) != nil {
			release()
			http.Error(w, "invalid offer", 400)
			return
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
			buffer := make([]byte, 1200)
			n, readErr := connection.Read(buffer)
			if readErr == nil {
				connection.Write(append([]byte("pion:"), buffer[:n]...))
			}
		}()
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(description{fragment, password, local.Address(), local.Port(), local.Priority()})
	})
	server := &http.Server{Addr: "127.0.0.1:8080", Handler: mux, ReadHeaderTimeout: 2 * time.Second, ReadTimeout: 3 * time.Second, WriteTimeout: 3 * time.Second}
	if err := server.ListenAndServe(); err != nil {
		panic("local test peer stopped")
	}
}
