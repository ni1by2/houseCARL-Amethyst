FROM ubuntu:24.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    python3 python3-msgpack python3-xxhash python3-yaml ca-certificates \
    libicu74 libssl3t64 libgssapi-krb5-2 libstdc++6 zlib1g \
    && rm -rf /var/lib/apt/lists/*
