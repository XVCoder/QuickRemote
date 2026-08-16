FROM --platform=$BUILDPLATFORM golang:1.22-alpine AS builder

ARG TARGETOS
ARG TARGETARCH

WORKDIR /build

COPY go.mod go.sum ./
RUN go mod download

COPY . .

RUN CGO_ENABLED=0 GOOS=$TARGETOS GOARCH=$TARGETARCH go build -ldflags="-s -w" -o quickremote-relay ./cmd/server

FROM alpine:latest

RUN apk --no-cache add ca-certificates tzdata

WORKDIR /app
COPY --from=builder /build/quickremote-relay .
COPY --from=builder /build/config.example.yaml .

RUN mkdir -p /etc/quickremote /var/lib/quickremote

EXPOSE 8443 8444

ENTRYPOINT ["./quickremote-relay"]
CMD ["/etc/quickremote/config.yaml"]
