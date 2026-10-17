# Chunson Space

Windows 桌面工具，包含完整版、简洁版和共享的北京时间小组件。

| 版本 | 内容 |
| --- | --- |
| `full` 完整版 | 自有账号档案、独立浏览器、代理与 IP 管理、模型测试、受管 Codex 分身 |
| `simple` 简洁版 | FlClash 出口检测、Windows 国家/地区与时区同步、英文 Chrome/Edge 登录浏览器 |
| `taskbar-clock` | 两版内置的北京时间任务栏小组件，固定 UTC+8，与系统时区无关 |

两个版本都提供“同步地区与时区”。同步需要主动点击；保留 Windows 显示语言、地区格式和输入法，退出不恢复旧时区。小组件随软件启动，两版同时打开只保留一个；右键可以退出，软件里的“北京时间”按钮可以重新打开。关闭主软件不会关闭时钟。

完整版首页的“导入本机账号”可复制本机 Codex 已保存的 ChatGPT 授权，无需再次登录。新档案默认跟随本机网络和时区；重复导入只更新同账号授权，保留已有独立 IP 与时区。导入后可自行开始模型测试；导入本身不会发送测试请求，也不修改本机 Codex 的授权或运行状态。

简洁版使用本机 Chrome 或 Edge，独立保存英文浏览器资料，打开 ChatGPT 与 `https://ippure.com/claude`。FlClash 仅开 TUN 也支持，但主 HTTP/mixed 入口须开启。相同入口不保证所有域名被 FlClash 分配到相同出口。

## 运行与构建

运行发布包：Windows 10/11 x64，解压完整目录后打开 EXE。包内包含 .NET 运行时；时钟使用 Windows 自带 .NET Framework/WPF。专用浏览器仍需本机已有 Chrome 或 Edge，网络功能需要 FlClash。

从源码构建：Windows x64、.NET 10 SDK、Windows PowerShell。根目录执行：

```powershell
./build.ps1 -Edition All -Verify -Package -Output ./out/release
```

`-Edition Full` 或 `Simple` 可单独构建。`-DotnetPath` 可指定 SDK。首次构建从 NuGet 还原固定版本依赖；完整版从 Mihomo 官方发布地址下载固定版本并核对 SHA-256。构建只操作输出目录，不启动账号登录、修改代理或同步系统设置。离线检查包含界面渲染；测试窗口不占据桌面、不抢焦点。

## 数据与边界

账号授权、浏览器资料、订阅和个人配置只在本机用户目录保存，不包含在这个仓库。不要把自己的运行目录提交到 GitHub。北京时间位置保存在 `%LOCALAPPDATA%/ChunsonWidgets`。

本工具不保证网站登录一定成功，也不判断账号风控状态。账号请求使用用户主动配置和授权的网络；系统同步、小组件显示、网站实际登录是不同的验收项目。

## 许可

原创部分采用 [MIT License](LICENSE)：允许使用、修改、分发及商用，须保留版权声明和许可文本。

第三方组件遵循其原许可证。Mihomo 是独立外部进程，适用 GPL-3.0；其版权、源码地址和许可证位于 `full/windows/ThirdParty/Mihomo`。YamlDotNet、GeoTimeZone、ModelTrace 及字体的声明分别保留在相应 `ThirdParty` 目录；本项目的 MIT 许可不替代它们的许可证。
