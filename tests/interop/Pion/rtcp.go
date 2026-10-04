// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Authored synthetic RTCP fixtures using pinned Pion public API; no upstream examples/fixtures imported.
package main

import (
	"encoding/hex"
	"encoding/json"
	"net/http"

	"github.com/pion/rtcp"
)

func rtcpVectors() [][]rtcp.Packet {
	report := rtcp.ReceptionReport{SSRC: 99, FractionLost: 64, TotalLost: 7, LastSequenceNumber: 65537, Jitter: 128, LastSenderReport: 0x11223344, Delay: 65536}
	return [][]rtcp.Packet{
		{
			&rtcp.SenderReport{SSRC: 0x21324354, NTPTime: 0x123456789abcdef0, RTPTime: 0x87654321, PacketCount: 4, OctetCount: 5, Reports: []rtcp.ReceptionReport{report}},
			rtcp.NewCNAMESourceDescription(0x21324354, "peer/Иван"),
			&rtcp.PictureLossIndication{SenderSSRC: 0x21324354, MediaSSRC: 99},
		},
		{&rtcp.ReceiverReport{SSRC: 3, Reports: []rtcp.ReceptionReport{report}}, rtcp.NewCNAMESourceDescription(3, "other")},
		{&rtcp.PictureLossIndication{SenderSSRC: 3, MediaSSRC: 99}},
		{&rtcp.Goodbye{Sources: []uint32{3, 4}, Reason: "bye/x"}},
	}
}

func registerRtcp(mux *http.ServeMux) {
	slots := make(chan struct{}, 4)
	mux.HandleFunc("GET /rtcp-vectors", func(w http.ResponseWriter, r *http.Request) {
		results := make([]string, 0, 4)
		for _, packets := range rtcpVectors() {
			wire, err := rtcp.Marshal(packets)
			if err != nil || len(wire) > 1200 {
				http.Error(w, "synthetic vector error", 500)
				return
			}
			results = append(results, hex.EncodeToString(wire))
		}
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(results)
	})
	mux.HandleFunc("POST /rtcp-reencode", func(w http.ResponseWriter, r *http.Request) {
		select {
		case slots <- struct{}{}:
		default:
			http.Error(w, "session limit", 429)
			return
		}
		defer func() { <-slots }()
		var request []string
		decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 32000))
		if decoder.Decode(&request) != nil || len(request) < 1 || len(request) > 8 {
			http.Error(w, "invalid packet batch", 400)
			return
		}
		result := make([]string, 0, len(request))
		for _, text := range request {
			if len(text) > 2400 {
				http.Error(w, "packet bound", 400)
				return
			}
			data, err := hex.DecodeString(text)
			if err != nil || len(data) < 8 {
				http.Error(w, "invalid packet", 400)
				return
			}
			packets, err := rtcp.Unmarshal(data)
			if err != nil || len(packets) > 32 {
				http.Error(w, "semantic decode rejected", 400)
				return
			}
			wire, err := rtcp.Marshal(packets)
			if err != nil || len(wire) > 1200 {
				http.Error(w, "semantic encode rejected", 400)
				return
			}
			result = append(result, hex.EncodeToString(wire))
		}
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(result)
	})
}
