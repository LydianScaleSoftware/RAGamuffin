#!/usr/bin/env bash
# Publishes Ragamuffin and installs it as a launchd agent that starts at login
# and restarts if it exits. Re-run after code changes to redeploy.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
LABEL="com.lydianscale.ragamuffin"
PUBLISH="$ROOT/publish"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
DOTNET="$(command -v dotnet)"

# Publish into a clean folder so files from older builds don't linger.
rm -rf "$PUBLISH"
dotnet publish "$ROOT/src/Ragamuffin" -c Release -o "$PUBLISH" --nologo
mkdir -p "$ROOT/data/files" "$ROOT/data/logs"

cat > "$PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>$DOTNET</string>
    <string>$PUBLISH/Ragamuffin.dll</string>
  </array>
  <key>WorkingDirectory</key><string>$PUBLISH</string>
  <key>EnvironmentVariables</key>
  <dict>
    <key>ASPNETCORE_ENVIRONMENT</key><string>Production</string>
    <key>Rag__FilesDirectory</key><string>$ROOT/data/files</string>
  </dict>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ThrottleInterval</key><integer>10</integer>
  <key>StandardOutPath</key><string>$ROOT/data/logs/ragamuffin.log</string>
  <key>StandardErrorPath</key><string>$ROOT/data/logs/ragamuffin.log</string>
</dict>
</plist>
PLIST

launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
# bootout returns before the old agent is gone; bootstrapping too early fails with "Input/output error".
for _ in $(seq 1 20); do
  launchctl print "gui/$(id -u)/$LABEL" >/dev/null 2>&1 || break
  sleep 0.5
done
launchctl bootstrap "gui/$(id -u)" "$PLIST"
echo "Installed $LABEL. Logs: $ROOT/data/logs/ragamuffin.log"
echo "UI: http://localhost:5077"
