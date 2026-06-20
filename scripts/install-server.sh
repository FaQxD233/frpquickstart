#!/usr/bin/env bash
set -euo pipefail

REPO="${FRPQUICK_REPO:-FaQxD233/frpquickstart}"
VERSION="${FRPQUICK_VERSION:-latest}"
INSTALL_DIR="${FRPQUICK_INSTALL_DIR:-/opt/frpquickstart/server}"
SERVICE_NAME="${FRPQUICK_SERVICE_NAME:-frpquickstart}"
TLS_MODE="${FRPQUICK_TLS:-acme}"
FRP_TRANSPORT="${FRPQUICK_FRP_TRANSPORT:-tcp}"
ACME_ID="${FRPQUICK_ACME_ID:-}"
ACME_EMAIL="${FRPQUICK_ACME_EMAIL:-}"
NO_SERVICE=0

usage() {
  cat <<'EOF'
Usage:
  install-server.sh [options]

Options:
  --version <tag>        GitHub release tag. Default: latest
  --repo <owner/repo>    GitHub repository. Default: FaQxD233/frpquickstart
  --install-dir <path>   Install directory. Default: /opt/frpquickstart/server
  --service-name <name>  systemd service name. Default: frpquickstart
  --tls <mode>           acme, self-signed, or none. Default: acme
  --frp-transport <p>    frpc-to-frps transport: tcp, websocket, wss, kcp, or quic. Default: tcp
  --acme-id <ip/domain>  ACME identifier. Default: public IPv4 when --tls acme
  --acme-email <email>   ACME account email. Default: admin@<public-ip>.sslip.io
  --no-service           Install binary only, do not create/start systemd service
  -h, --help             Show help

Example:
  curl -fsSL https://raw.githubusercontent.com/FaQxD233/frpquickstart/main/scripts/install-server.sh \
    | sudo bash -s -- --tls acme --acme-id 1.2.3.4 --acme-email admin@example.com
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --install-dir) INSTALL_DIR="$2"; shift 2 ;;
    --service-name) SERVICE_NAME="$2"; shift 2 ;;
    --tls) TLS_MODE="$2"; shift 2 ;;
    --frp-transport) FRP_TRANSPORT="$2"; shift 2 ;;
    --acme-id) ACME_ID="$2"; shift 2 ;;
    --acme-email) ACME_EMAIL="$2"; shift 2 ;;
    --no-service) NO_SERVICE=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
  esac
done

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "This installer is for Linux x64 only." >&2
  exit 1
fi

if [[ "$(uname -m)" != "x86_64" ]]; then
  echo "Unsupported architecture: $(uname -m). Only x86_64 is packaged." >&2
  exit 1
fi

if [[ "$EUID" -ne 0 && "$NO_SERVICE" -eq 0 ]]; then
  echo "Run with sudo to install the systemd service, or pass --no-service." >&2
  exit 1
fi

need_cmd() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing command: $1" >&2
    exit 1
  fi
}

need_cmd curl
need_cmd tar

public_ipv4() {
  curl -fsS4 --max-time 8 https://ip.sb || curl -fsS4 --max-time 8 https://ifconfig.me
}

if [[ "$TLS_MODE" == "acme" ]]; then
  if [[ -z "$ACME_ID" ]]; then
    ACME_ID="$(public_ipv4)"
  fi

  if [[ -z "$ACME_EMAIL" ]]; then
    safe_id="${ACME_ID//:/-}"
    ACME_EMAIL="admin@${safe_id}.sslip.io"
  fi

  if [[ ! -x "$HOME/.acme.sh/acme.sh" && ! -x "/root/.acme.sh/acme.sh" ]]; then
    curl https://get.acme.sh | sh -s "email=$ACME_EMAIL"
  fi
fi

asset="frpquick-server-linux-x64.tar.gz"
if [[ "$VERSION" == "latest" ]]; then
  url="https://github.com/$REPO/releases/latest/download/$asset"
else
  url="https://github.com/$REPO/releases/download/$VERSION/$asset"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $url"
curl -fL --retry 5 --retry-delay 2 -o "$tmp/$asset" "$url"

mkdir -p "$INSTALL_DIR"
tar -xzf "$tmp/$asset" -C "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/frpquick-server"

if [[ "$NO_SERVICE" -eq 1 ]]; then
  echo "Installed: $INSTALL_DIR/frpquick-server"
  exit 0
fi

need_cmd systemctl

exec_args="--tls $TLS_MODE --frp-transport $FRP_TRANSPORT"
if [[ "$TLS_MODE" == "acme" ]]; then
  exec_args="$exec_args --acme-id $ACME_ID --acme-email $ACME_EMAIL"
fi

cat >"/etc/systemd/system/$SERVICE_NAME.service" <<EOF
[Unit]
Description=FRP QuickStart Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/frpquick-server $exec_args
Restart=on-failure
RestartSec=5s

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"

echo "Service started: $SERVICE_NAME"
echo "Status: systemctl status $SERVICE_NAME --no-pager"
echo "Logs:   journalctl -u $SERVICE_NAME -f"
