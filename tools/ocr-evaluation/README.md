# 轻量离线 OCR 评估（独立工具）

此目录只评估候选方案，不接入 Snip.exe，不改变默认 OCR 或添加应用运行依赖。不要传入用户截图、诊断包或识别原文；脚本只生成公开产品名与虚构 UI 标签的 29 张测试图。

## 如何复现

Python 3.12，Windows 10/11 或已有 Noto CJK/DejaVu 字体的 Linux：

```bash
python -m venv .venv-ocr
# 激活后：
python -m pip install -r tools/ocr-evaluation/requirements.txt
python tools/ocr-evaluation/evaluate.py
```

Windows、.NET SDK 8.0.4xx：

```powershell
dotnet run --project tools/ocr-evaluation/NativeBenchmark.csproj -c Release -- artifacts/ocr-evaluation/fixtures.json artifacts/ocr-evaluation/native-windows.json
foreach ($candidate in 'PP-OCRv6-tiny','PP-OCRv6-small') {
    dotnet run --project tools/ocr-evaluation/NativeBenchmark.csproj -c Release --no-build -- artifacts/ocr-evaluation/fixtures.json "artifacts/ocr-evaluation/dotnet-$candidate.json" .model-cache/ocr-evaluation $candidate
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
```

也可运行 GitHub Actions 的 **Evaluate lightweight offline OCR**。此工作流独立于正式交付流水线；评估失败不发布任何应用版本，评估成功也不表示已经改善 Snip.exe。

## 度量口径与限制

- PP-OCRv4 mobile、PP-OCRv6 tiny/small 检测与识别模型均从 RapidAI 官方模型清单固定 SHA256，使用前再次校验。下载站不可达时，使用校验相同的官方 PyPI 包内模型，或 RapidOcrNet 固定提交的镜像，不能更改校验值放行。
- 仅下载模型用于安装准备；识别使用本地 ONNX，不调用网络服务。模型下载和字典提取不计入推理耗时。
- 各候选使用全新子进程，ONNX intra-op 2、inter-op 1；禁用 CPU arena。正常直立截图跳过方向分类推理，Python 仍可能初始化未使用的分类模型，因此同时测量实际 C# 库，不能混用两者内存数据。
- 模型构造可能延迟加载，因此 `load_ms` 不能单独称作冷启动；`cold_to_first_result_ms` 为构造至第一张图结果的耗时，仍不包含解释器启动与模块导入。每图识别三遍，后两遍统计连续耗时。
- Python RSS 每 20 ms 采样，可能漏掉极短峰值；C# 报告 Windows 进程 `PeakWorkingSet64`。包含运行时、解码器、检测与识别，**不是模型文件大小或仅模型增量**；不能用 Python 进程占用预测 Snip.exe 常驻内存。
- 12/18/28 px 中英文字、混排、深色/灰色/蓝色背景，以及 4K 稀疏文字和 4200 px 长图。相似字符 O/0、I/1/l 专门保留错误；不对 OCR 文本做字典替换。
- Exact 仅忽略空白和 Unicode 全角/半角兼容差异，错字、漏字、漏标点均计为失败；字符错误率是编辑距离/预期字符数。完整原始结果保存在 JSON，不能只报告成功样例。
- Linux 和 Windows 使用各自已声明的字体，缺字体直接失败，不能将它们视为同一像素数据。Windows 三个候选及原生引擎使用同一组 Windows 图片。
- 原生 OCR 使用与样本匹配的已安装语言，衡量其最佳语言条件，**不代表现有自动语言选择**。缺中文/英文语言组件的样本标为 Unrun，不计为通过，不安装组件冒充已有能力。
- 小型合成样本不能代表所有真实截图，也不能证明复现了用户截图。必须在接入后补充窗口响应、取消、资源回收与真实截图验收。

PP-OCR/RapidOCR/RapidOcrNet 使用 Apache-2.0；真正接入时须将适用许可证、来源与固定模型校验信息一并交付。
