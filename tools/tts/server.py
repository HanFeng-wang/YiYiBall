# -*- coding: utf-8 -*-
"""
ScreenTranslator 本地 TTS 推理服务（ZipVoice 零样本音色克隆，zh-en）。

用途：给 C# 端 TtsService 提供 HTTP 接口。config.json 里 TtsCosyVoiceUrl 填
http://127.0.0.1:9777 即可启用；留空则走 Windows SAPI 兜底。

接口：
  GET  /health -> {"ok":true,"ready":true}
  POST /tts    {"text":"要说的话"} -> audio/wav 字节流（24kHz）

启动（需先安装：pip install sherpa-onnx soundfile numpy -i 清华镜像）：
  python server.py

音色：参考音频 tools/tts/ref.wav（16k 单声道 wav，由 assets/voices 龙小淳 mp3
经 ffmpeg 转换），参考文字 tools/tts/ref.txt（参考音频的逐字内容）。
"""
import json
import io
import os
import sys
import glob
import wave
import struct
import threading
import asyncio
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

# Edge 在线神经语音（微软官方，音质远超本地 ZipVoice 且稳定）：可用则作为首选引擎
try:
    import edge_tts
except ImportError:
    edge_tts = None

EDGE_VOICE = "zh-CN-XiaoyiNeural"   # 晓伊（年轻女声，与助手名「依依」对应）
EDGE_TIMEOUT = 8.0                  # Edge 在线合成超时（秒），超时立即转本地 ZipVoice

import numpy as np
import sherpa_onnx
import soundfile as sf

# PyInstaller 打包后 __file__ 指向临时解压目录，用 exe 所在目录定位：
# 布局约定 {安装目录}\tools\tts\tts_server\tts_server.exe → ROOT=安装目录，models 在其下
if getattr(sys, "frozen", False):
    BASE = os.path.dirname(os.path.abspath(sys.executable))
else:
    BASE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(BASE))          # ScreenTranslator 根目录
MODELS = os.path.join(ROOT, "models")
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 9777

REF_WAV = os.path.join(BASE, "ref24.wav")   # 预重采样 24k：绕过推理内建 16k→24k 重采样器（重复合成时 native 崩溃）
REF_TXT_PATH = os.path.join(BASE, "ref.txt")

_tts = None
_ref_audio = None
_ref_sr = 0
_ref_text = ""
_zip_lock = threading.Lock()


def find_model_dir():
    dirs = [c for c in sorted(glob.glob(os.path.join(MODELS, "*zipvoice*"))) if os.path.isdir(c)]
    # 兜底引擎优先 int8 量化版（fp32 版实测存在 native 崩溃风险，只作备选）
    for c in dirs:
        if "int8" in os.path.basename(c):
            return c
    if dirs:
        return dirs[0]
    raise FileNotFoundError(f"models 目录下找不到 zipvoice 模型目录：{MODELS}")


def init_tts():
    global _tts, _ref_audio, _ref_sr, _ref_text

    mdir = find_model_dir()

    def pick(pattern):
        cands = sorted(glob.glob(os.path.join(mdir, pattern)))
        if not cands:
            raise FileNotFoundError(f"{mdir} 下找不到 {pattern}")
        # 优先 fp32 非量化版（音质），其次 int8
        for c in cands:
            if "fp32" in os.path.basename(c):
                return c
        for c in cands:
            if "int8" in os.path.basename(c):
                return c
        return cands[0]

    tokens = pick("tokens.txt")
    encoder = pick("encoder*.onnx")
    decoder = pick("decoder*.onnx")
    vocoder = os.path.join(MODELS, "vocos_24khz.onnx")
    lexicon = os.path.join(mdir, "lexicon.txt")
    lexicon_en = os.path.join(MODELS, "lexicon-en-us.txt")
    lex = lexicon if os.path.exists(lexicon) else ""
    if os.path.exists(lexicon_en):
        lex = f"{lex},{lexicon_en}" if lex else lexicon_en

    print(f"[tts] model dir: {mdir}")
    print(f"[tts] tokens={tokens}")
    print(f"[tts] encoder={encoder}")
    print(f"[tts] decoder={decoder}")
    print(f"[tts] vocoder={vocoder}")
    print(f"[tts] lexicon={lex}")

    model_config = sherpa_onnx.OfflineTtsModelConfig(
        zipvoice=sherpa_onnx.OfflineTtsZipvoiceModelConfig(
            tokens=tokens,
            encoder=encoder,
            decoder=decoder,
            lexicon=lex,
            vocoder=vocoder,
        ),
        num_threads=2,
        provider="cpu",
    )
    _tts = sherpa_onnx.OfflineTts(sherpa_onnx.OfflineTtsConfig(model=model_config))

    # 参考音频（音色）
    if not os.path.exists(REF_WAV):
        raise FileNotFoundError(f"参考音频不存在：{REF_WAV}（用 ffmpeg 从 assets/voices 转换）")
    _ref_audio, _ref_sr = sf.read(REF_WAV, dtype="float32")
    if _ref_audio.ndim > 1:
        _ref_audio = _ref_audio.mean(axis=1).astype(np.float32)

    # 参考文字（参考音频逐字内容；为空时克隆效果下降）
    if os.path.exists(REF_TXT_PATH):
        _ref_text = open(REF_TXT_PATH, "r", encoding="utf-8-sig").read().strip()
    print(f"[tts] 参考音频 {len(_ref_audio)/_ref_sr:.1f}s @{_ref_sr}Hz，参考文字 {len(_ref_text)} 字")


