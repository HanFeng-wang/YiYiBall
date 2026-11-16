@echo off
rem 一次性准备参考音频：assets/voices 下的参考音色 mp3 -> tools/tts/ref.wav（16k 单声道）
rem 用法：把你的参考音色 mp3 改名为 ref.mp3 放入 assets\voices\ 后运行本脚本
rem 依赖：pip 安装的 soundfile + numpy（无需 ffmpeg）
setlocal
set SRC=%~dp0..\..\assets\voices\ref.mp3
set DST=%~dp0ref.wav
python -c "import soundfile as sf, numpy as np; d, r = sf.read(r'%SRC%', dtype='float32', always_2d=True); m = d.mean(axis=1); n = int(len(m) * 16000 / r); y = np.interp(np.linspace(0, len(m)-1, n), np.arange(len(m)), m); sf.write(r'%DST%', y, 16000, subtype='PCM_16'); print('[OK] 参考音频已生成: %DST%')"
if errorlevel 1 (
    echo [!] 转换失败，请检查 python 与 soundfile/numpy
    exit /b 1
)
echo 下一步：把参考音频的文字内容写入 tools\tts\ref.txt（逐字转写，可用 qwen3-asr-flash）
