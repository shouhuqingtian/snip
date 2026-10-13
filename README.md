# 自用截图

使用 C#、WPF、.NET 8 和 MVVM 架构实现的 Windows 区域截图及标注软件。

## 下载

在 GitHub 文件页面点击 **Download raw file**（下载原始文件）：

- [便携版 ZIP](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-portable.zip)：解压后运行 `Snip.exe`，无需安装 .NET。
- [中文安装包](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-setup.exe)：按当前用户安装，支持卸载。
- [完整源码 ZIP](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-source.zip)。
- [文件校验值](downloads/SHA256SUMS.txt)。

Windows 10 22H2 / Windows 11 x64 为运行目标。当前已通过编译和核心测试，实际桌面功能仍需 Windows 实机验收，见验证记录。

## 使用

支持 Windows 10 22H2 / Windows 11 x64。启动后常驻托盘，按 **Ctrl+Alt+A** 截图，或通过托盘打开主窗口。左键拖动选区，可跨显示器；松开鼠标直接进入选区编辑，绿色对勾复制并结束；可打开独立编辑窗口。Esc 先取消当前输入/绘制，再取消截图。

2.0 新增选区内浮动工具栏、原位文字、标注缩放/填充、表情/图片贴纸、置顶贴图、离线 OCR/中英翻译和浏览器自动长截图。微信 4.1.15.12 界面自动化发送已加入核实流程，仍需用户 Windows 端完成控件适配，未宣称已可发送。

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
- `artifacts/自用截图-2.0.0-win-x64.zip`：便携交付包。
- `artifacts/installer/SelfSnip-2.0.0-Setup-x64.exe`：按当前用户安装，无需管理员权限。
- `artifacts/SHA256SUMS.txt`：输出文件校验值。

初次启动时 .NET 会将部分原生组件解压到用户临时目录；便携 EXE 不依赖预先安装 .NET。

## 项目结构

| 项目/目录 | 责任 |
| --- | --- |
| `Snip.Core` | 图像像素坐标、选区、标注、历史、OCR/AI 接口 |
| `Snip.Infrastructure` | Windows 屏幕采集、全局快捷键、托盘、绘图、导出 |
| `Snip.App` | WPF UI、ViewModel、应用服务与依赖组合 |
| `Snip.Translation` | 本地 ONNX 翻译、模型清单及下载校验 |
| `Snip.Core.Tests` | 不依赖桌面的核心回归测试 |
| `Snip.Windows.Checks` | STA 环境下的渲染、PNG 及编辑行为检查 |
| `packaging` | 自包含发布与 NSIS 配置 |

完整设计见 [需求与设计](docs/第一阶段需求与设计.md)，每项功能涉及的文件见 [功能变更记录](docs/功能变更记录.md)，第二阶段逐功能文件与检查结果见 [变更与验证](docs/第二阶段功能变更与验证.md)。

## 扩展

OCR/AI 契约位于 `Snip.Core/Services.cs`。后续在 Infrastructure 中实现 `IOcrService` / `IAiImageService`，在组合根注入，保持 ViewModel 与供应商无关。在线 OCR/AI 供应商留待后续；当前识别与翻译不上传图片或文字，联网仅用于下载模型。

项目已有 Windows CI 构建/测试/打包工作流。推送源码后由 GitHub Actions 执行 Windows 检查；执行状态以仓库 Actions 页面为准。

离线翻译集成检查（模型目录可在首次使用时通过 --install 自动下载约 221 MB）：

```powershell
dotnet run --project tests/Snip.Translation.Checks/Snip.Translation.Checks.csproj -c Release -- .model-cache/translation --install
```
