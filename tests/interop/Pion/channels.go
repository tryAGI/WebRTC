// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Newly authored independent SCTP/DCEP test harness, no upstream example imports.
package main

import (
	"context"
	"log"
	"net"

	"github.com/pion/datachannel"
	"github.com/pion/logging"
	"github.com/pion/sctp"
)

func serveChannels(ctx context.Context, connection net.Conn, request offer) {
	connection = &sctpLossConnection{Conn: connection, incoming: request.ExtensionScenario == "incoming-loss",
		dropEnabled: request.ExtensionScenario != "", corruptForward: request.ExtensionScenario == "malformed-forward"}
	stopClose := context.AfterFunc(ctx, func() { _ = connection.Close() })
	defer stopClose()
	logger := logging.NewDefaultLoggerFactory()
	config := sctp.Config{LoggerFactory: logger, NetConn: connection, MTU: 1152, MaxMessageSize: 262144, MaxReceiveBufferSize: 1048576, BlockWrite: true}
	var association *sctp.Association
	var err error
	if request.SctpClient {
		association, err = sctp.Client(config)
	} else {
		association, err = sctp.Server(config)
	}
	if err != nil {
		log.Printf("SCTP handshake: %v", err)
		return
	}
	defer association.Close()
	var channel *datachannel.DataChannel
	if request.PeerOpensChannels {
		id := uint16(1)
		if request.DtlsClient {
			id = 0
		}
		kind := datachannel.ChannelType(request.Reliability)
		if request.Unordered {
			kind |= 128
		}
		channel, err = datachannel.Dial(association, id, &datachannel.Config{LoggerFactory: logger, Label: "oai-events", Protocol: "json", ChannelType: kind, ReliabilityParameter: request.ReliabilityParameter})
	} else {
		channel, err = datachannel.Accept(association, &datachannel.Config{LoggerFactory: logger})
	}
	if err != nil {
		log.Printf("DCEP open: %v", err)
		return
	}
	opened := make(chan struct{})
	if request.PeerOpensChannels {
		channel.OnOpen(func() { close(opened) })
	}
	// Reading processes DCEP ACK. PR-SCTP fault injection must start only after
	// that ACK; RFC 8832 requires messages sent before it to remain reliable.
	readDone := make(chan error, 1)
	go func() {
		buffer := make([]byte, 262144)
		count := 4
		if request.ExtensionScenario != "" {
			count = 1
		}
		for i := 0; i < count; i++ {
			n, text, e := channel.ReadDataChannel(buffer)
			if e != nil {
				readDone <- e
				return
			}
			if request.ExtensionScenario == "peer-loss" || request.ExtensionScenario == "malformed-forward" {
				continue
			}
			if _, e = channel.WriteDataChannel(buffer[:n], text); e != nil {
				readDone <- e
				return
			}
		}
		readDone <- nil
	}()
	if request.PeerOpensChannels {
		if request.ExtensionScenario == "peer-loss" || request.ExtensionScenario == "malformed-forward" {
			select {
			case <-opened:
			case err = <-readDone:
				log.Printf("DCEP ACK: %v", err)
				return
			case <-ctx.Done():
				return
			}
			// Keep the synthetic lost message within the initial congestion flight.
			lost := make([]byte, 2000)
			for i := range lost {
				lost[i] = 0xaa
			}
			if _, err = channel.WriteDataChannel(lost, false); err == nil {
				_, err = channel.WriteDataChannel([]byte("after-skip"), true)
			}
		} else {
			_, err = channel.WriteDataChannel([]byte("pion:ready"), true)
		}
		if err != nil {
			log.Printf("DCEP write: %v", err)
			return
		}
	}
	select {
	case err = <-readDone:
		if err != nil {
			log.Printf("DCEP read: %v", err)
			return
		}
	case <-ctx.Done():
		return
	}
	if err = association.Shutdown(ctx); err != nil {
		log.Printf("SCTP shutdown: %v", err)
	}
}
