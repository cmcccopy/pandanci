# Raspberry Pi deployment

The server implements the existing Pandanci TCP/JSON protocol and stores both
word cards and notes in SQLite. Note deletion tombstones are retained so that
a deleted note is not restored by another device.

## Install

```bash
sudo hostnamectl set-hostname pandanci-pi
sudo apt update
sudo apt install -y python3 avahi-daemon
sudo systemctl enable --now avahi-daemon

sudo useradd --system --no-create-home pandanci-sync || true
sudo mkdir -p /opt/pandanci-sync /var/lib/pandanci-sync
sudo cp pandanci_sync_server.py /opt/pandanci-sync/
sudo cp pandanci-sync.service /etc/systemd/system/
sudo chown -R pandanci-sync:pandanci-sync /opt/pandanci-sync /var/lib/pandanci-sync
sudo systemctl daemon-reload
sudo systemctl enable --now pandanci-sync
sudo systemctl status pandanci-sync
```

Configure every Windows client with host `pandanci-pi.local` and port `17890`.
Use a DHCP reservation for the Raspberry Pi as a fallback if mDNS is not
available. Test from Windows with:

```powershell
Test-NetConnection pandanci-pi.local -Port 17890
```

Follow logs with `journalctl -u pandanci-sync -f`. Back up
`/var/lib/pandanci-sync/pandanci.db` regularly.