def synth(text: str) -> bytes:
    # ZipVoice 懒加载：只在 edge 失败、真正需要兜底时才加载（首次约 30-60s）
    global _tts
    with _zip_lock:   # 整个加载+推理串行化：sherpa-onnx 本地推理不适合并发调用
        if _tts is None:
            print("[tts] 首次兜底请求，开始加载 ZipVoice…", flush=True)
            init_tts()
        gen = sherpa_onnx.GenerationConfig()
        gen.reference_audio = _ref_audio
        gen.reference_sample_rate = _ref_sr
        gen.reference_text = _ref_text
        gen.num_steps = 4
        gen.extra = {"min_char_in_sentence": "30"}
        audio = _tts.generate(text, gen)
    # float32 -> 16bit wav
    samples = np.clip(audio.samples, -1.0, 1.0)
    pcm = (samples * 32767).astype("<i2").tobytes()
    buf = io.BytesIO()
    with wave.open(buf, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(audio.sample_rate)
        w.writeframes(pcm)
    return buf.getvalue()


def synth_edge(text: str) -> bytes:
    """Edge 神经语音合成（返回 mp3 字节）。"""
    if edge_tts is None:
        raise RuntimeError("edge-tts 未安装")

    async def _run():
        c = edge_tts.Communicate(text, EDGE_VOICE)
        buf = io.BytesIO()
        async for ch in c.stream():
            if ch["type"] == "audio":
                buf.write(ch["data"])
        return buf.getvalue()

    try:
        data = asyncio.run(asyncio.wait_for(_run(), timeout=EDGE_TIMEOUT))
    except asyncio.TimeoutError as e:
        raise RuntimeError(f"edge 合成超时（>{EDGE_TIMEOUT:.0f}s）") from e
    if len(data) < 200:
        raise RuntimeError(f"edge 合成结果过短：{len(data)} 字节")
    return data


class Handler(BaseHTTPRequestHandler):
    timeout = 30

    def log_message(self, fmt, *args):
        print("[tts]", fmt % args)

    def _send_all(self, code, ctype, body):
        # 本机网络过滤驱动会把「响应头」「响应体」两次 send 拆开时丢掉第二段，
        # 这里把状态行+头+体合成一次 sendall，绕开该问题，也减少小包往返。
        reason = {200: "OK", 404: "Not Found", 500: "Internal Server Error"}.get(code, "OK")
        head = (
            f"HTTP/1.0 {code} {reason}\r\n"
            f"Content-Type: {ctype}\r\n"
            f"Content-Length: {len(body)}\r\n"
            f"Connection: close\r\n\r\n"
        ).encode("latin1")
        try:
            self.connection.sendall(head + body)
        except Exception as e:
            print(f"[tts] 回包失败：{e}")
        self.close_connection = True

    def do_GET(self):
        if self.path == "/health":
            body = json.dumps({"ok": True, "ready": _tts is not None}).encode()
            self._send_all(200, "application/json", body)
        else:
            self._send_all(404, "text/plain; charset=utf-8", b"not found")

    def do_POST(self):
        if self.path != "/tts":
            self._send_all(404, "text/plain; charset=utf-8", b"not found")
            return
        try:
            n = int(self.headers.get("Content-Length", 0))
            req = json.loads(self.rfile.read(n).decode("utf-8"))
            text = str(req.get("text", "")).strip()
            if not text:
                raise ValueError("text 为空")

            # 首选 Edge 神经语音（稳定、音质高）；失败转 ZipVoice 本地克隆
            try:
                data, ctype = synth_edge(text), "audio/mpeg"
                print(f"[tts] edge 合成完成：{len(text)} 字 -> {len(data)} 字节")
            except Exception as e:
                print(f"[tts] edge 合成失败（转 ZipVoice）：{e}")
                data, ctype = synth(text), "audio/wav"
                print(f"[tts] zipvoice 合成完成：{len(text)} 字 -> {len(data)} 字节")

            self._send_all(200, ctype, data)
        except Exception as e:
            print(f"[tts] 合成失败：{e}")
            self._send_all(500, "text/plain; charset=utf-8", str(e).encode("utf-8"))


if __name__ == "__main__":
    # 不在启动时加载 ZipVoice（fp32/int8 实测有 native 崩溃风险，且加载拖慢就绪）：
    # 首选 Edge 常驻，ZipVoice 仅在 edge 失败时按需懒加载兜底
    print(f"[tts] 服务就绪（edge 优先）：http://127.0.0.1:{PORT}  （config.json 的 TtsCosyVoiceUrl 填 http://127.0.0.1:{PORT}）")
    class _Server(ThreadingHTTPServer):
        daemon_threads = True
        allow_reuse_address = True

    _Server(("127.0.0.1", PORT), Handler).serve_forever()
