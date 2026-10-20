# 树莓派同步服务器部署

`pandanci_sync_server.py` 使用 TCP/JSON 协议接收 PandanciClone 客户端请求，并把单词卡和文本笔记存入 SQLite。服务支持增量上传与下载，也会保留单词和笔记的删除标记。

默认监听所有网络接口的 `17890` 端口，数据库位于 `/var/lib/pandanci-sync/pandanci.db`。

## 环境要求

- Raspberry Pi OS 或其他使用 systemd 的 Debian 系 Linux
- Python 3（只使用标准库，无需安装 pip 依赖）
- 电脑与树莓派之间的 TCP 端口 `17890` 可访问

以下命令假设已经把本目录中的两个文件复制到树莓派并进入该目录。

## 首次安装

创建不可登录的专用服务用户：

```bash
sudo useradd --system --user-group --no-create-home --shell /usr/sbin/nologin pandanci-sync
```

如果提示用户已经存在，可以继续后面的步骤。

安装程序和服务文件：

```bash
sudo install -d -o root -g root -m 0755 /opt/pandanci-sync
sudo install -o root -g root -m 0755 pandanci_sync_server.py /opt/pandanci-sync/pandanci_sync_server.py
sudo install -d -o pandanci-sync -g pandanci-sync -m 0700 /var/lib/pandanci-sync
sudo install -o root -g root -m 0644 pandanci-sync.service /etc/systemd/system/pandanci-sync.service

sudo systemctl daemon-reload
sudo systemctl enable --now pandanci-sync
sudo systemctl status pandanci-sync --no-pager
```

`/opt/pandanci-sync` 由 `root` 管理，服务用户只有读取程序的权限；只有数据库目录 `/var/lib/pandanci-sync` 允许服务写入。

如果系统启用了 UFW，还需放行局域网访问。请将网段替换为实际的可信局域网网段：

```bash
sudo ufw allow from 172.16.204.0/24 to any port 17890 proto tcp
```

## 客户端设置

当前部署使用：

- 主机：`172.16.204.193`
- 端口：`17890`

在 Windows 客户端中选择“设置 → 局域网同步 → 同步设置...”，填写上述信息，再选择“立即同步”。可先在 PowerShell 中验证连接：

```powershell
Test-NetConnection 172.16.204.193 -Port 17890
```

建议在路由器中为树莓派配置 DHCP 固定租约。也可以选择性地安装 `avahi-daemon` 并设置主机名后使用 `.local` 地址，但部分 Windows 环境无法解析 mDNS，因此固定 IP 更稳妥。修改主机名不是运行同步服务的必要条件。

## 增量同步说明

客户端请求包含 `SinceRevision`，服务器响应包含 `ServerRevision`。升级后的第一次同步因为客户端还没有服务器修订号，会下载全部现有记录并完成一次全量对齐；之后只下载较新的修订，并只上传客户端待处理的变更。

服务器使用 `(kind, record_key)` 作为 SQLite 主键并执行 UPSERT，不会在每次同步时追加一份完整数据。磁盘占用仍可能因为新增单词、笔记、删除标记、WAL 文件和 SQLite 页分配而增加，这是正常现象。

如果客户端提交的修订号高于服务器当前修订号（例如恢复了较旧的服务器数据库），服务器会自动把该客户端请求当作首次同步处理，重新执行完整对齐。

## 日常维护

查看状态：

```bash
sudo systemctl status pandanci-sync --no-pager
```

持续查看日志：

```bash
sudo journalctl -u pandanci-sync -f
```

查看最近 100 行日志：

```bash
sudo journalctl -u pandanci-sync -n 100 --no-pager
```

重启服务：

```bash
sudo systemctl restart pandanci-sync
```

## 更新服务器程序

在包含新版 `pandanci_sync_server.py` 的目录中运行：

```bash
sudo systemctl stop pandanci-sync
sudo install -o root -g root -m 0755 pandanci_sync_server.py /opt/pandanci-sync/pandanci_sync_server.py
sudo systemctl start pandanci-sync
sudo systemctl status pandanci-sync --no-pager
```

若同时修改了 `pandanci-sync.service`，还需执行：

```bash
sudo install -o root -g root -m 0644 pandanci-sync.service /etc/systemd/system/pandanci-sync.service
sudo systemctl daemon-reload
sudo systemctl restart pandanci-sync
```

## 备份与恢复

数据库启用了 WAL 模式。为避免只复制主数据库却遗漏尚未合并的 WAL 数据，最简单可靠的备份方式是短暂停止服务后复制：

```bash
sudo systemctl stop pandanci-sync
sudo cp -a /var/lib/pandanci-sync/pandanci.db /var/lib/pandanci-sync/pandanci-backup-$(date +%Y%m%d-%H%M%S).db
sudo chown pandanci-sync:pandanci-sync /var/lib/pandanci-sync/pandanci-backup-*.db
sudo chmod 0600 /var/lib/pandanci-sync/pandanci-backup-*.db
sudo systemctl start pandanci-sync
```

也可以在服务运行时使用 SQLite backup API 制作一致性备份；不要只在 WAL 活跃时复制 `pandanci.db`。

恢复前先停止服务，并把现有数据库保留为可回退副本。下面的 `BACKUP_FILE` 必须替换为要恢复的实际文件名：

```bash
sudo systemctl stop pandanci-sync
sudo mv /var/lib/pandanci-sync/pandanci.db /var/lib/pandanci-sync/pandanci-before-restore.db
sudo cp BACKUP_FILE /var/lib/pandanci-sync/pandanci.db
sudo chown pandanci-sync:pandanci-sync /var/lib/pandanci-sync/pandanci.db
sudo chmod 0600 /var/lib/pandanci-sync/pandanci.db
sudo systemctl start pandanci-sync
sudo systemctl status pandanci-sync --no-pager
```

恢复后第一次同步可能执行完整对齐，这是预期行为。

## 安全限制

当前协议没有用户认证和 TLS 加密，只适用于可信局域网。不要把 `17890` 端口直接映射到公网，也不要在不受信任的网络中开放该服务。若需要跨互联网同步，应先增加身份认证和加密，或通过受控 VPN 访问。
