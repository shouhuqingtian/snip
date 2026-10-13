# 下载自用截图 2.0.1

打开下方文件页面，点击 **Download raw file**（下载原始文件）。私有仓库需要先登录有权限的 GitHub 账户。

- [便携版](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-portable.zip)：解压后双击 Snip.exe。
- [安装包](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-setup.exe)：中文安装和卸载。
- [完整源码](https://github.com/shouhuqingtian/snip/blob/main/downloads/self-snip-source.zip)。

运行平台为 Windows 10 22H2 / Windows 11 x64。启动后常驻系统托盘，按 Ctrl+Alt+A 开始截图。主窗口可通过托盘图标双击打开。

2.0 新增选区内工具栏、表情/图片贴纸、标注缩放与填充、贴图、离线 OCR/中英翻译、Chrome/Edge 自动长截图。翻译模型约 221 MB，首次在软件中下载后可断网使用。

2.0.1 修复识别和翻译的界面阻塞，加入阶段、耗时、取消和超时提示；网页截图菜单支持整个网页、可见网页、选定区域及选区长截图。先从托盘退出旧版，再解压/安装；已有翻译模型可继续使用。

[Windows CI](https://github.com/shouhuqingtian/snip/actions/runs/38375183657) 构建、44 项核心测试、21 项 Windows 检查、5 项真实翻译模型检查、3 项真实 Chrome 截图和原生英语 OCR 通过。当前发行包由相同 2.0.1 源码发布，ZIP/安装包内文件与 SHA256 已核对。

软件未签名。微信 4.1.15.12 的实际控件适配仍待完成；此次没有验收微信发送。具体修改文件、使用方法和验证边界见 [2.0.1 修复记录](../docs/2.0.1问题修复与验证.md)。校验值见 [SHA256SUMS.txt](SHA256SUMS.txt)。
