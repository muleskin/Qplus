#!/usr/bin/env bash
#
# Install a .NET SDK capable of building Qplus on Linux.
#
# Whether an SDK is acceptable is decided by asking dotnet itself — `dotnet --version`
# run from the repository root succeeds only if some installed SDK satisfies global.json.
# That is the authoritative check; guessing from version numbers is not, because
# roll-forward only ever moves *forward*: an SDK older than the pinned version can never
# satisfy it, however new its major version looks.
#
#   ./scripts/install-dotnet.sh                 install system-wide (needs root) or to ~/.dotnet
#   sudo ./scripts/install-dotnet.sh --verify   install, then build the server to prove it works
#   ./scripts/install-dotnet.sh --user          force a per-user install
#   ./scripts/install-dotnet.sh --version 10.0.100
#   ./scripts/install-dotnet.sh --channel 8.0
#
set -euo pipefail

# ---------------------------------------------------------------- settings

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
GLOBAL_JSON="$REPO_ROOT/global.json"

INSTALLER_URL="https://dot.net/v1/dotnet-install.sh"
SYSTEM_DIR="/usr/local/share/dotnet"
USER_DIR="$HOME/.dotnet"
PROFILE_D="/etc/profile.d/dotnet.sh"

# Channel used when global.json names no usable version.
DEFAULT_CHANNEL="8.0"

VERSION=""          # --version
CHANNEL=""          # --channel
SCOPE=""            # --system | --user
DO_PREREQS=1
DO_VERIFY=0

# ---------------------------------------------------------------- helpers

say()  { printf '\033[1;36m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

usage() {
    # Print the header comment block, stopping at the first line of real code,
    # so the help text can never drift from the comment above.
    awk 'NR>1 { if (/^#/) { sub(/^# ?/, ""); print } else { exit } }' "${BASH_SOURCE[0]}"
    exit 0
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version)  VERSION="${2:-}"; shift 2 ;;
        --channel)  CHANNEL="${2:-}"; shift 2 ;;
        --system)   SCOPE=system; shift ;;
        --user)     SCOPE=user; shift ;;
        --no-prereqs) DO_PREREQS=0; shift ;;
        --verify)   DO_VERIFY=1; shift ;;
        -h|--help)  usage ;;
        *) die "unknown option: $1 (try --help)" ;;
    esac
done

# ---------------------------------------------------------------- what does the repo ask for?

read_pinned_version() {
    [ -f "$GLOBAL_JSON" ] || return 1
    # Avoid a jq dependency: pull the first "version" value out of the sdk block.
    sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$GLOBAL_JSON" | head -1
}

PINNED="$(read_pinned_version || true)"
[ -n "$PINNED" ] && say "global.json asks for SDK $PINNED (plus whatever rollForward allows)"

# The version we will try to install, if we need to install anything at all.
WANTED="${VERSION:-$PINNED}"

# ---------------------------------------------------------------- is one already good enough?

# Authoritative: dotnet resolves global.json itself.
satisfies_global_json() {
    local dotnet="$1"
    [ -x "$dotnet" ] || command -v "$dotnet" >/dev/null 2>&1 || return 1
    ( cd "$REPO_ROOT" && "$dotnet" --version >/dev/null 2>&1 )
}

DOTNET=""
for candidate in "$(command -v dotnet 2>/dev/null || true)" "$SYSTEM_DIR/dotnet" "$USER_DIR/dotnet"; do
    [ -n "$candidate" ] || continue
    if satisfies_global_json "$candidate"; then
        DOTNET="$candidate"
        say "an installed SDK already satisfies global.json:"
        ( cd "$REPO_ROOT" && "$DOTNET" --version | sed 's/^/    /' )
        break
    fi
done

if [ -n "$DOTNET" ] && [ "$DO_VERIFY" -eq 0 ] && [ -z "$VERSION" ]; then
    say "nothing to install (pass --verify to build the server as a check)"
    exit 0
fi

# ---------------------------------------------------------------- scope

if [ -z "$SCOPE" ]; then
    if [ "$(id -u)" -eq 0 ]; then SCOPE=system; else SCOPE=user; fi
fi

if [ "$SCOPE" = system ]; then
    [ "$(id -u)" -eq 0 ] || die "--system needs root. Re-run with sudo, or use --user."
    INSTALL_DIR="$SYSTEM_DIR"
else
    INSTALL_DIR="$USER_DIR"
fi

# ---------------------------------------------------------------- prerequisites

