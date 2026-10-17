#!/usr/bin/env bash
set -euo pipefail
LABEL="com.lydianscale.ragamuffin"
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
rm -f "$HOME/Library/LaunchAgents/$LABEL.plist"
echo "Removed $LABEL (documents and database are untouched)."
