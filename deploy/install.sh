#!/usr/bin/env bash
# Installs DvbHeadend on a Raspberry Pi (64-bit Raspberry Pi OS / Debian).
# Run from the unpacked release folder:   sudo deploy/install.sh
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
  echo "Run with sudo." >&2
  exit 1
fi

HERE="$(cd "$(dirname "$0")/.." && pwd)"
APP=/opt/dvbheadend
DOTNET=/opt/dotnet

if [[ ! -f "$HERE/publish/dvbheadend.dll" ]]; then
  echo "publish/dvbheadend.dll not found. Build first with ./build.sh (needs the .NET 10 SDK)." >&2
  exit 1
fi

# 1. ASP.NET Core 10 runtime (Microsoft's installer; nothing else is needed on the Pi).
if ! "$DOTNET/dotnet" --list-runtimes 2>/dev/null | grep -q "Microsoft.AspNetCore.App 10\."; then
  echo "Installing the ASP.NET Core 10 runtime into $DOTNET ..."
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --runtime aspnetcore --install-dir "$DOTNET"
fi

# 2. Service account with access to /dev/dvb (group "video").
if ! id dvbheadend >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/dvbheadend --shell /usr/sbin/nologin --groups video dvbheadend
fi

# 3. Program files. Site settings in /etc/dvbheadend/settings.json survive upgrades.
systemctl stop dvbheadend 2>/dev/null || true
mkdir -p "$APP" /etc/dvbheadend /var/lib/dvbheadend/recordings
rm -rf "${APP:?}"/*
cp -r "$HERE/publish/." "$APP/"
if [[ ! -f /etc/dvbheadend/settings.json ]]; then
  cp "$HERE/deploy/settings.example.json" /etc/dvbheadend/settings.json
fi
# The settings file may hold the MQTT password: readable by root and the service only.
chown root:dvbheadend /etc/dvbheadend/settings.json
chmod 640 /etc/dvbheadend/settings.json
chown -R dvbheadend:dvbheadend /var/lib/dvbheadend

# 4. Tvheadend holds the tuners exclusively; it has to go quiet first.
if systemctl is-active --quiet tvheadend 2>/dev/null; then
  echo "Stopping and disabling tvheadend (it locks the tuners)."
  systemctl disable --now tvheadend
fi

install -m 644 "$HERE/deploy/dvbheadend.service" /etc/systemd/system/dvbheadend.service
systemctl daemon-reload
systemctl enable --now dvbheadend

sleep 3
systemctl --no-pager --lines=15 status dvbheadend || true
echo
echo "Open http://$(hostname -I | awk '{print $1}'):5004  ->  Setup  ->  Scan now"