install_prereqs() {
    [ "$DO_PREREQS" -eq 1 ] || return 0
    if [ "$(id -u)" -ne 0 ]; then
        warn "not root — skipping prerequisite packages (ICU, OpenSSL). Install them yourself if the build complains."
        return 0
    fi

    # .NET needs ICU for globalization and OpenSSL for TLS. Package names differ per distro.
    if command -v apt-get >/dev/null 2>&1; then
        say "installing prerequisites (apt)"
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq
        apt-get install -y -qq --no-install-recommends ca-certificates curl libicu-dev libssl-dev tar >/dev/null
    elif command -v dnf >/dev/null 2>&1; then
        say "installing prerequisites (dnf)"
        dnf install -y -q ca-certificates curl libicu openssl-libs tar >/dev/null
    elif command -v yum >/dev/null 2>&1; then
        say "installing prerequisites (yum)"
        yum install -y -q ca-certificates curl libicu openssl-libs tar >/dev/null
    elif command -v zypper >/dev/null 2>&1; then
        say "installing prerequisites (zypper)"
        zypper --non-interactive --quiet install ca-certificates curl libicu openssl tar >/dev/null
    elif command -v apk >/dev/null 2>&1; then
        # Alpine uses musl; Microsoft's builds need the musl RID and these extras.
        say "installing prerequisites (apk)"
        apk add --no-cache ca-certificates curl icu-libs libssl3 libstdc++ tar >/dev/null
    else
        warn "unrecognised package manager — install ICU and OpenSSL yourself if the build complains"
    fi
}

# ---------------------------------------------------------------- install

if [ -z "$DOTNET" ] || [ -n "$VERSION" ]; then
    say "installing to $INSTALL_DIR ($SCOPE)"
    install_prereqs

    command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1 \
        || die "need curl or wget to download the installer"

    TMP="$(mktemp -d)"
    trap 'rm -rf "$TMP"' EXIT
    INSTALLER="$TMP/dotnet-install.sh"

    say "fetching the official installer from $INSTALLER_URL"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --proto '=https' --tlsv1.2 "$INSTALLER_URL" -o "$INSTALLER"
    else
        wget -q --https-only -O "$INSTALLER" "$INSTALLER_URL"
    fi
    [ -s "$INSTALLER" ] || die "downloaded installer is empty"
    chmod +x "$INSTALLER"

    attempt() { "$INSTALLER" --install-dir "$INSTALL_DIR" --no-path "$@"; }

    installed=0
    if [ -n "$WANTED" ]; then
        say "installing SDK $WANTED"
        if attempt --version "$WANTED"; then
            installed=1
        else
            warn "SDK $WANTED is not available (preview builds are eventually retired)"
        fi
    fi

    if [ "$installed" -eq 0 ]; then
        fallback="${CHANNEL:-${WANTED%%.*}.0}"
        [ "$fallback" = ".0" ] && fallback="$DEFAULT_CHANNEL"
        say "installing the newest SDK on channel $fallback instead"
        attempt --channel "$fallback" --quality preview || attempt --channel "$fallback" || true
        # Last resort: whatever the default channel offers.
        [ -x "$INSTALL_DIR/dotnet" ] || attempt --channel "$DEFAULT_CHANNEL" || true
    fi

    [ -x "$INSTALL_DIR/dotnet" ] || die "could not install a .NET SDK"
    DOTNET="$INSTALL_DIR/dotnet"

    # ---------------------------------------------------------------- PATH
    if [ "$SCOPE" = system ]; then
        say "adding $INSTALL_DIR to PATH for all users ($PROFILE_D)"
        cat > "$PROFILE_D" <<EOF
# Added by Qplus scripts/install-dotnet.sh
export DOTNET_ROOT=$INSTALL_DIR
export PATH="\$PATH:$INSTALL_DIR:$INSTALL_DIR/tools"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
EOF
        chmod 0644 "$PROFILE_D"
        ln -sf "$INSTALL_DIR/dotnet" /usr/local/bin/dotnet
        say "linked /usr/local/bin/dotnet"
    else
        say "add this to your shell profile:"
        printf '\n    export DOTNET_ROOT=%s\n    export PATH="$PATH:%s"\n\n' "$INSTALL_DIR" "$INSTALL_DIR"
    fi
fi

export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$DOTNET")}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# ---------------------------------------------------------------- verify

say "installed SDKs:"
"$DOTNET" --list-sdks | sed 's/^/    /'

if satisfies_global_json "$DOTNET"; then
    say "global.json resolves to $( cd "$REPO_ROOT" && "$DOTNET" --version )"
else
    warn "no installed SDK satisfies $GLOBAL_JSON"
    warn "roll-forward never selects an SDK older than the pinned version, so installing a"
    warn "lower version will not help. Either install the pinned SDK, or relax global.json."
    exit 1
fi

if [ "$DO_VERIFY" -eq 1 ]; then
    say "building the query server as a check"
    ( cd "$REPO_ROOT" && "$DOTNET" build src/Qplus.Server/Qplus.Server.csproj -c Release )
    say "build succeeded"
fi

cat <<EOF

Done. Next steps:

    cd src/Qplus.Server
    make publish          # single self-contained binary -> ./out/Qplus.Server
    sudo make service     # install and start it

If 'dotnet' is not found in a new shell, open a new login session (or 'source $PROFILE_D').
EOF
