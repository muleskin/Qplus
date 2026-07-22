#!/usr/bin/env bash
#
# Install the .NET SDK needed to build Qplus on Linux.
#
# The version is read from global.json so this script can never drift from the pin.
# That pin is currently a preview build; preview builds are eventually removed from the
# CDN, so if the exact version is gone we fall back to the newest SDK that still
# satisfies global.json (rollForward: latestMinor accepts any newer .NET 10 SDK).
#
#   ./scripts/install-dotnet.sh                 install system-wide (needs root) or to ~/.dotnet
#   sudo ./scripts/install-dotnet.sh --verify   install, then build the server to prove it works
#   ./scripts/install-dotnet.sh --user          force a per-user install
#   ./scripts/install-dotnet.sh --version 10.0.100
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

# ---------------------------------------------------------------- pinned version

read_pinned_version() {
    [ -f "$GLOBAL_JSON" ] || return 1
    # Avoid a jq dependency: pull the first "version" value out of the sdk block.
    sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$GLOBAL_JSON" | head -1
}

if [ -z "$VERSION" ] && [ -z "$CHANNEL" ]; then
    VERSION="$(read_pinned_version || true)"
    if [ -n "$VERSION" ]; then
        say "global.json pins SDK $VERSION"
    else
        warn "could not read a version from $GLOBAL_JSON — falling back to channel 10.0"
        CHANNEL="10.0"
    fi
fi

# Major version the repo needs, used for the "already installed?" check below.
MAJOR="${VERSION%%.*}"; [ -n "$MAJOR" ] || MAJOR="${CHANNEL%%.*}"; [ -n "$MAJOR" ] || MAJOR=10

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

say "installing to $INSTALL_DIR ($SCOPE)"

# ---------------------------------------------------------------- already there?

sdk_present() {
    local dotnet="$1"
    [ -x "$dotnet" ] || return 1
    "$dotnet" --list-sdks 2>/dev/null | grep -q "^${MAJOR}\." || return 1
    return 0
}

for candidate in "$INSTALL_DIR/dotnet" "$(command -v dotnet 2>/dev/null || true)"; do
    if [ -n "$candidate" ] && sdk_present "$candidate"; then
        say "a .NET $MAJOR SDK is already installed:"
        "$candidate" --list-sdks | sed 's/^/    /'
        if [ "$DO_VERIFY" -eq 0 ]; then
            say "nothing to do (pass --verify to build the server as a check)"
            exit 0
        fi
        DOTNET="$candidate"
        break
    fi
done

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

if [ -z "${DOTNET:-}" ]; then
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

    install_attempt() {
        # shellcheck disable=SC2086
        "$INSTALLER" --install-dir "$INSTALL_DIR" --no-path "$@"
    }

    installed=0
    if [ -n "$VERSION" ]; then
        say "installing SDK $VERSION"
        if install_attempt --version "$VERSION"; then
            installed=1
        else
            warn "SDK $VERSION is not available on the CDN (preview builds get retired)"
        fi
    fi

    if [ "$installed" -eq 0 ]; then
        # global.json uses rollForward: latestMinor, so any newer .NET $MAJOR SDK satisfies it.
        fallback_channel="${CHANNEL:-$MAJOR.0}"
        say "falling back to the newest SDK on channel $fallback_channel"
        if install_attempt --channel "$fallback_channel" --quality preview; then
            installed=1
        elif install_attempt --channel "$fallback_channel"; then
            installed=1
        fi
    fi

    [ "$installed" -eq 1 ] || die "could not install a .NET $MAJOR SDK"
    DOTNET="$INSTALL_DIR/dotnet"
fi

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

export DOTNET_ROOT="$INSTALL_DIR"
export PATH="$PATH:$INSTALL_DIR"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# ---------------------------------------------------------------- verify

say "installed SDKs:"
"$DOTNET" --list-sdks | sed 's/^/    /'

say "global.json resolves to: $("$DOTNET" --version 2>/dev/null || echo '(global.json not satisfied)')"

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
