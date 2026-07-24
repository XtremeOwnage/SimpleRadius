#!/usr/bin/env bash
# Installs a SimpleRadius release tarball as a systemd service.
#
#   curl -fsSL -o simpleradius.tar.gz https://github.com/XtremeOwnage/SimpleRadius/releases/latest/download/simpleradius-linux-x64.tar.gz
#   tar xzf simpleradius.tar.gz && cd simpleradius-*/
#   sudo ./deploy/systemd/install.sh
#
# Re-running upgrades in place and leaves the database and configuration untouched.

set -euo pipefail

INSTALL_DIR=${INSTALL_DIR:-/opt/simpleradius}
CONFIG_DIR=${CONFIG_DIR:-/etc/simpleradius}
STATE_DIR=${STATE_DIR:-/var/lib/simpleradius}
SERVICE_USER=${SERVICE_USER:-simpleradius}

if [[ $EUID -ne 0 ]]; then
    echo "This script needs root: sudo $0" >&2
    exit 1
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
payload_dir=${PAYLOAD_DIR:-$(cd -- "$script_dir/../.." && pwd)}

if [[ ! -f "$payload_dir/SimpleRadius" && ! -f "$payload_dir/SimpleRadius.dll" ]]; then
    echo "No SimpleRadius binary found in $payload_dir." >&2
    echo "Run this from an extracted release tarball, or set PAYLOAD_DIR." >&2
    exit 1
fi

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    echo "Creating service account $SERVICE_USER"
    useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

echo "Installing to $INSTALL_DIR"
install -d -o root -g root -m 0755 "$INSTALL_DIR"
# --delete keeps an upgrade from leaving stale assemblies behind.
if command -v rsync >/dev/null 2>&1; then
    rsync -a --delete --exclude 'data' --exclude 'logs' "$payload_dir"/ "$INSTALL_DIR"/
else
    rm -rf "${INSTALL_DIR:?}"/*
    cp -a "$payload_dir"/. "$INSTALL_DIR"/
fi
chmod +x "$INSTALL_DIR/SimpleRadius" 2>/dev/null || true

install -d -o "$SERVICE_USER" -g "$SERVICE_USER" -m 0750 "$STATE_DIR"
install -d -o root -g root -m 0755 "$CONFIG_DIR"

if [[ ! -f "$CONFIG_DIR/simpleradius.env" ]]; then
    install -o root -g "$SERVICE_USER" -m 0640 \
        "$script_dir/simpleradius.env.example" "$CONFIG_DIR/simpleradius.env"
    echo "Wrote $CONFIG_DIR/simpleradius.env - review it before going live."
else
    echo "Keeping existing $CONFIG_DIR/simpleradius.env"
fi

install -m 0644 "$script_dir/simpleradius.service" /etc/systemd/system/simpleradius.service
systemctl daemon-reload
systemctl enable --now simpleradius.service

echo
echo "Installed. Useful commands:"
echo "  systemctl status simpleradius"
echo "  journalctl -u simpleradius -f"
echo
echo "The admin UI has no authentication by default. Keep it on a trusted network,"
echo "or set Authentication__Mode=Oidc in $CONFIG_DIR/simpleradius.env."
