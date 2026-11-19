# ASR 参数变体验证：找出该代理可用的 qwen3-asr-flash 传参形式
$ErrorActionPreference = 'Continue'
$base = 'https://maas.qianwenaiapi.com/compatible-mode/v1'
$key = [Environment]::GetEnvironmentVariable('QIANWEN_API_KEY', 'User')
if (-not $key) { $key = $env:QIANWEN_API_KEY }
$headers = @{ Authorization = "Bearer $key" }
$wav = Join-Path $env:TEMP 'asr_test.wav'
if (-not (Test-Path $wav)) { Write-Host '缺少测试 wav，先运行 verify-voice-models.ps1'; exit 1 }
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($wav))
$dataUrl = "data:audio/wav;base64,$b64"

function TryVariant($tag, $body) {
    try {
        $r = Invoke-RestMethod -Uri "$base/chat/completions" -Method Post -Headers $headers -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 12) -TimeoutSec 90
        Write-Host "[$tag] OK -> $($r.choices[0].message.content)"
        return $true
    } catch {
        $d = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { $_.Exception.Message }
        $d = ($d -replace '\s+', ' ')
        Write-Host "[$tag] FAIL -> $($d.Substring(0, [Math]::Min(260, $d.Length)))"
        return $false
    }
}

# 变体 A：input_audio.data 用完整 data URL
[void](TryVariant 'A data-url' @{ model = 'qwen3-asr-flash'; messages = @(@{ role = 'user'; content = @(
    @{ type = 'input_audio'; input_audio = @{ data = $dataUrl; format = 'wav' } }) }) })

# 变体 B：audio_url 字段 + data URL（DashScope qwen-audio 风格）
[void](TryVariant 'B audio_url' @{ model = 'qwen3-asr-flash'; messages = @(@{ role = 'user'; content = @(
    @{ type = 'audio_url'; audio_url = @{ url = $dataUrl } }) }) })

# 变体 C：audio_url 字段（裸 base64）
[void](TryVariant 'C audio_url-raw' @{ model = 'qwen3-asr-flash'; messages = @(@{ role = 'user'; content = @(
    @{ type = 'audio_url'; audio_url = @{ url = $b64 } }) }) })

# 变体 D：qwen3-omni-flash 识别音频（多模态直读）
[void](TryVariant 'D omni-asr' @{ model = 'qwen3.8-omni-flash'; messages = @(@{ role = 'user'; content = @(
    @{ type = 'input_audio'; input_audio = @{ data = $dataUrl; format = 'wav' } },
    @{ type = 'text'; text = '逐字转写这段音频，只输出转写文本' }) }) })
