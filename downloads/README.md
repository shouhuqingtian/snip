# 下载自用截图 2.0

打开下方文件页面，点击 **Download raw file**（下载原始文件）。私有仓库需要先登录有权限的 GitHub 账户。

- [便携版](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-portable.zip)：解压后双击 Snip.exe。
- [安装包](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-setup.exe)：中文安装和卸载。
- [完整源码](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-source.zip)。

运行平台为 Windows 10 22H2 / Windows 11 x64。启动后常驻系统托盘，按 Ctrl+Alt+A 开始截图。主窗口可通过托盘图标双击打开。

2.0 新增选区内工具栏、表情/图片贴纸、标注缩放与填充、贴图、离线 OCR/中英翻译、Chrome/Edge 自动长截图。翻译模型约 221 MB，首次在软件中下载后可断网使用。

当前下载包由 [通过的 Windows CI](https://github.com/shouhuqingtian/snip/actions/runs/38368351618) 生成：35 项核心测试、19 项 Windows 检查和 4 项真实翻译模型检查通过。软件未签名，桌面交互实机验收尚待完成。**微信 4.1.15.12 直接发送尚未完成实际控件适配**，可运行 `Snip.exe --wechat-diagnostics` 提供脱敏结构，继续适配。各功能修改文件与验证状态见 [第二阶段变更与验证](../docs/第二阶段功能变更与验证.md)。校验值见 [SHA256SUMS.txt](SHA256SUMS.txt)。
