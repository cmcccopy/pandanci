# PandanciClone

PandanciClone 是一个使用 C# WinForms 编写的本地单词图工具，兼容项目目录中的 `.wordmap` 文件，支持 Google 翻译与本地中英双向词典。

## 主要功能

- 打开、保存和另存为 `.wordmap` 单词图
- 添加、拖动、复习和删除单词卡
- 添加、编辑和调整文本笔记
- 添加图片以及建立单词关联线
- 使用 Google 翻译和本地中英词典，支持划词查询
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

如果旧程序正在运行、无法覆盖可执行文件，可先生成独立新版：

```powershell
powershell -ExecutionPolicy Bypass -File .\src\PandanciClone\build.ps1 -OutputFile PandanciClone.updated.exe
```

保存内容并退出旧程序后，运行 `PandanciClone.updated.exe` 即可。该文件位于同一目录，继续使用现有单词图和设置。

## Google 翻译与 429 限流

查询窗口包含 Google 翻译与本地词典两个独立面板，原 Bing 面板已替换为离线查词。未配置 API Key 时，Google 优先使用免密钥网页 RPC 协议：

```text
POST https://translate.google.com/_/TranslateWebserverUi/data/batchexecute?rpcids=MkEWBc
```

该入口不需要 Google Cloud 项目或 API Key。直接打开翻译窗口使用即可；如果所在网络无法直连 Google，可在“设置 → 翻译代理...”中填写可用的 HTTP 代理，例如 `127.0.0.1:10805`。结果状态会显示“Google Web RPC（免密钥）”。如曾配置官方密钥，应清除本机密钥和 `PANDANCI_GOOGLE_TRANSLATE_API_KEY` 环境变量后再使用免密钥模式。

