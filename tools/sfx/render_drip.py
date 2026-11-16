# -*- coding: utf-8 -*-
"""
渲染「落地 · 果冻弹」语音模式提示音为 wav（与 docs/白条状态提示_WebView2预览.html 同参）。

进入音 voice_in.wav:
    dropRich(0.00, 320→660, .22, glide=.08, decay=.26, harm=.30)   # 主滴（含二次谐波）
    dropNote(0.00, 150→200, .11, glide=.11, decay=.34)             # 低频落地"咚"
    dropSpring(0.17, 640→1280, .15)                                # 回弹（啵嘤弹簧音）
告别音 voice_out.wav（走向向下）:
    dropSpring(0.00, 590→1180, .15)
    dropRich(0.18, 320→660, .20, glide=.08, decay=.26, harm=.30)
    dropNote(0.18, 150→200, .10, glide=.11, decay=.34)

Web Audio 语义：
    频率 exponentialRampToValueAtTime：f(t) = f0 * (f1/f0)^(t/glide)
    包络：0 → linear 8ms → vol → exponential 衰减至 0.0001 @ decay
用法: python tools/sfx/render_drip.py   （输出到 assets/sounds/）
"""
import numpy as np
import wave
import os

SR = 48000


def env(t, vol, decay, attack=0.008):
    """Web Audio 包络：linear attack → exponential decay → 0"""
    g = np.zeros_like(t)
    up = t < attack
    g[up] = vol * t[up] / attack
    mid = (t >= attack) & (t < decay)
    g[mid] = vol * (0.0001 / vol) ** ((t[mid] - attack) / (decay - attack))
    return g


def add_note(buf, start, f0, f1, vol, glide, decay, harm=0.0):
    """dropNote / dropRich：正弦（可选二次谐波）上滑 + 指数衰减"""
    n0 = int(start * SR)
    n1 = int((start + decay + 0.05) * SR)
    if n1 > len(buf):
        n1 = len(buf)
    t = np.arange(n1 - n0) / SR
    # 基频：exponentialRamp 到 glide 后保持 f1
    f = np.where(t < glide, f0 * (f1 / f0) ** (t / glide), f1)
    ph = 2 * np.pi * np.cumsum(f) / SR
    s = np.sin(ph)
    if harm > 0:
        s = s + harm * np.sin(2 * ph)   # 二次谐波（dropRich）
    buf[n0:n1] += s * env(t, vol, decay)


def add_spring(buf, start, f0, f1, vol):
    """dropSpring：快速上滑 → 回弹下沉(0.84) → 回稳(0.93)，Q 弹感"""
    total = 0.37
    n0 = int(start * SR)
    n1 = min(int((start + total) * SR), len(buf))
    t = np.arange(n1 - n0) / SR
    f = np.empty_like(t)
    seg1 = t < 0.06
    seg2 = (t >= 0.06) & (t < 0.17)
    seg3 = (t >= 0.17) & (t < 0.28)
    f[seg1] = f0 * (f1 / f0) ** (t[seg1] / 0.06)
    f[seg2] = f1 * (0.84) ** ((t[seg2] - 0.06) / 0.11)
    f[seg3] = f1 * 0.84 * (0.93 / 0.84) ** ((t[seg3] - 0.17) / 0.11)
    f[t >= 0.28] = f1 * 0.93
    ph = 2 * np.pi * np.cumsum(f) / SR
    buf[n0:n1] += np.sin(ph) * env(t, vol, 0.32)


def render(events, dur, path):
    buf = np.zeros(int(dur * SR))
    for ev in events:
        kind = ev[0]
        if kind == "rich":
            _, start, f0, f1, vol, glide, decay, harm = ev
            add_note(buf, start, f0, f1, vol, glide, decay, harm)
        elif kind == "note":
            _, start, f0, f1, vol, glide, decay = ev
            add_note(buf, start, f0, f1, vol, glide, decay)
        else:
            _, start, f0, f1, vol = ev
            add_spring(buf, start, f0, f1, vol)
    # 首尾 3ms 淡入淡出防爆音
    n = min(len(buf), int(0.003 * SR))
    buf[:n] *= np.linspace(0, 1, n)
    buf[-n:] *= np.linspace(1, 0, n)
    pcm = (np.clip(buf, -1, 1) * 32767).astype(np.int16)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(pcm.tobytes())
    peak = float(np.max(np.abs(buf)))
    print(f"{path}: {dur:.2f}s peak={peak:.2f}")


# 进入音：主滴 + 低频落地咚 + 啵嘤回弹
render([
    ("rich",   0.00, 320,  660, .22, .08, .26, .30),
    ("note",   0.00, 150,  200, .11, .11, .34),
    ("spring", 0.17, 640, 1280, .15),
], 0.85, os.path.join("assets", "sounds", "voice_in.wav"))

# 告别音：回弹（向下走向）→ 主滴落地
render([
    ("spring", 0.00, 590, 1180, .15),
    ("rich",   0.18, 320,  660, .20, .08, .26, .30),
    ("note",   0.18, 150,  200, .10, .11, .34),
], 0.85, os.path.join("assets", "sounds", "voice_out.wav"))

print("done")
