FROM ubuntu:24.04 AS runtime

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates libatomic1 libgcc-s1 libstdc++6 zlib1g \
        libgssapi-krb5-2 libssl3t64 \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 vatican \
    && useradd --uid 10001 --gid 10001 --home-dir /var/lib/vatican --no-create-home vatican \
    && mkdir -p /var/lib/vatican/config /var/lib/vatican/pending-results /opt/server \
    && chown -R 10001:10001 /var/lib/vatican

ENV XDG_CONFIG_HOME=/var/lib/vatican/config \
    VD_RUN_RESULT_DIR=/var/lib/vatican/pending-results
WORKDIR /opt/server
COPY --chmod=0555 entrypoint.sh /usr/local/bin/vatican-server
USER 10001:10001
EXPOSE 7979/udp
STOPSIGNAL SIGTERM
ENTRYPOINT ["/usr/local/bin/vatican-server"]
CMD ["-vdBind", "0.0.0.0", "-vdPort", "7979", "-vdDtls"]

FROM runtime AS server
ARG TARGETARCH
USER root
COPY build/linux-server/ /opt/server/
# Reject desktop/foreign-platform or incomplete payloads instead of shipping an empty image.
RUN test "$TARGETARCH" = amd64 \
    && test -f server-build.json \
    && grep -q '"target": "StandaloneLinux64"' server-build.json \
    && grep -q '"subtarget": "Server"' server-build.json \
    && test "$(od -An -tx1 -N5 VaticanDefendorServer.x86_64 | tr -d ' \n')" = 7f454c4602 \
    && test "$(od -An -tx1 -j18 -N2 VaticanDefendorServer.x86_64 | tr -d ' \n')" = 3e00 \
    && test -f UnityPlayer.so \
    && test -f VaticanDefendorServer_Data/boot.config \
    && chmod 0555 VaticanDefendorServer.x86_64 \
    && ldd VaticanDefendorServer.x86_64 > /tmp/server-libraries \
    && ldd UnityPlayer.so >> /tmp/server-libraries \
    && cat /tmp/server-libraries \
    && ! grep -q 'not found' /tmp/server-libraries
USER 10001:10001
