# 自用截图：Agent 开发约定

## 工作环境和目录

使用当前云任务已有 checkout，不主动创建 worktree。SDK 由 global.json 固定为 .NET 8.0.4xx。Linux 可以编译 WPF 和运行 Core 测试，实际桌面、原生 OCR 和浏览器输入必须在 Windows 交互桌面验证；不能把 Linux 编译或 CI 缺少语言组件的 Unrun 写成桌面功能通过。

- src/Snip.Core：坐标、标注、滚动拼接、服务契约、平台无关诊断日志。
- src/Snip.Infrastructure：Win32/WPF/WinRT、OCR、浏览器输入、渲染和托盘。
- src/Snip.App：UI、ViewModel、工作流组合；组合根在 App.xaml.cs。
- src/Snip.Translation：ONNX 模型下载校验和本地推理。
- tests/Snip.Windows.Checks/Fixtures/scrolling：版本控制的公开网页样例。

## 修改和验证

先读相关代码和当前验证记录；错误反馈优先查看诊断包的 environment.json 和对应 OperationId 的日志。明确是否复现、发生阶段与证据，再做范围最小的修复。遇到新的桌面缺陷补真实失败场景的回归用例，不写只重复实现逻辑的测试。

快速验证（PowerShell 7）：

```powershell
./packaging/Validate.ps1 -Scope Core
# 已经完成 Release 构建时可省去重复构建
./packaging/Validate.ps1 -Scope Core -NoBuild
# Windows 交互桌面：渲染/响应、真实模型、Chrome、原生英语 OCR
./packaging/Validate.ps1 -Scope Desktop -NoBuild
```

无 PowerShell 时可 dotnet build Snip.sln -c Release --maxcpucount:2，再 dotnet test tests/Snip.Core.Tests/Snip.Core.Tests.csproj -c Release --no-build。保持退出码，不提交编译失败的修改去等待远程 CI。原生检查不得通过 Wine 或 headless 浏览器冒充 Windows 桌面验收。

WPF 控件只在 Dispatcher 线程访问；BitmapSource 跨线程前必须 Freeze，异步等待前读取需要的尺寸。耗时的编码、语言组件初始化和模型工作放后台；任务应支持取消、超时、迟到结果保护。不得用无限 Task.Run 重试掩盖原生挂起。

## 诊断与隐私

DiagnosticJournal 的 metadata 只传技术数据：尺寸、计数、版本、引擎阶段、耗时、匹配误差。禁止传截图/PNG、OCR 原文、译文、联系人、窗口标题、网页 URL、异常消息、路径、用户名、密钥或环境变量值。异常由日志器保留类型、HResult 和去除文件路径的堆栈，不使用 Exception.ToString。诊断落盘失败不得阻塞截图；默认日志最多 5 × 2 MiB，内存保留最近 200 条。

## 交付

版本源为 Directory.Build.props。主分支验证通过后自动生成 GitHub prerelease，便携 ZIP 带 build-info.json、检查记录和完整提交号；失败时不发布。正式 v<Version> 标签才生成安装包，安装包重用同提交已测试便携 EXE。

不要再向 Git 提交 EXE/便携 ZIP/源码 ZIP；downloads 中是旧版历史文件。GitHub Releases 附件提供直接下载。SkipChecks 仅用于当前提交已经验证的 CI 阶段；重用便携包须核对版本和提交。源码包排除 downloads、本机缓存、Git 元数据。

每项功能在变更文档列出修改文件、验证证据和边界。用户看不到云环境本地文件链接时，给 GitHub 页面/Release 下载链接。后续环境问题与代码缺陷分别记录；不得重复索要已有授权或在日志/输出中打印凭据。
