#!/usr/bin/env bash
# Installer for claude-usage-bar (Claude Usage menu bar app + ccc CLI) on macOS.
#
# By default it reads your existing Claude Code login (from the Keychain) — no
# sign-in needed. Builds a normal, quittable .app, starts it at login via a
# LaunchAgent (no forced respawn), and installs the `ccc` terminal command.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="$HOME/.local/bin"
CFGDIR="$HOME/.config/ccc"
VENV="$CFGDIR/venv"
APP_NAME="Claude Usage"
LABEL="com.claude-usage-bar.menubar"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"

echo "==> Installing into $BIN and $CFGDIR"
mkdir -p "$BIN" "$CFGDIR"

# 1. Python venv + dependency (rumps; pulls in pyobjc)
python3 -m venv "$VENV"
"$VENV/bin/pip" install --quiet --upgrade pip
"$VENV/bin/pip" install --quiet -r "$REPO/requirements.txt"

# 2. CLI + menu bar app
install -m 0755 "$REPO/bin/ccc" "$BIN/ccc"
install -m 0755 "$REPO/bin/ccc-bar" "$BIN/ccc-bar"

# 3. Menu bar icon (prebuilt; no image libs needed)
cp "$REPO/assets/icon.png" "$CFGDIR/icon.png"

# 4. Build the .app bundle in a writable Applications folder (tap to open)
if [ -w /Applications ]; then APPDIR="/Applications"; else APPDIR="$HOME/Applications"; fi
mkdir -p "$APPDIR"
APP="$APPDIR/$APP_NAME.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$REPO/assets/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
cat > "$APP/Contents/MacOS/launcher" <<EOF
#!/bin/bash
exec "$VENV/bin/python" "$BIN/ccc-bar"
EOF
chmod +x "$APP/Contents/MacOS/launcher"
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>Claude Usage</string>
    <key>CFBundleDisplayName</key><string>Claude Usage</string>
    <key>CFBundleIdentifier</key><string>com.claude-usage-bar.app</string>
    <key>CFBundleExecutable</key><string>launcher</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSUIElement</key><true/>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
</dict>
</plist>
PLIST
touch "$APP"
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP" 2>/dev/null || true

# 5. LaunchAgent: start at login, runs the app directly (reliable), NO KeepAlive
#    so it stays quit when you choose Quit. A single-instance guard in the app
#    keeps a manual tap and the login start from ever double-launching.
launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
cat > "$PLIST" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>$LABEL</string>
    <key>ProgramArguments</key>
    <array>
        <string>$VENV/bin/python</string>
        <string>$BIN/ccc-bar</string>
    </array>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><false/>
    <key>ProcessType</key><string>Interactive</string>
    <key>StandardOutPath</key><string>$CFGDIR/cccbar.log</string>
    <key>StandardErrorPath</key><string>$CFGDIR/cccbar.log</string>
</dict>
</plist>
EOF
launchctl bootstrap "gui/$(id -u)" "$PLIST"

# 6. PATH note
case ":$PATH:" in
  *":$BIN:"*) ;;
  *) echo "==> Add to your shell rc:  export PATH=\"\$HOME/.local/bin:\$PATH\"" ;;
esac

echo
echo "==> Done. Look for the '>' icon in your menu bar (you may get a one-time"
echo "    Keychain prompt to allow reading your Claude Code login — click Allow)."
echo "    Open anytime: tap \"$APP_NAME\" in Applications/Spotlight, or run 'ccc'."
