// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Newly authored independent SCTP/DCEP test harness, no upstream example imports.
package main

import (
	"context"
	"fmt"
	"io"
	"log"
	"net"
	"strings"
	"time"

	"github.com/pion/datachannel"
	"github.com/pion/logging"
	"github.com/pion/sctp"
)

func serveChannels(ctx context.Context, connection net.Conn, request offer) {
	connection = &sctpLossConnection{Conn: connection, incoming: request.ExtensionScenario == "incoming-loss",
		dropEnabled: request.ExtensionScenario == "incoming-loss" || request.ExtensionScenario == "peer-loss" || request.ExtensionScenario == "malformed-forward", corruptForward: request.ExtensionScenario == "malformed-forward",
		corruptReset: request.ExtensionScenario == "close-malformed"}
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
	if strings.HasPrefix(request.ExtensionScenario, "close-") {
		if err = serveChannelClosure(ctx, association, logger, request); err != nil {
			log.Printf("DCEP closure %s: %v", request.ExtensionScenario, err)
		}
		return
	}
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

// Only public Pion APIs are used. Both generations must close completely before
// the same stream ID is reused; EOF alone confirms only the incoming direction.
func serveChannelClosure(ctx context.Context, association *sctp.Association, logger *logging.DefaultLoggerFactory, request offer) error {
	id := uint16(1)
	if request.DtlsClient {
		id = 0
	}
	for generation := 0; generation < 2; generation++ {
		var channel *datachannel.DataChannel
		var err error
		if request.PeerOpensChannels {
			channel, err = datachannel.Dial(association, id, &datachannel.Config{LoggerFactory: logger, Label: "oai-events", Protocol: "json"})
		} else {
			channel, err = datachannel.Accept(association, &datachannel.Config{LoggerFactory: logger})
		}
		if err != nil {
			return err
		}
		if !request.PeerOpensChannels {
			id = channel.StreamIdentifier()
		}
		stream, err := association.OpenStream(id, sctp.PayloadTypeWebRTCBinary)
		if err != nil {
			return err
		}
		if request.PeerOpensChannels {
			opened := make(chan struct{})
			channel.OnOpen(func() { close(opened) })
			readDone := make(chan error, 1)
			go func() { buffer := make([]byte, 1024); _, _, e := channel.ReadDataChannel(buffer); readDone <- e }()
			select {
			case <-opened:
			case e := <-readDone:
				return fmt.Errorf("opening read: %w", e)
			case <-ctx.Done():
				return ctx.Err()
			}
			if _, err = channel.WriteDataChannel([]byte(fmt.Sprintf("generation:%d", generation)), true); err != nil {
				return err
			}
			if err = channel.Close(); err != nil {
				return err
			}
			select {
			case e := <-readDone:
				if e != io.EOF {
					return fmt.Errorf("closing read: %w", e)
				}
			case <-ctx.Done():
				return ctx.Err()
			}
		} else {
			buffer := make([]byte, 1024)
			n, text, e := channel.ReadDataChannel(buffer)
			if e != nil || !text || string(buffer[:n]) != fmt.Sprintf("generation:%d", generation) {
				return fmt.Errorf("generation data: %v", e)
			}
			if _, err = channel.WriteDataChannel(buffer[:n], true); err != nil {
				return err
			}
			if request.ExtensionScenario == "close-simultaneous" {
				if err = channel.Close(); err != nil {
					return err
				}
			}
			if _, _, err = channel.ReadDataChannel(buffer); err != io.EOF {
				return fmt.Errorf("reset EOF: %w", err)
			}
			if err = channel.Close(); err != nil {
				return err
			}
		}
		for stream.State() != sctp.StreamStateClosed {
			select {
			case <-time.After(time.Millisecond):
			case <-ctx.Done():
				return ctx.Err()
			}
		}
	}
	return association.Shutdown(ctx)
}
