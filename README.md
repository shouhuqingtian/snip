# 自用截图

使用 C#、WPF、.NET 8 和 MVVM 架构实现的 Windows 区域截图及标注软件。

## 下载

当前开发版 **2.0.25 测试便携版**：[下载便携 ZIP](https://github.com/shouhuqingtian/snip/releases/download/preview-dfb9f4b1b53e/self-snip-preview-win-x64.zip) · [完整源码](https://github.com/shouhuqingtian/snip/releases/download/preview-dfb9f4b1b53e/self-snip-source.zip)。包含检查记录、版本信息和完整提交号。无需配置在线密钥或下载翻译模型即可使用 Windows 本地 OCR；缺翻译资源时默认保留文字识别结果，见 [修改与验证](docs/2.0.15默认OCR与翻译可用性.md)。

2.0.25 新增自适应长文结果、独立放大阅读与在线英文译文的喇叭朗读。音标、词性和拼音分到独立页签；Windows 系统英语语音按需启动，停止后释放。详见 [长文阅读与英文朗读](docs/2.0.25长文阅读与英文朗读.md)。

2.0.24 优化画笔轨迹、贴纸解码、文字布局及长图临时缓冲，整屏采集移出界面线程，并补充诊断内存 / CPU 数据，见 [整体分析、修改文件与实测](docs/2.0.24内存与交互性能分析.md)。

2.0.23 增加默认自动中英互译；在线翻译附美式音标、词性、词义和中文译文拼音，保留完整译文，见 [修改文件、使用与验证](docs/2.0.23自动方向与在线语言注释.md)。

2.0.22 基础离线词典扩充到 **100000 个不同词条，含 5000 条短语**，保留全部旧词条。词库随软件内置，无需下载模型；英文 / 中文反查索引分别按需加载，见 [修改文件、实测与验证](docs/2.0.22十万词条基础词典.md)。

2.0.21 新增“通用设置”：可开关开机自启，默认关闭。文字工作台可在在线大模型、离线模型、基础词典之间选择并“保存为默认”；新截图及重启后沿用，不再反复请求旧的在线服务。基础词典不下载、不联网、不启动模型，仅用于查词和部分短语，见 [修改文件、使用与验证](docs/2.0.21开机自启与默认翻译.md)。

2.0.20 在线翻译增加 OpenAI、智谱 GLM、Moonshot / Kimi、硅基流动，与 DeepSeek、通义千问共六家预设；模型名可直接输入，支持多组自定义兼容接口、独立加密密钥和保存切换。服务故障时保留原文，选择备用配置后重试，见 [修改文件、使用与验证](docs/2.0.20多服务与自定义在线翻译.md)。

2.0.19 网页截图保留三个入口：整个网页、从当前位置向下、捕捉选定区域。第三项进入模式后按住左键重新拖选，碰到网页下沿自动滚动，松开完成，Esc 取消；真实 Windows Chrome 的拖选、越过下沿、末尾像素、取消及选中文字 / 固定侧栏页面检查通过，见 [修改文件、使用与验证](docs/2.0.19网页拖选与边缘自动滚动.md)。

2.0.16 增加可选 Vulkan GPU 离线翻译，支持兼容的 NVIDIA/AMD/Intel 驱动；约 32 MB GPU 运行库单独下载，复用同一模型。无兼容设备时回退 CPU。实体 GPU 加速尚待兼容设备实测，见 [修改与验证](docs/2.0.16通用GPU翻译加速.md)。

2.0.17 优化 CPU 离线翻译：现有模型无需重下，Windows 同轮样例原生峰值内存约减少 21%、耗时缩短约 11%；CPU 累计计算时间有所增加。见 [实测、修改文件与边界](docs/2.0.17离线CPU翻译减负.md)。

下面是历史 **2.0.2** 发行文件：

在 GitHub 文件页面点击 **Download raw file**（下载原始文件）：

- [便携版 ZIP](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-portable.zip)：解压后运行 `Snip.exe`，无需安装 .NET。
- [中文安装包](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-setup.exe)：按当前用户安装，支持卸载。
- [完整源码 ZIP](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-source.zip)。
- [文件校验值](downloads/SHA256SUMS.txt)。

Windows 10 22H2 / Windows 11 x64 为运行目标。当前已通过编译和核心测试，实际桌面功能仍需 Windows 实机验收，见验证记录。

## 使用

支持 Windows 10 22H2 / Windows 11 x64。启动后常驻托盘，按 **Ctrl+Alt+A** 截图，或通过托盘打开主窗口。左键拖动选区，可跨显示器；松开鼠标直接进入选区编辑，绿色对勾复制并结束；可打开独立编辑窗口。Esc 先取消当前输入/绘制，再取消截图。

2.0 系列新增选区内浮动工具栏、原位文字、标注缩放/填充、表情/图片贴纸、置顶贴图、离线 OCR/中英翻译和浏览器自动长截图。微信 4.1.15.12 界面自动化发送已加入核实流程，仍需用户 Windows 端完成控件适配，未宣称已可发送。

已有矩形、椭圆、箭头、画笔、中文文字、马赛克、颜色、线宽、撤销/重做、PNG 保存及剪贴板复制。参见 [使用说明](docs/使用说明.md)。

## 源码构建

安装 .NET 8 SDK（8.0.400 或更新的 8.0.4xx 补丁），在仓库根目录运行：

```powershell
dotnet restore Snip.sln
dotnet build Snip.sln -c Release --no-restore
dotnet test tests/Snip.Core.Tests/Snip.Core.Tests.csproj -c Release --no-build
dotnet run --project tests/Snip.Windows.Checks/Snip.Windows.Checks.csproj -c Release --no-build
dotnet run --project src/Snip.App/Snip.App.csproj -c Release
```

WPF 应用和 Windows 检查只能在 Windows 运行。Linux 可使用 Windows targeting reference packs 编译项目并执行 Core 测试。

## 发布

使用 PowerShell 7 运行发布脚本：

```powershell
# Windows；生成便携 EXE 与 ZIP
./packaging/Publish.ps1
# 同时生成安装包；先安装 NSIS 3.11 或更新版本
./packaging/Publish.ps1 -Installer
# 如无法自动定位 makensis
./packaging/Publish.ps1 -Installer -MakensisPath 'C:\Program Files (x86)\NSIS\makensis.exe'
```

输出目录：

- `artifacts/portable/win-x64/Snip.exe`：包含 .NET 运行时的单文件 EXE。
- `artifacts/自用截图-<版本>-win-x64.zip`：便携交付包，版本来自 `Directory.Build.props`。
- `artifacts/installer/SelfSnip-<版本>-Setup-x64.exe`：按当前用户安装，无需管理员权限。
- `artifacts/SHA256SUMS.txt`：输出文件校验值。

初次启动时 .NET 会将部分原生组件解压到用户临时目录；便携 EXE 不依赖预先安装 .NET。

## 项目结构

| 项目/目录 | 责任 |
| --- | --- |
| `Snip.Core` | 图像像素坐标、选区、标注、历史、OCR/AI 接口 |
| `Snip.Infrastructure` | Windows 屏幕采集、全局快捷键、托盘、绘图、导出 |
| `Snip.App` | WPF UI、ViewModel、应用服务与依赖组合 |
| `Snip.Translation` | 单一 Hy-MT2 GGUF 翻译、CPU 进程生命周期及下载校验 |
| `Snip.Core.Tests` | 不依赖桌面的核心回归测试 |
| `Snip.Windows.Checks` | STA 环境下的渲染、PNG 及编辑行为检查 |
| `packaging` | 自包含发布与 NSIS 配置 |

完整设计见 [需求与设计](docs/第一阶段需求与设计.md)，每项功能涉及的文件见 [功能变更记录](docs/功能变更记录.md)，第二阶段逐功能文件与检查结果见 [变更与验证](docs/第二阶段功能变更与验证.md)。

## 扩展

OCR/AI 契约位于 `Snip.Core/Services.cs`。后续在 Infrastructure 中实现 `IOcrService` / `IAiImageService`，在组合根注入，保持 ViewModel 与供应商无关。OCR 使用 Windows 本地组件；离线翻译不上传图片或文字。可选在线翻译已经支持 DeepSeek/通义千问，开启时会发送识别后的文字；模型首次下载需要联网。

CI 分为快速编译/Core 检查、Windows 桌面集成验证、交付三个阶段。验证通过才发布便携版；普通 main 推送发布测试版，`v<版本>` 标签生成正式安装包并发布。文档和历史下载文件的单独修改不会触发全量流程（使用说明作为包内容仍会触发）。失败日志和截图保留为 Actions 附件，独立检查失败后仍继续收集其余检查结果。

2.0.3 可从主窗口或托盘 **导出诊断包**，反馈问题时提供 ZIP 和操作现象即可；诊断记录不包含截图、识别文本或译文。具体修改文件、流程和验收边界见 [诊断与开发效率改进](docs/2.0.3诊断与开发效率改进.md)。

离线翻译集成检查（模型目录可在首次使用时通过 --install 自动下载约 951 MB 模型与约 20 MB 推理器）：

```powershell
dotnet run --project tests/Snip.Translation.Checks/Snip.Translation.Checks.csproj -c Release -- .model-cache/translation --install
```

2.0.2 修复 OCR/翻译阻塞及 Chrome 自动滚动，并提供整个网页、可见网页和选定区域模式，见 [问题修复与验证](docs/2.0.1问题修复与验证.md)。

2.0.2 改进白底图文长截图，并排除软件进度窗和置顶贴图，见 [长截图修复](docs/2.0.2长截图修复.md)。
