# Turian's CI image: Ubuntu + Zig + the handful of tools `zig build`/`zig build ci`
# shell out to (curl to fetch Zig itself, git-lfs for the binary test fixtures,
# zip for release packaging). Every CI job in .gitlab-ci.yml pulls this instead
# of apt-get-installing and downloading Zig on every run.
#
# Rebuilt by the `build_ci_image` job only when this file, or ZIG_VERSION in
# .gitlab-ci.yml, changes — see that job's `rules: changes:`.
FROM ubuntu:26.04

ARG ZIG_VERSION=0.16.0

RUN apt-get update -qq \
    && apt-get install -y -qq --no-install-recommends ca-certificates curl xz-utils git git-lfs zip \
    && rm -rf /var/lib/apt/lists/* \
    && curl -fsSL "https://ziglang.org/download/${ZIG_VERSION}/zig-x86_64-linux-${ZIG_VERSION}.tar.xz" \
       | tar -xJ --strip-components=1 -C /usr/local/bin \
         "zig-x86_64-linux-${ZIG_VERSION}/zig" "zig-x86_64-linux-${ZIG_VERSION}/lib" \
    && zig version
