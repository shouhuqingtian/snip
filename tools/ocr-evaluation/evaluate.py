"""Benchmark public synthetic text only; never accepts user screenshots or contacts.

Each candidate runs in a fresh child process. Network access is limited to installing
SHA256-pinned models before timing; recognition itself uses local ONNX files.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import statistics
import subprocess
import sys
import threading
import time
import urllib.request
import zipfile


ROOT = Path(__file__).resolve().parent


def normalize(text):
    import unicodedata
    # Ignore formatting only. Do not correct letters, digits, spelling or word order.
    return "".join(c for c in unicodedata.normalize("NFKC", text) if not c.isspace())


def distance(a, b):
    row = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        next_row = [i]
        for j, y in enumerate(b, 1):
            next_row.append(min(row[j] + 1, next_row[-1] + 1, row[j - 1] + (x != y)))
        row = next_row
    return row[-1]


def download_models(cache):
    import importlib.util
    manifest = json.loads((ROOT / "models.json").read_text())
    cache.mkdir(parents=True, exist_ok=True)
    paths = {}
    for candidate, models in manifest["models"].items():
        paths[candidate] = {}
        for role, spec in models.items():
            path = cache / (candidate + "-" + role + ".onnx")
            if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest() != spec["SHA256"]:
                temporary = path.with_suffix(".download")
                if "wheel_resource" in spec:
                    wheel = cache / "v4-model-data.whl"
                    expected = manifest["model_wheel"]["digests"]["sha256"]
                    if not wheel.exists() or hashlib.sha256(wheel.read_bytes()).hexdigest() != expected:
                        with urllib.request.urlopen(manifest["model_wheel"]["url"], timeout=120) as response:
                            wheel.write_bytes(response.read())
                    if hashlib.sha256(wheel.read_bytes()).hexdigest() != expected:
                        raise ValueError("OCR model data wheel checksum mismatch")
                    with zipfile.ZipFile(wheel) as archive:
                        temporary.write_bytes(archive.read(spec["wheel_resource"]))
                elif "package_resource" in spec:
                    package, relative = spec["package_resource"].split("/", 1)
                    package_root = Path(importlib.util.find_spec(package).origin).parent
                    shutil.copyfile(package_root / relative, temporary)
                else:
                    url = spec.get("mirror", spec["model_dir"])
                    with urllib.request.urlopen(url, timeout=120) as response, temporary.open("wb") as output:
                        while chunk := response.read(1024 * 1024):
                            output.write(chunk)
                if hashlib.sha256(temporary.read_bytes()).hexdigest() != spec["SHA256"]:
                    temporary.unlink()
                    raise ValueError("OCR model checksum mismatch: " + candidate + "/" + role)
                temporary.replace(path)
            paths[candidate][role] = str(path.resolve())
    return paths


def fixtures(folder):
    from PIL import Image, ImageDraw, ImageFont
    if platform.system() == "Windows":
        font_root = Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
        regular, bold = font_root / "msyh.ttc", font_root / "msyhbd.ttc"
        latin = font_root / "segoeui.ttf"
    else:
        regular = Path("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc")
        bold = Path("/usr/share/fonts/opentype/noto/NotoSansCJK-Bold.ttc")
        latin = Path("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf")
    for path in [regular, bold, latin]:
        if not path.is_file():
            raise FileNotFoundError("Benchmark font is missing; cannot substitute silently: " + path.name)
    # Synthetic UI labels, public product names, and made-up English sentences.
    # No samples from user screenshots, diagnostics or OCR output are saved here.
    cases = []
    for size in [12, 18, 28]:
        for name, text, font in [
            ("chinese-label", "是否开启提示：", bold),
            ("chinese-form", "是否需要确认：\n通知方式：\n生效日期：\n处理状态：", bold),
            ("english-word", "Google", latin),
            ("english-product", "Microsoft Edge", latin),
            ("english-sentence", "Please save the selected image before closing.", latin),
            ("mixed", "文件 File 2026\n状态 Status: Ready\n总计 Total: 123.45", regular),
            ("confusable", "Order O0 / Code I1l / Model v2.0", latin),
        ]:
            cases.append((f"{name}-{size}", text, size, font, "#FFFFFF", "#363B42"))
    for name, bg, fg in [("dark", "#20252B", "#F4F6F8"), ("gray", "#F6F7F8", "#777777"), ("blue", "#E9F2FF", "#173C77")]:
        cases.extend([(name + "-chinese", "是否开启提示：\n通知方式：", 18, bold, bg, fg),
                      (name + "-english", "Google\nSave the selected image.", 18, latin, bg, fg)])
    output = []
    folder.mkdir(parents=True, exist_ok=True)
    for name, text, size, path, background, foreground in cases:
        # Render at 3x then downsample to reproduce antialiased UI text, not giant glyphs.
        scale = 3
        font = ImageFont.truetype(str(path), size * scale)
        probe = ImageDraw.Draw(Image.new("RGB", (1, 1)))
        box = probe.multiline_textbbox((0, 0), text, font=font, spacing=size * scale)
        width = int((box[2] - box[0]) / scale) + 32
        height = int((box[3] - box[1]) / scale) + 32
        image = Image.new("RGB", (width * scale, height * scale), background)
        draw = ImageDraw.Draw(image)
        draw.multiline_text((16 * scale - box[0], 16 * scale - box[1]), text, font=font, fill=foreground, spacing=size * scale)
        image = image.resize((width, height), Image.Resampling.LANCZOS)
        target = folder / (name + ".png")
        image.save(target)
        output.append({"name": name, "expected": text, "path": str(target.resolve()), "width": width, "height": height})
    # Large preview with sparse labels and a long list, to expose detector downscaling.
    for name, width, height, count in [("4k-form", 3840, 2160, 12), ("long-page", 900, 4200, 50)]:
        image = Image.new("RGB", (width, height), "white")
        draw = ImageDraw.Draw(image)
        font = ImageFont.truetype(str(regular), 24)
        lines = [f"通知记录 {i:03d} Status Ready" for i in range(1, count + 1)]
        for i, text in enumerate(lines):
            draw.text((40, 30 + i * 76), text, font=font, fill="#363B42")
        target = folder / (name + ".png")
        image.save(target)
        output.append({"name": name, "expected": "\n".join(lines), "path": str(target.resolve()), "width": width, "height": height})
    return output


def worker(config, output):
    # Set these before importing NumPy, OpenCV or ONNX Runtime.
    os.environ["OMP_NUM_THREADS"] = "2"
    os.environ["OPENBLAS_NUM_THREADS"] = "2"
    import cv2
    import psutil
    from rapidocr import RapidOCR, OCRVersion, ModelType
    cv2.setNumThreads(1)
    process = psutil.Process()
    peak = [process.memory_info().rss]
    stop = threading.Event()

    def sample():
        while not stop.wait(0.02):
            peak[0] = max(peak[0], process.memory_info().rss)

    sampler = threading.Thread(target=sample, daemon=True)
    sampler.start()
    ver, model_type = config["candidate"].rsplit("-", 1)
    start = time.perf_counter()
    engine = RapidOCR(params={
        "Det.model_path": config["paths"]["det"], "Rec.model_path": config["paths"]["rec"],
        "Det.ocr_version": OCRVersion(ver), "Rec.ocr_version": OCRVersion(ver),
        "Det.model_type": ModelType(model_type), "Rec.model_type": ModelType(model_type),
        "Det.mean": [0.5, 0.5, 0.5] if ver == "PP-OCRv6" else [0.485, 0.456, 0.406],
        "Det.std": [0.5, 0.5, 0.5] if ver == "PP-OCRv6" else [0.229, 0.224, 0.225],
        "Global.use_cls": False, "Global.log_level": "critical",
        "EngineConfig.onnxruntime.intra_op_num_threads": 2,
        "EngineConfig.onnxruntime.inter_op_num_threads": 1,
        "EngineConfig.onnxruntime.enable_cpu_mem_arena": False,
    })
    load_ms = (time.perf_counter() - start) * 1000
    rows = []
    cpu_start = process.cpu_times()
    try:
        for fixture in config["fixtures"]:
            elapsed = []
            recognized = ""
            peak[0] = process.memory_info().rss
            for _ in range(3):
                start = time.perf_counter()
                result = engine(fixture["path"], use_cls=False)
                elapsed.append((time.perf_counter() - start) * 1000)
                current = "\n".join(result.txts or [])
                if recognized and current != recognized:
                    raise ValueError("Non-deterministic OCR text for " + fixture["name"])
                recognized = current
            expected, actual = normalize(fixture["expected"]), normalize(recognized)
            rows.append({"name": fixture["name"], "expected": fixture["expected"], "actual": recognized,
                         "exact": expected == actual, "errors": distance(expected, actual), "characters": len(expected),
                         "first_ms": round(elapsed[0], 2), "warm_median_ms": round(statistics.median(elapsed[1:]), 2),
                         "sampled_peak_rss_mib": round(max(peak[0], process.memory_info().rss) / 1048576, 2),
                         "width": fixture["width"], "height": fixture["height"]})
    finally:
        cpu_end = process.cpu_times()
        peak[0] = max(peak[0], process.memory_info().rss)
        stop.set()
        sampler.join()
    report = {"candidate": config["candidate"], "load_ms": round(load_ms, 2),
              "cold_to_first_result_ms": round(load_ms + rows[0]["first_ms"], 2),
              "sampled_peak_rss_mib": max(r["sampled_peak_rss_mib"] for r in rows),
              "model_bytes": sum(Path(p).stat().st_size for p in config["paths"].values()),
              "cpu_seconds": round(cpu_end.user + cpu_end.system - cpu_start.user - cpu_start.system, 3),
              "rows": rows}
    Path(output).write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path("artifacts/ocr-evaluation"))
    parser.add_argument("--cache", type=Path, default=Path(".model-cache/ocr-evaluation"))
    parser.add_argument("--worker", type=Path)
    args = parser.parse_args()
    if args.worker:
        worker(json.loads(args.worker.read_text(encoding="utf-8")), args.output)
        return
    args.output.mkdir(parents=True, exist_ok=True)
    paths = download_models(args.cache)
    # Dictionary export is installation work, outside every timed inference process.
    import onnxruntime as ort
    session_options = ort.SessionOptions()
    session_options.intra_op_num_threads = 2
    session_options.inter_op_num_threads = 1
    for models in paths.values():
        session = ort.InferenceSession(models["rec"], sess_options=session_options, providers=["CPUExecutionProvider"])
        dictionary = session.get_modelmeta().custom_metadata_map["character"]
        Path(models["rec"]).with_suffix(".txt").write_text("\n".join(dictionary.splitlines()) + "\n", encoding="utf-8")
        del session
    samples = fixtures(args.output / "fixtures")
    (args.output / "fixtures.json").write_text(json.dumps(samples, ensure_ascii=False, indent=2), encoding="utf-8")
    reports = []
    for candidate, models in paths.items():
        config = args.output / (candidate + "-request.json")
        config.write_text(json.dumps({"candidate": candidate, "paths": models, "fixtures": samples}, ensure_ascii=False), encoding="utf-8")
        target = args.output / (candidate + ".json")
        with (args.output / (candidate + ".log")).open("w", encoding="utf-8") as log:
            subprocess.run([sys.executable, str(Path(__file__).resolve()), "--worker", str(config), "--output", str(target)],
                           stdout=log, stderr=subprocess.STDOUT, timeout=240, check=True)
        report = json.loads(target.read_text(encoding="utf-8"))
        normal = [r for r in report["rows"] if r["name"] not in ["4k-form", "long-page"]]
        report["summary"] = {"exact": sum(r["exact"] for r in report["rows"]), "total": len(report["rows"]),
                             "character_error_rate": round(sum(r["errors"] for r in report["rows"]) / sum(r["characters"] for r in report["rows"]), 5),
                             "normal_warm_median_ms": round(statistics.median(r["warm_median_ms"] for r in normal), 2)}
        reports.append(report)
        print(json.dumps({k: report[k] for k in ["candidate", "load_ms", "cold_to_first_result_ms", "sampled_peak_rss_mib", "model_bytes", "summary"]}, ensure_ascii=False), flush=True)
    from importlib.metadata import version
    result = {"platform": platform.platform(), "machine": platform.machine(), "logical_cpu_count": os.cpu_count(),
              "intra_op_threads": 2, "inter_op_threads": 1,
              "packages": {name: version(name) for name in ["rapidocr", "onnxruntime", "psutil", "Pillow"]},
              "fixture_count": len(samples), "reports": reports}
    (args.output / "report.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
