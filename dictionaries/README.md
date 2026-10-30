# 离线中英词典

此目录由 `scripts/install-dictionaries.ps1` 安装词典数据。程序同时保留对根目录 `user-dict.txt`、`dict.db`、`Dictionary.db` 的兼容，不会覆盖这些文件。

当前已安装 ECDICT 3,402,564 条与 CC-CEDICT 125,324 条。首次下载约 211 MiB，正式数据库合计约 1.40 GiB；生成索引和解压时还需要临时空间。

- ECDICT 1.0.28：完整 SQLite 英汉词库，包含英文释义、中文释义、音标、词形和词频。来源：https://github.com/skywind3000/ECDICT 。上游仓库声明 MIT 许可，原文保存在 `licenses/ECDICT-MIT.txt`；词库也含上游汇编的第三方资料，分发时应保留来源说明。
- CC-CEDICT：简体、繁体中文词语与英语释义、拼音。来源：https://www.mdbg.net/chinese/dictionary?page=cedict 。许可为 CC BY-SA 4.0，保留署名，改编数据应按相同许可分享；许可和上游文件头见 `licenses/`。

`ecdict.db` 是下载后增加不区分大小写索引的 ECDICT 数据库；`offline-index.db` 包含 CC-CEDICT、ECDICT 中文释义反查索引及词形索引。中文释义反查属于英汉释义的反向检索，不等同于人工编写的汉英释义；界面会标明其来源。

`manifest.json` 记录来源 URL、版本、安装时间、下载文件 SHA-256 和真实词条数量。生成数据库及下载压缩包不提交到 Git；如需在其他电脑上使用，复制整个此目录或重新运行安装脚本。

在仓库根目录安装（需要 Python 3 和 Windows curl）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install-dictionaries.ps1 -Proxy http://127.0.0.1:10805
```

下载文件保留在 `downloads/` 以便重新生成索引。更新 CC-CEDICT 时，可先将对应旧压缩包移到备份目录，再运行安装。更新前请关闭软件，以便安全替换正在使用的数据库。
