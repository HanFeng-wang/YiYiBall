# 语音链路模型裸调验证（老规矩：接入前先验证参数）
# 验证：①qwen3-asr-flash 整段 wav 识别  ②tongyi-intent-detect-v3 意图分类
#       ③deepseek-v4.1-flash 结构化规划  ④qwen3.8-omni-flash tools 参数支持
# 用法：pwsh -File tools\verify-voice-models.ps1

$ErrorActionPreference = 'Continue'
$base = 'https://maas.qianwenaiapi.com/compatible-mode/v1'
$key = [Environment]::GetEnvironmentVariable('QIANWEN_API_KEY', 'User')
if (-not $key) { $key = $env:QIANWEN_API_KEY }
if (-not $key) { Write-Host '未找到 QIANWEN_API_KEY'; exit 1 }
$headers = @{ Authorization = "Bearer $key" }

# 0) 用 SAPI 生成一段中文测试 wav
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$zh = $synth.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -like 'zh*' } | Select-Object -First 1
if ($zh) { $synth.SelectVoice($zh.VoiceInfo.Name) }
$wav = Join-Path $env:TEMP 'asr_test.wav'
$synth.SetOutputToWaveFile($wav, (New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)))
$synth.Speak('帮我打开微信')
$synth.SetOutputToDefaultAudioDevice()
$synth.Dispose()
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($wav))
Write-Host ("测试 wav：{0} 字节（base64 {1}）" -f (Get-Item $wav).Length, $b64.Length)

function Post($tag, $body) {
    $json = $body | ConvertTo-Json -Depth 12
    try {
        $r = Invoke-RestMethod -Uri "$base/chat/completions" -Method Post -Headers $headers -ContentType 'application/json' -Body $json -TimeoutSec 90
        Write-Host "[$tag] OK -> " + $r.choices[0].message | ConvertTo-Json -Depth 8 -Compress
        return $r
    } catch {
        Write-Host "[$tag] FAIL -> $($_.Exception.Message)"
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
            $d = $_.ErrorDetails.Message
            Write-Host ("  detail: " + $d.Substring(0, [Math]::Min(600, $d.Length)))
        }
        return $null
    }
}

Write-Host "`n===== ① ASR：qwen3-asr-flash（input_audio 整段 wav）====="
$r1 = Post 'asr' @{
    model = 'qwen3-asr-flash'
    messages = @(@{ role = 'user'; content = @(
        @{ type = 'input_audio'; input_audio = @{ data = $b64; format = 'wav' } }) })
}

Write-Host "`n===== ② 意图路由：tongyi-intent-detect-v3 ====="
$r2 = Post 'intent' @{
    model = 'tongyi-intent-detect-v3'
    messages = @(
        @{ role = 'system'; content = '你是意图分类器。根据用户语音指令判断：日常对话、提问、闲聊输出 chat；要求执行电脑操作输出 task。只输出 chat 或 task 两个词之一，不输出其他内容。' },
        @{ role = 'user'; content = '帮我打开微信' })
}

Write-Host "`n===== ③ 任务规划：deepseek-v4.1-flash（结构化 JSON）====="
$r3 = Post 'plan' @{
    model = 'deepseek-v4.1-flash'
    messages = @(
        @{ role = 'system'; content = '你是电脑操作任务规划器。根据用户指令输出一个 JSON 对象，格式二选一：打开应用 {"action":"open_app","args":{"name":"应用名"}}；截图翻译 {"action":"screenshot_translate","args":{}}。只输出 JSON，不要解释。' },
        @{ role = 'user'; content = '帮我打开微信' })
}

Write-Host "`n===== ④ omni tools 支持：qwen3.8-omni-flash ====="
$tools = @(@{ type = 'function'; function = @{
    name = 'open_app'; description = '打开电脑上的应用程序'
    parameters = @{ type = 'object'; properties = @{ name = @{ type = 'string'; description = '应用名，如 微信' } }; required = @('name') }
} })
$r4 = Post 'omni-tools' @{
    model = 'qwen3.8-omni-flash'
    messages = @(@{ role = 'user'; content = '帮我打开微信' })
    tools = $tools
}

Write-Host "`n===== 汇总 ====="
Write-Host ("ASR 文本:   " + $(if ($r1) { $r1.choices[0].message.content } else { 'FAIL' }))
Write-Host ("意图分类:   " + $(if ($r2) { $r2.choices[0].message.content } else { 'FAIL' }))
Write-Host ("规划 JSON:  " + $(if ($r3) { $r3.choices[0].message.content } else { 'FAIL' }))
if ($r4) {
    $m = $r4.choices[0].message
    Write-Host ("omni tools: finish=" + $r4.choices[0].finish_reason + " tool_calls=" + $(if ($m.tool_calls) { ($m.tool_calls | ConvertTo-Json -Depth 6 -Compress) } else { '无（退化文本：' + $m.content + '）' }))
} else { Write-Host 'omni tools: FAIL' }
