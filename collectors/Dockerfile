# Reference collector wrapper image. Small, non-root, and carries NO .NET runtime: the transport is
# curl and the only validation a shell can do (syntactic JSON + a collector_id assertion) is jq. Real
# vendor-specific collector scripts and their image build inputs stay Ologist-side; this image just
# proves the ingest flow and gives those scripts a runnable base. Bake NO credentials: configuration is
# supplied at runtime via environment variables (see collectors/README.md).
FROM alpine:3.20

RUN apk add --no-cache curl jq \
    && addgroup -S collector \
    && adduser -S -G collector collector

WORKDIR /collector

COPY entrypoint.sh /usr/local/bin/entrypoint.sh
COPY example/ /collector/

RUN chmod +x /usr/local/bin/entrypoint.sh /collector/collect.sh

USER collector

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
