// Authored SDK metadata fixture. Generates real Clang module debug data only;
// its object is never linked into WebRTC or substituted for framework code.
import CryptoKit
import Foundation

public func moduleSymbolProbe(_ data: Data) -> Int {
    SymmetricKey(data: data).bitCount
}
