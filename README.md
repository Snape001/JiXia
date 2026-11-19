# 记下

一款贴在 Windows 屏幕右侧的任务清单。把手上的数字显示紧急任务数，鼠标停留即可展开。专注时突然想到要查的事，可以先写在底部，等手头的事做完再回顾。

[下载记下.exe](outputs/记下.exe?raw=true) · [观看操作演示](outputs/记下操作演示.gif)

## 使用

- 点「紧急」或「不紧急」旁的 `＋` 创建任务。拖动卡片可以调整顺序或分区，双击可以编辑。
- 把完成的任务拖入垃圾桶。松手后有 5 秒可以撤销，任务会回到原位。
- 在底部输入分心念头，按回车保存。点「回顾」逐条处理，已经勾选的记录也可以恢复。
- 右键系统托盘里的「记下」图标，可以展开或收起侧栏、固定面板，或退出应用。

![记下的托盘右键菜单](outputs/托盘菜单.png)

## 运行与构建

适用于装有 .NET Framework 4.8 的 Windows。下载后双击 `记下.exe` 即可运行。升级时先从托盘退出旧版。任务和分心记录保存在当前用户的 `%LOCALAPPDATA%\SidePage` 目录中。

在项目根目录用 PowerShell 执行：

```powershell
.\src\build.ps1
```

程序输出到 `outputs\记下.exe`。构建使用 Windows 自带的 .NET Framework C# 编译器和 WPF 程序集。`qa` 中的检查脚本使用独立测试数据。

```powershell
.\qa\check.ps1
.\qa\check-drag.ps1
.\qa\check-thoughts.ps1
.\qa\check-tray.ps1
```
