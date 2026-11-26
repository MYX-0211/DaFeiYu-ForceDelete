# 大肥鱼 · 强制删除工具

> 基于 [ccysb/CC-DEL](https://github.com/ccysb/CC-DEL) 的毛玻璃美化版。单文件、免安装、无外部依赖，专治 Windows 上「文件正在使用中 / 拒绝访问 / 找不到该项目」这类删不掉的文件和文件夹。

![界面截图](docs/screenshot.png)

## 下载与运行

到 [Releases](../../releases) 下载 `DaFeiYu-ForceDelete-v1.0.0.exe`，双击即可运行，不需要安装。文件名可以随意改，不影响功能。

- 系统要求：Windows 10 / 11，自带 .NET Framework 4.x 与 PowerShell 5.1，无需额外安装
- 单文件约 1.4 MB，脚本、界面、图标、背景图全部内嵌
- 运行时会把资源释放到 `%LOCALAPPDATA%\DeepSeek\`，不写系统目录、不改 HKLM
- 普通权限即可运行；删除系统目录里的东西时，点右上角「以管理员重启」
- 没有数字签名，首次运行若出现 SmartScreen 提示，点「更多信息 → 仍要运行」

> **删除不进回收站，删了就没了。** 请先确认列表里的每一项，再点「开始执行删除」。

## 功能

- **渐进式删除**：直接删 → 清除只读/隐藏/系统属性 → 解除占用 → 修复权限 → 强力兜底 → 重启后删除。能在前一步解决就不动权限
- **解除占用**：用系统自带的 Restart Manager 找出是谁锁着文件；可以只结束占用进程、不删文件
- **长路径与保留名**：支持超过 260 字符的路径，也能删掉 `nul` 这类保留名文件
- **拖拽加入**：把文件或文件夹拖进窗口即可，支持 Ctrl 多选
- **右键菜单**：安装后在资源管理器里右键就能把目标加入待删除列表
- **实时日志**：每一步做了什么、失败在哪，都写进日志，可一键打开

## 界面

| 区域 | 作用 |
| --- | --- |
| 顶部 | 标题、当前权限、「右键菜单」安装/卸载、「以管理员重启」 |
| 选项 | 删除失败时解除占用、删除失败时修复权限、删除前二次确认（默认全开） |
| 待删除列表 | 半透明玻璃面板，显示完整路径，右侧标注文件 / 文件夹 |
| 右侧操作 | 解除占用、移除选中、清空列表、打开日志 |
| 运行日志 | 实时输出处理过程 |
| 底部 | 状态与进度条、「开始执行删除」 |

窗口默认 1000 × 700（逻辑像素），可自由缩放，高 DPI 下按比例放大。

## 命令行

```text
大肥鱼-强制删除工具.exe                  打开图形界面（已有窗口则激活它）
大肥鱼-强制删除工具.exe -Add "路径"      把路径加入待删除列表，不开新窗口
大肥鱼-强制删除工具.exe -Install         安装资源管理器右键菜单
大肥鱼-强制删除工具.exe -Uninstall       卸载右键菜单
大肥鱼-强制删除工具.exe -Probe "路径"    只查不删：是谁占用了它
大肥鱼-强制删除工具.exe -Pending "路径"  登记为下次开机时由系统删除
大肥鱼-强制删除工具.exe -SelfTest        自检（19 项）
```

（Release 里的文件名是 `DaFeiYu-ForceDelete-v1.0.0.exe`，下面以自己构建出的文件名举例。）

也可以不编译直接跑脚本：`powershell -ExecutionPolicy Bypass -File src\DeepSeek.ps1`。脚本需要和 `DeepSeek.Ui.cs`、`logo.ico`、`bg.png` 放在同一目录。

## 从源码构建

只需要 Windows 自带的 PowerShell 5.1 和 .NET Framework，不用装 Go、Visual Studio 或任何 SDK：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

产物在 `dist\大肥鱼-强制删除工具.exe`。构建脚本会检查 exe 的子系统是不是 GUI（防止闪黑窗）以及四个资源是否都已内嵌。

## 目录结构

```text
.
├─ build.ps1              构建脚本（csc.exe 编译）
├─ src\
│  ├─ launcher.cs         C# 宿主：释放资源、无黑窗拉起 PowerShell、单实例
│  ├─ DeepSeek.ps1        主脚本：删除引擎 + 图形界面（内部代号 DeepSeek）
│  ├─ DeepSeek.Ui.cs      界面控件与毛玻璃绘制
│  ├─ app.manifest        清单：DPI 感知
│  ├─ logo.ico            程序图标
│  └─ bg.png              界面背景图
├─ assets\icon.png        图标源图
├─ docs\                  截图
└─ upstream\              原版 CC-DEL 的脚本、README、LICENSE，仅供对照
```

## 相对原版 CC-DEL 的改动

- 图形界面重写为毛玻璃风格：每个面板独立绘制自己区域的模糊背景取样，列表半透明
- 自定义背景图与程序图标
- 宿主程序由 Go 改为 C#（`launcher.cs`），用系统自带的 `csc.exe` 即可编译
- 窗口默认尺寸调整为 1000 × 700
- 删除引擎（`Invoke-ForceDelete`、`Unlock-Target` 等）没有改动

## 注意事项

- 「解除占用」会结束正在使用目标的进程，未保存的工作会丢失
- 「修复权限」仅在直接删除失败时才会执行，会对目标执行 `takeown` 与 `icacls`
- 被已加载的 DLL 锁住的文件（映像锁定）通常重启前删不掉，可以用 `-Pending` 登记为开机时删除
- 日志在 `%LOCALAPPDATA%\DeepSeek\DeepSeek.log`

## 许可证与致谢

本项目基于 [ccysb/CC-DEL](https://github.com/ccysb/CC-DEL)（MIT License, Copyright (c) 2026 pcoof）修改，原版许可证全文见 [LICENSE](LICENSE) 与 [upstream/LICENSE](upstream/LICENSE)。

「DeepSeek」仅为本项目内部脚本与数据目录的代号，与 DeepSeek 公司无关，本项目未获其背书。
