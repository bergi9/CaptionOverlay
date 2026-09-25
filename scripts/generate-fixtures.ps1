# Generates the synthetic WAV fixtures in tests/fixtures (16 kHz, mono, PCM16).
# Speech is produced with the Windows SAPI text-to-speech engine, so the exact
# boundaries of every spoken phrase are known and written to *.labels.json.
#
# Usage: powershell -NoProfile -File scripts/generate-fixtures.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech

$SampleRate = 16000
$outDir = Join-Path $PSScriptRoot '..\tests\fixtures'
New-Item -ItemType Directory -Force $outDir | Out-Null

function New-Silence([double]$seconds) {
    return New-Object byte[] ([int]($seconds * $SampleRate) * 2)
}

function New-Speech([string]$text, [int]$rate = 0) {
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    $voice = $synth.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -like 'en-*' } | Select-Object -First 1
    if ($voice) { $synth.SelectVoice($voice.VoiceInfo.Name) }
    $synth.Rate = $rate
    $ms = New-Object System.IO.MemoryStream
    $fmt = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo($SampleRate, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
    $synth.SetOutputToAudioStream($ms, $fmt)
    $synth.Speak($text)
    $synth.Dispose()
    return Get-TrimmedPcm $ms.ToArray()
}

# Trim leading/trailing near-silence so labels match audible speech.
function Get-TrimmedPcm([byte[]]$pcm) {
    $n = $pcm.Length / 2
    $threshold = 300
    $first = 0
    while ($first -lt $n -and [Math]::Abs([BitConverter]::ToInt16($pcm, $first * 2)) -lt $threshold) { $first++ }
    $last = $n - 1
    while ($last -gt $first -and [Math]::Abs([BitConverter]::ToInt16($pcm, $last * 2)) -lt $threshold) { $last-- }
    $len = ($last - $first + 1) * 2
    $out = New-Object byte[] $len
    [Array]::Copy($pcm, $first * 2, $out, 0, $len)
    return , $out
}

function New-Tones([double]$seconds) {
    # A simple chord progression with a percussive envelope: "music-like" but no speech.
    $n = [int]($seconds * $SampleRate)
    $bytes = New-Object byte[] ($n * 2)
    $chords = @(@(261.63, 329.63, 392.0), @(220.0, 261.63, 329.63), @(174.61, 220.0, 261.63), @(196.0, 246.94, 293.66))
    for ($i = 0; $i -lt $n; $i++) {
        $t = $i / $SampleRate
        $chord = $chords[[int][Math]::Floor($t / 2.0) % 4]
        $beat = $t % 0.5
        $env = [Math]::Exp(-4.0 * $beat)
        $v = 0.0
        foreach ($f in $chord) { $v += [Math]::Sin(2 * [Math]::PI * $f * $t) }
        $v = 0.25 * $v / 3.0 + 0.15 * $env * [Math]::Sin(2 * [Math]::PI * 55.0 * $t)
        $s = [int16]([Math]::Max(-32767, [Math]::Min(32767, $v * 32767)))
        $b = [BitConverter]::GetBytes($s)
        $bytes[2 * $i] = $b[0]; $bytes[2 * $i + 1] = $b[1]
    }
    return , $bytes
}

function Write-Wav([string]$name, [System.Collections.Generic.List[byte[]]]$parts) {
    $total = 0; foreach ($p in $parts) { $total += $p.Length }
    $path = Join-Path $outDir $name
    $fs = [System.IO.File]::Create($path)
    $w = New-Object System.IO.BinaryWriter($fs)
    $w.Write([Text.Encoding]::ASCII.GetBytes('RIFF')); $w.Write([int](36 + $total))
    $w.Write([Text.Encoding]::ASCII.GetBytes('WAVE'))
    $w.Write([Text.Encoding]::ASCII.GetBytes('fmt ')); $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1)
    $w.Write([int]$SampleRate); $w.Write([int]($SampleRate * 2)); $w.Write([int16]2); $w.Write([int16]16)
    $w.Write([Text.Encoding]::ASCII.GetBytes('data')); $w.Write([int]$total)
    foreach ($p in $parts) { $w.Write($p) }
    $w.Dispose()
    Write-Host "wrote $path ($([Math]::Round($total / 2 / $SampleRate, 2)) s)"
}

# Builds a WAV from alternating silence/speech parts and writes the speech boundaries as labels.
function Write-LabelledSpeech([string]$name, [object[]]$script) {
    $parts = New-Object 'System.Collections.Generic.List[byte[]]'
    $labels = @()
    $pos = 0
    foreach ($item in $script) {
        if ($item -is [double] -or $item -is [int]) {
            $bytes = New-Silence ([double]$item)
        }
        else {
            $bytes = New-Speech $item
            $labels += [ordered]@{ startMs = [int]($pos / 2 / $SampleRate * 1000); endMs = [int](($pos + $bytes.Length) / 2 / $SampleRate * 1000); text = $item }
        }
        $parts.Add($bytes)
        $pos += $bytes.Length
    }
    Write-Wav $name $parts
    $labelPath = Join-Path $outDir ([IO.Path]::ChangeExtension($name, '.labels.json'))
    ConvertTo-Json -InputObject @($labels) -Depth 3 | Set-Content -Encoding utf8 $labelPath
}

Write-LabelledSpeech 'speech_en.wav' @(
    1.0,
    'The weather today is sunny with a light breeze from the west.',
    1.5,
    'Please remember to bring your umbrella tomorrow.',
    1.2,
    'Thank you, and have a wonderful evening.',
    1.0
)

Write-LabelledSpeech 'monologue_en_30s.wav' @(
    0.5,
    ('Once upon a time there was a small village at the edge of a great forest, and the people who lived there ' +
     'worked hard every single day, gathering wood and growing vegetables and trading with travellers who came along ' +
     'the old road from the mountains, bringing news of distant cities and strange inventions that nobody in the village ' +
     'had ever seen before, and every evening the children would gather around the fire to listen to these stories ' +
     'until the stars came out and their parents called them home to sleep.'),
    0.5
)

$silence = New-Object 'System.Collections.Generic.List[byte[]]'
$silence.Add((New-Silence 10.0))
Write-Wav 'silence_10s.wav' $silence

$music = New-Object 'System.Collections.Generic.List[byte[]]'
$music.Add((New-Tones 8.0))
Write-Wav 'music_8s.wav' $music