2026-10-29 在当前代理配置下实测 RPC 入口返回 HTTP 200。实现参考了开源项目 [translators 的 GoogleV2 协议](https://github.com/UlionTse/translators)及 [googletrans](https://github.com/ssut/py-googletrans)，没有引入它们的运行依赖。程序使用 Windows 系统目录中的 `curl.exe` 发出请求，遵循软件配置的代理，并支持取消、超时和冷却；系统没有 `curl.exe` 时才使用 .NET 请求。Windows 10/11 通常已经包含该工具。

RPC 入口不存在或响应格式变化时，才尝试原来的 `https://translate.googleapis.com/translate_a/single?client=gtx` 遗留接口一次。收到 429 或 503 时遵守冷却规则，不通过反复切换入口绕开服务器要求。两个网页接口都可能限流或变更，RPC 也不是有稳定性承诺的 Cloud Translation API。

如果直连或代理的出口 IP 被限流，公共 JSON 和 Mobile 页面都可能返回 `429 Too Many Requests`。短时间重复访问两个入口通常不能解除这种限制。

当前版本已移除 Mobile 轮询和 1、2、4、8 秒的重复请求，改为：

- 多个翻译请求和长文本分段共用请求队列，公共接口的请求启动间隔至少为 1.5 秒。
- 首次收到公共接口 429 后暂停该出口的请求至少 5 分钟；再次限流会延长冷却，默认最多延长至 30 分钟。服务器指定的 `Retry-After` 更长时，以服务器为准。
- 冷却期间直接提示 HTTP 状态和剩余时间，已缓存的结果仍可使用，右侧本地词典独立运行且不联网。
- 相同内容和目标语言复用缓存；长文本中已完成的段落也会复用，切换文本或关闭弹窗会取消旧的 Google 请求。

这些措施减少重复请求，但无法保证免密钥接口永不返回 429。日常使用可以继续使用 Web 遗留接口；对可用性有更高要求时，可选择配置 Google 官方 Cloud Translation API。

### 配置 Google 官方接口

1. 在 [Google Cloud Console](https://console.cloud.google.com/) 创建或选择项目，启用 **Cloud Translation API**，按官方要求配置结算与配额。
2. 创建 API Key，并将其 API 使用范围限制为 Cloud Translation API。客户端使用 Basic v2 接口；不要为这个桌面客户端设置仅限浏览器 HTTP Referer 的密钥限制。
3. 打开软件“设置 → Google 官方接口...”，填入 API Key，确定后再次翻译。结果状态会注明“Google 官方接口”。现有的“翻译代理...”设置同样适用于官方接口。

密钥保存在 `%LocalAppData%\PandanciClone\google-translation-key.bin`，通过 Windows DPAPI 按当前用户加密，不写入项目中的 `.settings` 或同步数据。不要把该密钥文件复制到其他电脑；每台设备分别配置即可。官方服务按 Google 当前价格、免费额度和项目配额计费。

也可以设置当前用户的环境变量 `PANDANCI_GOOGLE_TRANSLATE_API_KEY`，重启程序后生效。本机加密保存的密钥优先；在设置中留空会清除本机密钥，但不会删除环境变量。两者均未配置时使用公共接口。

官方接口收到 429 或配额类 403 时也会暂停请求，默认至少 60 秒，并遵守 `Retry-After`。密钥无效、API 未启用、结算或权限问题会给出对应提示，请检查 Google Cloud 项目；不会自动切回公共接口掩盖配置错误。

参考资料：[Google 官方身份验证](https://docs.cloud.google.com/translate/docs/authentication)、[文本翻译 API](https://docs.cloud.google.com/translate/docs/basic/translating-text)、[配额说明](https://docs.cloud.google.com/translate/quotas)、[HTTP 429 与 Retry-After 标准](https://www.rfc-editor.org/rfc/rfc6585.html#section-4)。

翻译回归测试可在仓库根目录运行，无需 Google Key，也不会发送外网翻译请求：

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-translation-tests.ps1
```

## 本地英汉、汉英词典

原 Bing 翻译面板已改为“本地词典”，不再请求 Bing。输入词语并按 Enter 会同时启动 Google 翻译和离线查词；本地词典面板的刷新按钮只查询词典，不触发 Google 请求。主窗口原有“查词”和单词卡双击查词也可使用扩展词库。

已安装的新增词库：

| 词库 | 实际词条数 | 用途 |
| --- | ---: | --- |
| [ECDICT 1.0.28](https://github.com/skywind3000/ECDICT) | 3,402,564 | 英汉及英文释义，含单词、短语、专名、音标和词形信息 |
| [CC-CEDICT](https://www.mdbg.net/chinese/dictionary?page=cedict) | 125,324 | 简体、繁体中文词语查英语释义和拼音 |

另生成 6,368,485 条中文释义反查索引，以及 455,009 条英语词形索引。支持 `went` 等词形还原、简繁体查询及英语释义反查。反查结果会单独注明来源，不当作人工编写的汉英释义。

仍优先显示 `user-dict.txt` 中的自定义释义，并兼容原来的 `dict.db`、`Dictionary.db`。长句未直接收录时，按词或词组显示释义；这属于查词，不是整句机器翻译。

新增数据库位于 `dictionaries/ecdict.db` 与 `dictionaries/offline-index.db`，合计约 1.40 GiB。它们不上传到同步服务器，也不提交到 Git。复制软件到另一台电脑时，请同时复制 `dictionaries/` 目录。来源、许可、下载 SHA-256 和真实数量保存在该目录的 `manifest.json` 与 `licenses/` 中，安装与更新步骤见 [词典说明](dictionaries/README.md)。

验证本地词典及导入器：

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-dictionary-tests.ps1
python -m unittest discover -s tests -p test_dictionary_import.py
```

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
- `dictionaries/`：扩展词库、索引、来源清单与许可证。
- `scripts/install-dictionaries.ps1`：下载开放词库并生成离线索引。
- `images/`：添加到单词图中的本地图片。
- `deploy/raspberry-pi/`：树莓派同步服务器及 systemd 服务文件。
- `src/PandanciClone/`：Windows 客户端源代码和构建脚本。
