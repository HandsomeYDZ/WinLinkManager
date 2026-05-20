# WinLinkManager (NTFS Link Manager)

> 说明：本人没有过多代码经验，此项目由AI生成，仅作参考使用。

## 功能

- 浏览 NTFS 符号链接 / Junction 列表
- 按名称和路径搜索
- 创建链接（文件符号链接、目录符号链接、Junction）
- 链接类型转换（`/D` 与 `/J`）

## 技术栈

- C# / WPF / .NET Framework 4.8
- SQLite（Microsoft.Data.Sqlite）
- NTFS USN

## 本地运行

```bash
dotnet run --project WinLinkManager.App
```

## 手动打包（Portable 多文件）

```bash
dotnet publish WinLinkManager.App -c Release -o publish
```

输出目录：`publish\`   
运行方式：右键 `WinLinkManager.App.exe`，选择“以管理员身份运行”。

## 数据目录

程序数据默认存储于：

```text
%LocalAppData%\WinLinkManager\
```

## License

MIT
