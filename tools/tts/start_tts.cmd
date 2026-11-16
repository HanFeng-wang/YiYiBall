@echo off
rem 启动本地 TTS 推理服务（ZipVoice 参考音色克隆）
rem 启动后 config.json 的 TtsCosyVoiceUrl 填 http://127.0.0.1:9777 即可启用；
rem 留空则走 Windows SAPI 兜底。
rem sherpa-onnx ZipVoice 连续合成偶发 native 崩溃（无 Python 堆栈），此处守护：退出后 2 秒自动重启。
:loop
cd /d "%~dp0"
python -u server.py 9777 >> tts.log 2>&1
echo [%date% %time%] [tts] 服务退出，2 秒后自动重启 >> tts.log
timeout /t 2 /nobreak >nul
goto loop
