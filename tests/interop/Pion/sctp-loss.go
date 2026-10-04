// Copyright (c) 2026 tryAGI and contributors. Licensed under MIT.
// Newly authored RFC 9260 fault injection for isolated PR-SCTP interoperability.
package main

import (
	"encoding/binary"
	"hash/crc32"
	"io"
	"net"
	"sync"
	"sync/atomic"
)

type sctpLossConnection struct {
	net.Conn
	incoming           bool
	dropEnabled        bool
	corruptForward     bool
	reorderResetResult bool
	resetResultHeld    bool
	heldResetResult    []byte
	writeGate          sync.Mutex
	corruptReset       bool
	resetMutations     atomic.Int32
	forwardMutations   atomic.Int32
}

func stripLostMessage(packet []byte) ([]byte, bool) {
	if len(packet) < 16 {
		return packet, false
	}
	kept := append([]byte(nil), packet[:12]...)
	dropped := false
	for offset := 12; offset < len(packet); {
		if offset+4 > len(packet) {
			return packet, false
		}
		length := int(binary.BigEndian.Uint16(packet[offset+2:]))
		padded := (length + 3) & ^3
		if length < 4 || offset+padded > len(packet) {
			return packet, false
		}
		lose := packet[offset] == 0 && length >= 17 && binary.BigEndian.Uint32(packet[offset+12:]) == 53
		if lose {
			for _, value := range packet[offset+16 : offset+length] {
				if value != 0xaa {
					lose = false
					break
				}
			}
		}
		if lose {
			dropped = true
		} else {
			kept = append(kept, packet[offset:offset+padded]...)
		}
		offset += padded
	}
	if !dropped {
		return packet, false
	}
	if len(kept) == 12 {
		return nil, true
	}
	for i := 8; i < 12; i++ {
		kept[i] = 0
	}
	binary.LittleEndian.PutUint32(kept[8:], crc32.Checksum(kept, crc32.MakeTable(crc32.Castagnoli)))
	return kept, true
}

func (c *sctpLossConnection) Read(buffer []byte) (int, error) {
	for {
		n, err := c.Conn.Read(buffer)
		if !c.dropEnabled || !c.incoming || n == 0 {
			return n, err
		}
		kept, dropped := stripLostMessage(buffer[:n])
		if !dropped {
			return n, err
		}
		if len(kept) != 0 {
			return copy(buffer, kept), err
		}
		if err != nil {
			return 0, err
		}
	}
}
func (c *sctpLossConnection) malformedForward(packet []byte) ([]byte, bool) {
	if !c.corruptForward {
		return packet, false
	}
	for offset := 12; offset+4 <= len(packet); {
		length := int(binary.BigEndian.Uint16(packet[offset+2:]))
		padded := (length + 3) & ^3
		if length < 4 || offset+padded > len(packet) {
			return packet, false
		}
		if packet[offset] == 192 && length >= 12 {
			attempt := c.forwardMutations.Add(1)
			if attempt > 2 {
				return packet, false
			}
			altered := append([]byte(nil), packet...)
			if attempt == 1 {
				// Duplicate one stream/SSN entry while keeping framing and CRC valid.
				altered = append(altered[:offset+length:offset+length], packet[offset+8:offset+12]...)
				altered = append(altered, packet[offset+padded:]...)
				binary.BigEndian.PutUint16(altered[offset+2:], uint16(length+4))
			} else {
				binary.BigEndian.PutUint32(altered[offset+4:], binary.BigEndian.Uint32(packet[offset+4:])+1000000)
			}
			for i := 8; i < 12; i++ {
				altered[i] = 0
			}
			binary.LittleEndian.PutUint32(altered[8:], crc32.Checksum(altered, crc32.MakeTable(crc32.Castagnoli)))
			return altered, true
		}
		offset += padded
	}
	return packet, false
}

