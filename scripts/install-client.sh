#!/usr/bin/env bash
set -euo pipefail

REPO="${FRPQUICK_REPO:-FaQxD233/frpquickstart}"
VERSION="${FRPQUICK_VERSION:-latest}"
INSTALL_DIR="${FRPQUICK_INSTALL_DIR:-$HOME/.local/share/frpquickstart/client}"
BIN_DIR="${FRPQUICK_BIN_DIR:-$HOME/.local/bin}"
SERVICE_NAME="${FRPQUICK_SERVICE_NAME:-frpquick-client}"
SHARE=""
SERVER=""
CONTROL_PORT=""
SECRET=""
TLS_MODE=""
TLS_FINGERPRINT=""
FRP_TRANSPORT=""
REMOTE_PORT=""
LOCAL_IP="127.0.0.1"
LOCAL_PORT=""
PROTOCOL="tcp"
CREATE_SERVICE=0
RUN_NOW=0

usage() {
  cat <<'EOF'
Usage:
  install-client.sh [options]

Options:
  --version <tag>            GitHub release tag. Default: latest
  --repo <owner/repo>        GitHub repository. Default: FaQxD233/frpquickstart
  --install-dir <path>       Install directory. Default: ~/.local/share/frpquickstart/client
  --share <frpquick-url>     Server share link
  --server <host>            Server host when not using --share
  --control-port <port>      Control port. Default comes from client
  --secret <secret>          API secret when not using --share
  --tls <mode>               acme, self-signed, or none
  --tls-fingerprint <fp>     Required for self-signed TLS
  --frp-transport <protocol> frpc-to-frps transport: tcp, websocket, wss, kcp, or quic
  --remote-port <port>       Public port on server
  --local-ip <ip>            Local service IP. Default: 127.0.0.1
  --local-port <port>        Local service port
  --protocol <tcp|udp>       Default: tcp
  --service                  Create and start a user systemd service
  --run-now                  Run the tunnel command once after installation
  -h, --help                 Show help

Example:
  curl -fsSL https://raw.githubusercontent.com/FaQxD233/frpquickstart/main/scripts/install-client.sh \
    | bash -s -- --share 'frpquick://1.2.3.4:9080/?tls=acme&transport=tcp&secret=...' \
      --remote-port 18080 --local-port 8080 --service
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    --install-dir) INSTALL_DIR="$2"; shift 2 ;;
    --share) SHARE="$2"; shift 2 ;;
    --server) SERVER="$2"; shift 2 ;;
    --control-port) CONTROL_PORT="$2"; shift 2 ;;
    --secret) SECRET="$2"; shift 2 ;;
    --tls) TLS_MODE="$2"; shift 2 ;;
    --tls-fingerprint) TLS_FINGERPRINT="$2"; shift 2 ;;
    --frp-transport) FRP_TRANSPORT="$2"; shift 2 ;;
    --remote-port) REMOTE_PORT="$2"; shift 2 ;;
    --local-ip) LOCAL_IP="$2"; shift 2 ;;
    --local-port) LOCAL_PORT="$2"; shift 2 ;;
    --protocol) PROTOCOL="$2"; shift 2 ;;
    --service) CREATE_SERVICE=1; shift ;;
    --run-now) RUN_NOW=1; shift ;;
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

if ! command -v curl >/dev/null 2>&1; then
  echo "Missing command: curl" >&2
  exit 1
fi

if ! command -v tar >/dev/null 2>&1; then
  echo "Missing command: tar" >&2
  exit 1
fi

asset="frpquick-client-linux-x64.tar.gz"
if [[ "$VERSION" == "latest" ]]; then
  url="https://github.com/$REPO/releases/latest/download/$asset"
else
  url="https://github.com/$REPO/releases/download/$VERSION/$asset"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $url"
curl -fL --retry 5 --retry-delay 2 -o "$tmp/$asset" "$url"

mkdir -p "$INSTALL_DIR" "$BIN_DIR"
tar -xzf "$tmp/$asset" -C "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/frpquick-client"
ln -sf "$INSTALL_DIR/frpquick-client" "$BIN_DIR/frpquick-client"

echo "Installed: $INSTALL_DIR/frpquick-client"

if [[ -z "$REMOTE_PORT" || -z "$LOCAL_PORT" ]]; then
  echo "No tunnel was configured. Run: $BIN_DIR/frpquick-client"
  exit 0
fi

if [[ -z "$SHARE" && -z "$SERVER" ]]; then
  echo "Tunnel parameters need either --share or --server." >&2
  exit 1
fi

quote_arg() {
  printf "%q" "$1"
}

cmd=("$INSTALL_DIR/frpquick-client")
if [[ -n "$SHARE" ]]; then
  cmd+=(--share "$SHARE")
else
  cmd+=(--server "$SERVER")
  [[ -n "$CONTROL_PORT" ]] && cmd+=(--control-port "$CONTROL_PORT")
  [[ -n "$SECRET" ]] && cmd+=(--secret "$SECRET")
  [[ -n "$TLS_MODE" ]] && cmd+=(--tls "$TLS_MODE")
  [[ -n "$TLS_FINGERPRINT" ]] && cmd+=(--tls-fingerprint "$TLS_FINGERPRINT")
fi
cmd+=(--remote-port "$REMOTE_PORT" --local-ip "$LOCAL_IP" --local-port "$LOCAL_PORT" --protocol "$PROTOCOL")
[[ -n "$FRP_TRANSPORT" ]] && cmd+=(--frp-transport "$FRP_TRANSPORT")

run_script="$INSTALL_DIR/run-$REMOTE_PORT.sh"
{
  echo '#!/usr/bin/env bash'
  echo 'set -euo pipefail'
  printf 'exec'
  for arg in "${cmd[@]}"; do
    printf ' %s' "$(quote_arg "$arg")"
  done
  printf '\n'
} >"$run_script"
chmod +x "$run_script"

echo "Tunnel command written: $run_script"

if [[ "$CREATE_SERVICE" -eq 1 ]]; then
  if ! command -v systemctl >/dev/null 2>&1; then
    echo "systemctl not found; run manually: $run_script" >&2
    exit 1
  fi

  service_file="$HOME/.config/systemd/user/$SERVICE_NAME-$REMOTE_PORT.service"
  mkdir -p "$(dirname "$service_file")"
  cat >"$service_file" <<EOF
[Unit]
Description=FRP QuickStart Client $REMOTE_PORT
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$INSTALL_DIR
ExecStart=$run_script
Restart=always
RestartSec=5s

[Install]
WantedBy=default.target
EOF

  systemctl --user daemon-reload
  systemctl --user enable "$SERVICE_NAME-$REMOTE_PORT"
  systemctl --user restart "$SERVICE_NAME-$REMOTE_PORT"
  loginctl enable-linger "$USER" >/dev/null 2>&1 || true
  echo "User service started: $SERVICE_NAME-$REMOTE_PORT"
elif [[ "$RUN_NOW" -eq 1 ]]; then
  exec "$run_script"
else
  echo "Run manually: $run_script"
fi
