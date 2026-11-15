FROM golang:1.23-alpine AS build
WORKDIR /src
COPY go.mod main.go ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /conn-soak .

FROM alpine:3.20
COPY --from=build /conn-soak /usr/local/bin/conn-soak
USER nobody
ENTRYPOINT ["conn-soak"]