func (c *sctpLossConnection) malformedReset(packet []byte) []byte {
	if !c.corruptReset {
		return packet
	}
	for offset := 12; offset+4 <= len(packet); {
		length := int(binary.BigEndian.Uint16(packet[offset+2:]))
		padded := (length + 3) &^ 3
		if length < 4 || offset+padded > len(packet) {
			return packet
		}
		// One-ID reset parameter: its alignment padding can hold a duplicate ID.
		if packet[offset] == 130 && (length == 22 || length == 24) && binary.BigEndian.Uint16(packet[offset+4:]) == 13 && binary.BigEndian.Uint16(packet[offset+6:]) == 18 {
			attempt := c.resetMutations.Add(1)
			if attempt > 2 {
				return packet
			}
			altered := append([]byte(nil), packet...)
			if attempt == 1 {
				binary.BigEndian.PutUint16(altered[offset+2:], 24)
				binary.BigEndian.PutUint16(altered[offset+6:], 20)
				copy(altered[offset+22:offset+24], packet[offset+20:offset+22])
			} else {
				binary.BigEndian.PutUint16(altered[offset+20:], 65535)
			}
			for i := 8; i < 12; i++ {
				altered[i] = 0
			}
			binary.LittleEndian.PutUint32(altered[8:], crc32.Checksum(altered, crc32.MakeTable(crc32.Castagnoli)))
			return altered
		}
		offset += padded
	}
	return packet
}

// Hold one successful reset result until after the next OPEN is written.
// This deliberately reorders the result behind the peer's next generation without
// retrying an old request against the independent peer's already reused stream.
func (c *sctpLossConnection) holdResetResult(packet []byte) bool {
	if !c.reorderResetResult || c.resetResultHeld {
		return false
	}
	for offset := 12; offset+4 <= len(packet); {
		length := int(binary.BigEndian.Uint16(packet[offset+2:]))
		padded := (length + 3) &^ 3
		if length < 4 || offset+padded > len(packet) {
			return false
		}
		if packet[offset] == 130 {
			for p := offset + 4; p+4 <= offset+length; {
				n := int(binary.BigEndian.Uint16(packet[p+2:]))
				span := (n + 3) &^ 3
				if n < 4 || p+n > offset+length {
					return false
				}
				if binary.BigEndian.Uint16(packet[p:]) == 16 && n == 12 && binary.BigEndian.Uint32(packet[p+8:]) == 1 {
					c.resetResultHeld = true
					c.heldResetResult = append([]byte(nil), packet...)
					return true
				}
				p += span
			}
		}
		offset += padded
	}
	return false
}

func (c *sctpLossConnection) Write(packet []byte) (int, error) {
	c.writeGate.Lock()
	defer c.writeGate.Unlock()
	if c.holdResetResult(packet) {
		return len(packet), nil
	}
	originalLength := len(packet)
	packet = c.malformedReset(packet)
	if !c.dropEnabled || c.incoming {
		n, err := c.Conn.Write(packet)
		if err == nil && n != len(packet) {
			err = io.ErrShortWrite
		}
		if err != nil {
			return 0, err
		}
		if len(c.heldResetResult) != 0 && hasDcepOpen(packet) {
			held := c.heldResetResult
			c.heldResetResult = nil
			n, err = c.Conn.Write(held)
			if err == nil && n != len(held) {
				err = io.ErrShortWrite
			}
			if err != nil {
				return 0, err
			}
		}
		return originalLength, nil
	}
	kept, dropped := stripLostMessage(packet)
	mutated, changed := c.malformedForward(kept)
	if !dropped && !changed {
		return c.Conn.Write(packet)
	}
	kept = mutated
	if len(kept) == 0 {
		return len(packet), nil
	}
	n, err := c.Conn.Write(kept)
	if err != nil {
		return 0, err
	}
	if n != len(kept) {
		return 0, io.ErrShortWrite
	}
	return len(packet), nil
}

func hasDcepOpen(packet []byte) bool {
	for offset := 12; offset+4 <= len(packet); {
		length := int(binary.BigEndian.Uint16(packet[offset+2:]))
		padded := (length + 3) &^ 3
		if length < 4 || offset+padded > len(packet) {
			return false
		}
		if packet[offset] == 0 && length >= 17 && binary.BigEndian.Uint32(packet[offset+12:]) == 50 && packet[offset+16] == 3 {
			return true
		}
		offset += padded
	}
	return false
}
