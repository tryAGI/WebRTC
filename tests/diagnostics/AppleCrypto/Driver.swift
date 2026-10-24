// Authored diagnostic driver. The two builds link the same optimized driver
// object against unchanged upstream bridge source compiled -Onone or -O.
// All pointers borrow local arrays synchronously; no pointer escapes a closure.
import CryptoKit
import Foundation

@_silgen_name("AppleCryptoNative_AesGcmDecrypt")
func bridgeDecrypt(key: UnsafeBufferPointer<UInt8>, nonceData: UnsafeBufferPointer<UInt8>,
                   cipherText: UnsafeBufferPointer<UInt8>, tag: UnsafeBufferPointer<UInt8>,
                   plaintext: UnsafeMutableBufferPointer<UInt8>, aad: UnsafeBufferPointer<UInt8>) throws

@_silgen_name("AppleCryptoNative_IsAuthenticationFailure")
func bridgeAuthenticationFailure(error: Error) -> Bool

enum ProbeError: Error { case integrity, invalidArguments }

@main
struct Driver {
    static func main() throws {
        guard CommandLine.arguments.count == 2,
              ["Onone", "O"].contains(CommandLine.arguments[1]) else { throw ProbeError.invalidArguments }
        let optimization = CommandLine.arguments[1]
        print("optimization,key_bytes,payload_bytes,mode,sample,operations,ns_per_op,accepted,rejected")
        for keyLength in [16, 32] {
            for payloadLength in [0, 60, 1200] {
                // Fixed synthetic fixture, decrypt-only measurement. Nonce reuse
                // occurs only for repeated opening of this single sealed box.
                let key = [UInt8](repeating: 0x52, count: keyLength)
                let nonce = [UInt8](repeating: 0x31, count: 12)
                let plaintext = [UInt8](repeating: 0x27, count: payloadLength)
                let aad = [UInt8](repeating: 0x80, count: 12)
                let box = try AES.GCM.seal(plaintext, using: SymmetricKey(data: key),
                    nonce: AES.GCM.Nonce(data: nonce), authenticating: aad)
                let ciphertext = Array(box.ciphertext)
                for mode in ["valid", "bad_tag"] {
                    var tag = Array(box.tag)
                    if mode == "bad_tag" { tag[0] ^= 1 }
                    var output = [UInt8](repeating: 0xa5, count: payloadLength)
                    // Borrow once for a complete cohort: pointer closure overhead
                    // and driver optimization are identical in the two binaries.
                    try key.withUnsafeBufferPointer { keyBuffer in
                        try nonce.withUnsafeBufferPointer { nonceBuffer in
                            try ciphertext.withUnsafeBufferPointer { cipherBuffer in
                                try tag.withUnsafeBufferPointer { tagBuffer in
                                    try aad.withUnsafeBufferPointer { aadBuffer in
                                        try output.withUnsafeMutableBufferPointer { outputBuffer in
                                            func exercise(_ count: Int) throws -> (Int, Int) {
                                                var accepted = 0; var rejected = 0
                                                for _ in 0..<count {
                                                    do {
                                                        try bridgeDecrypt(key: keyBuffer, nonceData: nonceBuffer,
                                                            cipherText: cipherBuffer, tag: tagBuffer,
                                                            plaintext: outputBuffer, aad: aadBuffer)
                                                        accepted += 1
                                                    } catch {
                                                        guard bridgeAuthenticationFailure(error: error) else { throw error }
                                                        rejected += 1
                                                    }
                                                }
                                                return (accepted, rejected)
                                            }
                                            _ = try exercise(1000)
                                            for sample in 0..<7 {
                                                let start = DispatchTime.now().uptimeNanoseconds
                                                let (accepted, rejected) = try exercise(5000)
                                                let elapsed = DispatchTime.now().uptimeNanoseconds - start
                                                if mode == "valid" {
                                                    guard accepted == 5000, rejected == 0,
                                                          outputBuffer.elementsEqual(plaintext) else { throw ProbeError.integrity }
                                                } else {
                                                    guard accepted == 0, rejected == 5000,
                                                          outputBuffer.allSatisfy({ $0 == 0xa5 }) else { throw ProbeError.integrity }
                                                }
                                                print("\(optimization),\(keyLength),\(payloadLength),\(mode),\(sample),5000,\(Double(elapsed) / 5000),\(accepted),\(rejected)")
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}
