# PandanciClone

PandanciClone 是一个使用 C# WinForms 编写的本地单词图工具，兼容项目目录中的 `.wordmap` 文件，并可读取 `user-dict.txt`、`dict.db` 和 `Dictionary.db` 查询释义。

## 主要功能

- 打开、保存和另存为 `.wordmap` 单词图
- 添加、拖动、复习和删除单词卡
- 添加、编辑和调整文本笔记
- 添加图片以及建立单词关联线
- 使用 Google 和 Bing 翻译，支持划词翻译
- 使用 Tesseract 进行截图 OCR 翻译
- 在局域网内同步单词卡和文本笔记
- 使用简化记忆曲线安排下次复习时间

常用快捷键可在程序的“帮助”菜单中查看。

## 运行

直接运行根目录中的 `PandanciClone.exe`。程序默认打开同目录下的 `wordmap.wordmap`；如果该文件不存在，则使用 `blank.wordmap`。

程序及其数据文件建议放在当前 Windows 用户有写入权限的目录中，否则设置、同步状态和单词图可能无法保存。

## 构建

构建环境需要 64 位 Windows、.NET Framework 4.x 和 PowerShell。仓库已经包含编译所需的 `System.Data.SQLite.DLL`。

在仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\src\PandanciClone\build.ps1
```

构建结果为根目录下的 `PandanciClone.exe`。

## 局域网同步

当前树莓派同步服务器地址：

- 主机：`172.16.204.193`
- 端口：`17890`

在客户端中依次打开“设置 → 局域网同步 → 同步设置...”，填写主机和端口，然后选择“立即同步”。如果树莓派的地址由 DHCP 自动分配，建议在路由器中为它设置固定租约，以免 IP 地址变化。

同步内容包括单词卡、复习状态、文本笔记及其删除状态。目前不同步图片文件和单词关联线。

### 增量同步方式

- 升级到增量同步版本后的第一次同步会执行一次完整对齐，数据较多时需要稍等。
- 后续同步只上传本机尚未提交的变更，并只下载服务器上的新修订。
- 同步过程中再次发生本地修改时，客户端会自动补做一轮同步。
- 删除单词或笔记时会保留删除标记，防止其他设备把旧数据重新恢复。
- 冲突按记录的更新时间处理；时间相同时使用设备 ID 作为稳定的判定依据。因此各设备的系统时间应保持正确。

增量同步不会在每次点击时重新上传整份 1 MB 以上的数据。服务器也不会为每次同步保存一份完整副本，而是按单词或笔记主键更新 SQLite 记录。数据库仍会随着新增记录、删除标记和 SQLite 页分配而逐步增大。

### 本地同步文件

- `PandanciClone.settings`：客户端设置，包括同步主机和端口。
- `PandanciClone.sync`：当前客户端的同步修订号、待上传记录和删除标记。
- `%LocalAppData%\PandanciClone\sync-device-id.txt`：当前 Windows 用户在这台设备上的唯一同步 ID。

不要在多台电脑之间复制或共享 `PandanciClone.sync`，每台客户端都应维护自己的同步进度。设备 ID 也应保持独立；复制整个程序目录到另一台电脑时，不需要复制 `%LocalAppData%` 中的设备 ID。

## 同步故障排查

先在 Windows PowerShell 中检查端口：

```powershell
Test-NetConnection 172.16.204.193 -Port 17890
```

如果 `TcpTestSucceeded` 为 `False`，请确认电脑和树莓派位于可互通的局域网、树莓派 IP 没有变化，并检查服务器服务和防火墙。如果端口可访问但客户端仍提示失败，请查看树莓派服务日志。详细部署和维护方法见 [`deploy/raspberry-pi/README.md`](deploy/raspberry-pi/README.md)。

## 主要文件

- `wordmap.wordmap`：默认单词图数据。
- `blank.wordmap`：新建单词图时使用的空白模板。
- `dict.db`、`Dictionary.db`、`user-dict.txt`：本地词典数据。
- `images/`：添加到单词图中的本地图片。
- `deploy/raspberry-pi/`：树莓派同步服务器及 systemd 服务文件。
- `src/PandanciClone/`：Windows 客户端源代码和构建脚本。
