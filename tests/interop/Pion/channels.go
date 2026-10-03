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
		kind := datachannel.ChannelTypeReliable
		if request.Unordered {
			kind = datachannel.ChannelTypeReliableUnordered
		}
		channel, err = datachannel.Dial(association, id, &datachannel.Config{LoggerFactory: logger, Label: "oai-events", Protocol: "json", ChannelType: kind})
		if err == nil {
			_, err = channel.WriteDataChannel([]byte("pion:ready"), true)
		}
	} else {
		channel, err = datachannel.Accept(association, &datachannel.Config{LoggerFactory: logger})
	}
	if err != nil {
		log.Printf("DCEP open: %v", err)
		return
	}
	buffer := make([]byte, 262144)
	for i := 0; i < 4; i++ {
		n, text, e := channel.ReadDataChannel(buffer)
		if e != nil {
			log.Printf("DCEP read: %v", e)
			return
		}
		if _, e = channel.WriteDataChannel(buffer[:n], text); e != nil {
			log.Printf("DCEP write: %v", e)
			return
		}
	}
	if err = association.Shutdown(ctx); err != nil {
		log.Printf("SCTP shutdown: %v", err)
	}
}
