FROM golang:1.26-bookworm@sha256:a688600ca24f8a4d3ca77f95b0dd40704a9fc787c826660eb7ba0b641b8b175d AS peer
WORKDIR /peer
COPY tests/interop/Pion/go.mod tests/interop/Pion/go.sum ./
RUN go mod download
COPY tests/interop/Pion/main.go tests/interop/Pion/srtp.go tests/interop/Pion/dtls.go tests/interop/Pion/channels.go tests/interop/Pion/write-notices.sh tests/interop/Pion/THIRD_PARTY_NOTICES.txt ./
RUN cp THIRD_PARTY_NOTICES.txt /tmp/expected-notices.txt && sh write-notices.sh && cmp /tmp/expected-notices.txt THIRD_PARTY_NOTICES.txt && CGO_ENABLED=0 go build -trimpath -o /pion-peer .

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble-aot
WORKDIR /workspace
COPY . .
COPY --from=peer /pion-peer /usr/local/bin/pion-peer
RUN dotnet build WebRTC.slnx -c Release
CMD ["sh", "-c", "pion-peer & peer_pid=$!; trap 'kill $peer_pid 2>/dev/null || true' EXIT; dotnet run --project src/tests/WebRTC.IntegrationTests -c Release --no-build -- --pion-uri http://127.0.0.1:8080"]
