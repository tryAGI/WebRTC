// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Newly authored full local peer using public APIs, no upstream examples/algorithms imported.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/pion/dtls/v3"
	"github.com/pion/rtp"
	"github.com/pion/turn/v5"
	"github.com/pion/webrtc/v4"
)

type sessionRequest struct {
	Sdp     string `json:"sdp"`
	Passive bool   `json:"passive"`
	Relay   bool   `json:"relay"`
	Video   string `json:"video"`
}
type sessionResponse struct {
	Id       string `json:"id"`
	Sdp      string `json:"sdp"`
	StunPort int    `json:"stunPort,omitempty"`
}
type fullSession struct {
	peer     *webrtc.PeerConnection
	cancel   context.CancelFunc
	audio    atomic.Int32
	video    atomic.Int32
	data     atomic.Int32
	failures atomic.Int32
	relay    *turn.Server
}

func registerPeerConnections(mux *http.ServeMux, slots chan struct{}) {
	var gate sync.Mutex
	sessions := make(map[string]*fullSession)
	var sequence atomic.Uint64
	decode := func(w http.ResponseWriter, r *http.Request) (sessionRequest, bool) {
		var request sessionRequest
		decoder := json.NewDecoder(http.MaxBytesReader(w, r.Body, 70000))
		decoder.DisallowUnknownFields()
		if decoder.Decode(&request) != nil {
			http.Error(w, "invalid session", 400)
			return request, false
		}
		if request.Video != "" && request.Video != "h264" && request.Video != "vp8" {
			http.Error(w, "invalid video codec", 400)
			return request, false
		}
		// The default acceptance lane has no public DNS, STUN/TURN or provider endpoints.
		for line := range strings.Lines(request.Sdp) {
			if strings.HasPrefix(line, "a=candidate:") {
				fields := strings.Fields(strings.TrimPrefix(line, "a=candidate:"))
				if len(fields) < 8 || !net.ParseIP(fields[4]).IsLoopback() {
					http.Error(w, "local candidate required", 400)
					return request, false
				}
			}
		}
		return request, true
	}
	create := func(w http.ResponseWriter, r *http.Request, offerer bool) {
		request, ok := decode(w, r)
		if !ok {
			return
		}
		select {
		case slots <- struct{}{}:
		default:
			http.Error(w, "session limit", 429)
			return
		}
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		settings := webrtc.SettingEngine{}
		settings.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
		settings.SetIncludeLoopbackCandidate(true)
		settings.SetIPFilter(func(ip net.IP) bool { return ip.IsLoopback() })
		settings.SetRemoteIPFilter(func(ip net.IP) bool { return ip.IsLoopback() })
		settings.SetHostAcceptanceMinWait(0)
		settings.SetDTLSExtendedMasterSecret(dtls.RequireExtendedMasterSecret)
		if !offerer {
			role := webrtc.DTLSRoleClient
			if request.Passive {
				role = webrtc.DTLSRoleServer
			}
			_ = settings.SetAnsweringDTLSRole(role)
		}
		media := &webrtc.MediaEngine{}
		capability := webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2, SDPFmtpLine: "minptime=10;useinbandfec=1"}
		err := media.RegisterCodec(webrtc.RTPCodecParameters{RTPCodecCapability: capability, PayloadType: 111}, webrtc.RTPCodecTypeAudio)
		if err == nil {
			err = media.RegisterHeaderExtension(webrtc.RTPHeaderExtensionCapability{URI: "urn:ietf:params:rtp-hdrext:sdes:mid"}, webrtc.RTPCodecTypeAudio)
		}
		var videoCapability webrtc.RTPCodecCapability
		if err == nil && request.Video != "" {
			videoCapability = webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeVP8, ClockRate: 90000, SDPFmtpLine: "max-fs=3600;max-fr=30"}
			videoPt := webrtc.PayloadType(96)
			if request.Video == "h264" {
				videoCapability.MimeType = webrtc.MimeTypeH264
				videoCapability.SDPFmtpLine = "profile-level-id=42e01f;packetization-mode=1;level-asymmetry-allowed=0"
				videoPt = 102
			}
			err = media.RegisterCodec(webrtc.RTPCodecParameters{RTPCodecCapability: videoCapability, PayloadType: videoPt}, webrtc.RTPCodecTypeVideo)
			if err == nil {
				err = media.RegisterHeaderExtension(webrtc.RTPHeaderExtensionCapability{URI: "urn:ietf:params:rtp-hdrext:sdes:mid"}, webrtc.RTPCodecTypeVideo)
			}
		}
		api := webrtc.NewAPI(webrtc.WithSettingEngine(settings), webrtc.WithMediaEngine(media))
		configuration := webrtc.Configuration{}
		var relay *turn.Server
		stunPort := 0
		if err == nil && request.Relay {
			var service webrtc.ICEServer
			relay, service, stunPort, err = localRelay()
			configuration = webrtc.Configuration{ICEServers: []webrtc.ICEServer{service}, ICETransportPolicy: webrtc.ICETransportPolicyRelay}
		}
		var pc *webrtc.PeerConnection
		if err == nil {
			pc, err = api.NewPeerConnection(configuration)
		}
		if err != nil {
			if relay != nil {
				_ = relay.Close()
			}
			cancel()
			<-slots
			http.Error(w, "peer creation failed", 500)
			return
		}
		id := fmt.Sprint(sequence.Add(1))
		session := &fullSession{peer: pc, cancel: cancel, relay: relay}
		gate.Lock()
		sessions[id] = session
		gate.Unlock()
		context.AfterFunc(ctx, func() {
			_ = pc.Close()
			if relay != nil {
				_ = relay.Close()
			}
			gate.Lock()
			delete(sessions, id)
			gate.Unlock()
			<-slots
		})
		fail := func() { session.failures.Add(1); cancel(); http.Error(w, "session negotiation failed", 400) }
		localTrack, err := webrtc.NewTrackLocalStaticRTP(capability, "audio", "tryagi")
		var sender *webrtc.RTPSender
		if err == nil {
			sender, err = pc.AddTrack(localTrack)
		}
		if err != nil {
			fail()
			return
		}
		go func() {
			for {
				if _, _, e := sender.ReadRTCP(); e != nil {
					return
				}
			}
		}()
		var videoTrack *webrtc.TrackLocalStaticRTP
		if request.Video != "" {
			videoTrack, err = webrtc.NewTrackLocalStaticRTP(videoCapability, "video", "tryagi")
			var videoSender *webrtc.RTPSender
			if err == nil {
				videoSender, err = pc.AddTrack(videoTrack)
			}
			if err != nil {
				fail()
				return
			}
			go func() {
				for {
					if _, _, e := videoSender.ReadRTCP(); e != nil {
						return
					}
				}
			}()
		}
		pc.OnTrack(func(track *webrtc.TrackRemote, receiver *webrtc.RTPReceiver) {
			go func() {
				for {
					packet, _, e := track.ReadRTP()
					if e != nil {
						if e != io.EOF && ctx.Err() == nil {
							log.Printf("local RTP reader stopped")
						}
						return
					}
					codec := track.Codec()
					echoTrack := localTrack
					if codec.MimeType == webrtc.MimeTypeOpus && codec.ClockRate == 48000 && len(packet.Payload) <= 1275 {
						session.audio.Add(1)
					} else if videoTrack != nil && codec.MimeType == videoCapability.MimeType && codec.ClockRate == 90000 && len(packet.Payload) <= 4096 {
						echoTrack = videoTrack
						session.video.Add(1)
					} else {
						session.failures.Add(1)
						cancel()
						return
					}
					// Public track writer supplies its negotiated PT/SSRC. Do not echo the
					// other sender's MID extension into our separately signaled track.
					echo := &rtp.Packet{Header: rtp.Header{Version: 2, SequenceNumber: packet.SequenceNumber, Timestamp: packet.Timestamp, Marker: packet.Marker}, Payload: packet.Payload}
					if echoTrack.WriteRTP(echo) != nil {
						session.failures.Add(1)
						cancel()
						return
					}
				}
			}()
		})
		configure := func(channel *webrtc.DataChannel) {
			channel.OnMessage(func(message webrtc.DataChannelMessage) {
				if len(message.Data) > 262144 {
					session.failures.Add(1)
					cancel()
					return
				}
				session.data.Add(1)
				var e error
				if message.IsString {
					e = channel.SendText(string(message.Data))
				} else {
					e = channel.Send(message.Data)
				}
				if e != nil {
					session.failures.Add(1)
					cancel()
				}
			})
			if offerer {
				channel.OnOpen(func() {
					if channel.SendText("pion:ready") != nil {
						session.failures.Add(1)
						cancel()
					}
				})
			}
		}
		pc.OnDataChannel(configure)
		if offerer {
			var channel *webrtc.DataChannel
			channel, err = pc.CreateDataChannel("oai-events", nil)
			if err == nil {
				configure(channel)
			}
		}
		if err == nil && !offerer {
			err = pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeOffer, SDP: request.Sdp})
		}
		var description webrtc.SessionDescription
		if err == nil {
			if offerer {
				description, err = pc.CreateOffer(nil)
			} else {
				description, err = pc.CreateAnswer(nil)
			}
		}
		gathered := webrtc.GatheringCompletePromise(pc)
		if err == nil {
			err = pc.SetLocalDescription(description)
		}
		if err != nil {
			fail()
			return
		}
		select {
		case <-gathered:
		case <-ctx.Done():
			fail()
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(sessionResponse{Id: id, Sdp: pc.LocalDescription().SDP, StunPort: stunPort})
	}
	mux.HandleFunc("POST /session/offer", func(w http.ResponseWriter, r *http.Request) { create(w, r, true) })
	mux.HandleFunc("POST /session/answer", func(w http.ResponseWriter, r *http.Request) { create(w, r, false) })
	mux.HandleFunc("POST /session/{id}/answer", func(w http.ResponseWriter, r *http.Request) {
		request, ok := decode(w, r)
		if !ok {
			return
		}
		gate.Lock()
		session := sessions[r.PathValue("id")]
		gate.Unlock()
		if session == nil {
			http.Error(w, "session missing", 404)
			return
		}
		if session.peer.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: request.Sdp}) != nil {
			session.failures.Add(1)
			session.cancel()
			http.Error(w, "answer rejected", 400)
			return
		}
		w.WriteHeader(204)
	})
	mux.HandleFunc("GET /session/{id}/stats", func(w http.ResponseWriter, r *http.Request) {
		gate.Lock()
		session := sessions[r.PathValue("id")]
		gate.Unlock()
		if session == nil {
			http.Error(w, "session missing", 404)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		allocations := 0
		if session.relay != nil {
			allocations = session.relay.AllocationCount()
		}
		_ = json.NewEncoder(w).Encode(map[string]int32{"audio": session.audio.Load(), "video": session.video.Load(), "data": session.data.Load(), "failures": session.failures.Load(), "relayAllocations": int32(allocations)})
	})
	mux.HandleFunc("DELETE /session/{id}", func(w http.ResponseWriter, r *http.Request) {
		gate.Lock()
		session := sessions[r.PathValue("id")]
		gate.Unlock()
		if session != nil {
			session.cancel()
		}
		w.WriteHeader(204)
	})
}
