# PandanciClone

一个用 C# WinForms 写的本地单词图工具，兼容当前目录里的 `wordmap.wordmap`、`dict.db`、`Dictionary.db` 和 `user-dict.txt`。

## 构建

在仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\src\PandanciClone\build.ps1
```

输出文件是根目录下的 `PandanciClone.exe`。

## 功能

- 打开、保存、另存为 `.wordmap`
- 显示并拖动单词卡，保存位置
- 右键画布添加单词或注释
- 右键单词删除、查词、标记复习、建立关联线
- 查询 `user-dict.txt`、`dict.db`、`Dictionary.db`
- 简单记忆曲线：已记住会延长下次复习间隔，未记住会回到短间隔
