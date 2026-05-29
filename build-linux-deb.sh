#!/bin/bash
# ============================================================
#  build-linux-deb.sh
#
#  Build a self-contained .deb package of ShockUI for
#  Debian/Ubuntu hosts. Run from the project root.
#
#  Requirements (on the build machine):
#    - .NET 8 SDK         (publishes the binaries)
#    - dpkg-deb           (built into Debian/Ubuntu; also works in WSL)
#
#  Output:
#    dist/shockui_<version>_amd64.deb
#
#  Install on target Linux:
#    sudo apt install ./shockui_<version>_amd64.deb
# ============================================================
set -euo pipefail

# ── Discover version from .csproj ────────────────────────────
VERSION="$(grep -oP '(?<=<Version>)[^<]+' ShockUI.csproj || true)"
if [ -z "$VERSION" ]; then
    echo "ERROR: could not read <Version> from ShockUI.csproj" >&2
    exit 1
fi

ARCH="amd64"
PKG_NAME="shockui_${VERSION}_${ARCH}"
WORK_DIR="dist/${PKG_NAME}"

echo "==> Building ShockUI v${VERSION} .deb package"

# ── Clean previous build ─────────────────────────────────────
rm -rf "$WORK_DIR"
mkdir -p "$WORK_DIR/DEBIAN"                  \
         "$WORK_DIR/opt/shockui"             \
         "$WORK_DIR/usr/share/applications"  \
         "$WORK_DIR/usr/local/bin"

# ── dotnet publish (self-contained, single file) ─────────────
echo "==> Publishing for linux-x64..."
dotnet publish ShockUI.csproj                       \
    -c Release                                       \
    -r linux-x64                                     \
    --self-contained true                            \
    -p:PublishSingleFile=true                        \
    -p:DebugType=embedded                            \
    -p:IncludeNativeLibrariesForSelfExtract=true     \
    -o "$WORK_DIR/opt/shockui"                       \
    --nologo

chmod +x "$WORK_DIR/opt/shockui/ShockUI"

# ── Wrapper in /usr/local/bin so users can launch from a shell ──
cat > "$WORK_DIR/usr/local/bin/shockui" <<'SHELL_WRAPPER'
#!/bin/bash
exec /opt/shockui/ShockUI "$@"
SHELL_WRAPPER
chmod +x "$WORK_DIR/usr/local/bin/shockui"

# ── Desktop launcher entry ───────────────────────────────────
cat > "$WORK_DIR/usr/share/applications/shockui.desktop" <<DESKTOP_ENTRY
[Desktop Entry]
Type=Application
Name=ShockUI
GenericName=Unified Engineering GUI
Comment=EOS engineering interface for System Controller and sub-modules
Exec=/opt/shockui/ShockUI
Terminal=false
Categories=Development;Engineering;
Version=${VERSION}
StartupNotify=true
DESKTOP_ENTRY

# ── Debian control file ──────────────────────────────────────
# Dependencies cover what Avalonia 11 + System.IO.Ports need on a
# fresh Ubuntu 22.04 / Debian 12 install. The libgl and libicu
# alternatives accommodate newer distros that bumped major versions.
cat > "$WORK_DIR/DEBIAN/control" <<CONTROL_FILE
Package: shockui
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: ${ARCH}
Maintainer: ShockUI Team <noreply@example.com>
Depends: libfontconfig1,
 libice6,
 libsm6,
 libx11-6,
 libxext6,
 libxrandr2,
 libxcursor1,
 libxi6,
 libxfixes3,
 libharfbuzz0b,
 libfreetype6,
 libgl1-mesa-glx | libgl1,
 libicu70 | libicu72 | libicu74 | libicu76
Description: ShockUI Unified Engineering GUI
 Avalonia-based desktop application for the EOS engineering
 interface. Connects via serial/UART to the System Controller
 and sub-modules (Pan/Tilt Stab Controller, NIR/MWIR cameras,
 SWIR/VisNIR optical modules, Noptel LRX laser range finder).
CONTROL_FILE

# ── Post-install script ──────────────────────────────────────
# Adds the invoking user to dialout (so the app can open /dev/ttyUSB*)
# and refreshes the desktop database so the launcher shows up.
cat > "$WORK_DIR/DEBIAN/postinst" <<'POSTINST_SCRIPT'
#!/bin/bash
set -e

TARGET_USER=""
if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ]; then
    TARGET_USER="$SUDO_USER"
elif [ -n "${PKEXEC_UID:-}" ]; then
    TARGET_USER="$(id -nu "$PKEXEC_UID" 2>/dev/null || true)"
fi

if [ -n "$TARGET_USER" ] && id "$TARGET_USER" >/dev/null 2>&1; then
    if ! id -nG "$TARGET_USER" | grep -qw "dialout"; then
        usermod -a -G dialout "$TARGET_USER"
        echo ""
        echo "  Added user '$TARGET_USER' to the 'dialout' group."
        echo "  >> Log out and back in for serial-port access to take effect."
        echo ""
    fi
fi

if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi

exit 0
POSTINST_SCRIPT
chmod 755 "$WORK_DIR/DEBIAN/postinst"

# ── Post-remove cleanup ──────────────────────────────────────
cat > "$WORK_DIR/DEBIAN/postrm" <<'POSTRM_SCRIPT'
#!/bin/bash
set -e
if command -v update-desktop-database >/dev/null 2>&1; then
    update-desktop-database -q /usr/share/applications || true
fi
exit 0
POSTRM_SCRIPT
chmod 755 "$WORK_DIR/DEBIAN/postrm"

# ── Build the .deb ───────────────────────────────────────────
echo "==> Assembling .deb..."
dpkg-deb --build --root-owner-group "$WORK_DIR" "dist/${PKG_NAME}.deb" >/dev/null

SIZE="$(du -h "dist/${PKG_NAME}.deb" | cut -f1)"
echo ""
echo "✓ Built: dist/${PKG_NAME}.deb  (${SIZE})"
echo ""
echo "  Install on the target machine:"
echo "    sudo apt install ./dist/${PKG_NAME}.deb"
echo ""
echo "  Upgrade later: same command with a newer .deb."
echo "  Uninstall:     sudo apt remove shockui"