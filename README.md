# NetSpeed

Windows 11 任务栏网速监控。只做一件事：**看网速，以及是谁在占网速**。

鼠标移到任务栏上的速度读数，弹出卡片显示当前上下行速率，以及按流量排名的前 N 个进程。

- 任务栏内嵌式读数（↑ 上传 / ↓ 下载），跟随系统亮/暗主题
- 悬停卡片：实时总速率 + 进程占用排行（图标、名称、各自的上下行速率、占比条）
- 没有 CPU、内存、显卡、硬盘、历史流量这些无关信息
- 全屏时自动隐藏，不遮挡视频和游戏
- 可切换为桌面悬浮窗（可拖动）

## 运行要求

- Windows 10 1809 及以上 / Windows 11
- [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（用 `-SelfContained` 打包则不需要）
- **进程流量排行需要管理员权限**（见下方说明）

## 构建

```powershell
.\build.ps1
```

产物在 `publish\`。要一个不依赖 .NET 运行时的版本：

```powershell
.\build.ps1 -SelfContained
```

## 装到另一台电脑

程序是绿色的，没有安装程序。三种打包方式，实测都能正常建立内核 ETW 会话（即进程排行可用）：

| 打包命令 | 拷什么 | 目标机器要装什么 | 体积 |
| --- | --- | --- | --- |
| `.\build.ps1` | `publish\` **整个文件夹** | [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0) | ~6 MB |
| `.\build.ps1 -SelfContained` | 输出文件夹整个拷 | 无 | ~150 MB |
| `.\build.ps1 -SingleFile` | 单个 `NetSpeed.exe` | 无 | ~150 MB |

前两种**必须拷整个文件夹**，只拷 `NetSpeed.exe` 起不来——TraceEvent 等依赖是并排的独立 DLL。

放到目标机器上一个**固定位置**（例如 `C:\Tools\NetSpeed\`），别放桌面或下载目录——开机自启记的是绝对路径，之后再挪就得重设。

然后：

1. 右键 `NetSpeed.exe` → 以管理员身份运行（不提权只有总网速，没有进程排行）
2. 右键读数 → 开机自动启动

不用拷的东西：`%AppData%\NetSpeed\` 下的配置和日志会自动重建；开机自启是每台机器各自的计划任务，需要在新机器上重新打开一次。

> 随包的 `amd64\KernelTraceControl.dll` 只在合并 ETL 文件时才用得到，本程序不做这件事——实测把它删掉，实时内核会话照样能启动。所以单文件模式也是安全的。

排查启动问题看 `%AppData%\NetSpeed\error.log`，里面会记录 ETW 会话状态（`etw: Running` / `etw: NeedsAdmin` / `etw: Failed - ...`）。

> 如果程序是被强制结束的（任务管理器结束进程、崩溃），它的内核 ETW 会话会残留到下次重启。下次启动时程序会自动接管并重建同名会话，不需要手工清理。

## 使用

| 操作 | 结果 |
| --- | --- |
| 鼠标悬停在读数上 | 弹出详情卡片 |
| 左键单击读数 | 钉住卡片（点击别处关闭） |
| 右键单击读数 / 托盘图标 | 菜单：设置、开机自启、切换悬浮窗、退出 |
| 卡片左下「设置」 | 打开设置窗口 |

设置项：显示位置、宽度、与托盘图标的间距、垂直微调、字号、刷新间隔、速度单位（B/s 或 bps）、排行条数、网卡选择、开机自启、托盘图标、全屏隐藏。

配置文件：`%AppData%\NetSpeed\settings.json`
错误日志：`%AppData%\NetSpeed\error.log`

> 开机自启记录的是**当前 exe 的绝对路径**。如果之后把程序挪了位置（比如从 `bin\Release\...` 换到 `publish\`），需要在设置里把开机自启关掉再打开一次。

## 为什么需要管理员权限

Windows 没有提供"每个进程用了多少网络流量"的性能计数器。任务管理器和资源监视器拿到这个数据的方式，是订阅内核的 **ETW（Event Tracing for Windows）** 网络事件——而创建内核 ETW 会话必须是管理员。

本程序的处理方式：

- **非管理员运行**：正常显示总网速，卡片里提示并提供「以管理员身份重启」按钮
- **管理员运行**：订阅 `Microsoft-Windows-Kernel-Network`，按 PID 累计 TCP/UDP（含 IPv6）的收发字节，再按可执行文件聚合（Chrome 的几十个子进程会合并成一行）
- **开机自启**：管理员状态下写入计划任务（最高权限），开机不会弹 UAC；非管理员状态下退回普通启动项

总速率不走 ETW，而是读网卡计数器（`GetIPStatistics`）——ETW 的进程求和总会比网卡实际吞吐略低，用网卡计数器作为总速率更准确。

## 实现要点

```
src/NetSpeed/
  Core/
    EtwTrafficMonitor.cs   内核 ETW 会话，按 PID 累计收发字节
    NetworkMeter.cs        网卡计数器采样、自动选活跃网卡（带粘滞，避免 VPN 抢占）
    TrafficAggregator.cs   按可执行文件聚合 + EMA 平滑 + 排序
    ProcessInfoCache.cs    PID -> 进程名（取文件说明，svchost 之类做了中文映射）
    MonitorService.cs      采样循环，产出不可变快照
  Interop/
    TaskbarLocator.cs      定位任务栏和托盘区，算出读数该放的位置
    WindowHelper.cs        置顶、不抢焦点、物理像素定位、全屏检测
  UI/
    WidgetWindow.xaml      任务栏读数
    PopupWindow.xaml       悬停卡片
    SettingsWindow.xaml    设置
    Theme.cs               跟随系统亮/暗（读数跟任务栏主题，卡片跟应用主题）
```

读数窗口是**置顶的顶层工具窗口**，停在托盘区左侧的空白处，而不是像 TrafficMonitor 那样 `SetParent` 挂进 `Shell_TrayWnd`。视觉效果一样，但 explorer.exe 重启时不会被连带销毁——子窗口会随父窗口一起 `DestroyWindow`。

## 致谢

交互形态参考了 [zhongyang219/TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)，代码为全新实现。
