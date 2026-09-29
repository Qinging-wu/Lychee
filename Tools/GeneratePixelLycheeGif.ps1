$ErrorActionPreference = 'Stop'

$normalFrame = @(
    '................'
    '.......OO.......'
    '.....OOOOOO.....'
    '....OHHHHGGO....'
    '...OHHHHGGGGO...'
    '..OHHHHGGGGSSO..'
    '..OHHHGGGGGSSO..'
    '..OGEGGGGEGSSO..'
    '..OHHGGGGGSSSO..'
    '..OGGGGEEGSSSO..'
    '..OGGGGGGGSSSO..'
    '...OGGGGGSSO....'
    '....OGGGGSSO....'
    '.....OOOOOO.....'
    '......OOOO......'
    '................'
)

$blinkFrame = $normalFrame.Clone()
$blinkFrame[7] = '..OEEGGGGEESSO..'

foreach ($frame in @($normalFrame, $blinkFrame)) {
    if ($frame.Count -ne 16 -or @($frame | Where-Object { $_.Length -ne 16 }).Count -gt 0) {
        throw 'Every animation frame must be exactly 16 by 16 pixels.'
    }
}

$palette = @{
    '.' = 0 # transparent
    'O' = 1 # outline and open eyes: #1E1E2E
    'E' = 1
    'G' = 2 # body: #B5DE56
    'H' = 3 # upper-left highlight: #E4F7A9
    'S' = 4 # lower-right shade: #8FB13F
}

$global:gifBytes = [System.Collections.Generic.List[byte]]::new()
function Add-GifByte([int] $value) {
    $global:gifBytes.Add([byte]($value -band 255))
}
function Add-GifText([string] $value) {
    foreach ($item in [System.Text.Encoding]::ASCII.GetBytes($value)) {
        $global:gifBytes.Add($item)
    }
}
function Add-GifFrame([string[]] $rows, [int] $delayHundredths) {
    # Graphic Control Extension: restore to transparent background and use the supplied delay.
    Add-GifByte 0x21; Add-GifByte 0xF9; Add-GifByte 4; Add-GifByte 9
    Add-GifByte $delayHundredths; Add-GifByte ($delayHundredths -shr 8)
    Add-GifByte 0; Add-GifByte 0

    # Full-canvas image descriptor using the global color table.
    Add-GifByte 0x2C
    foreach ($value in @(0, 0, 0, 0, 16, 0, 16, 0, 0)) { Add-GifByte $value }
    Add-GifByte 3 # LZW minimum code size for this palette

    # Emit a clear code before each literal. This keeps the tiny pixel-art encoder simple
    # and deterministic while producing a standards-compliant GIF LZW stream.
    $packed = [System.Collections.Generic.List[byte]]::new()
    $buffer = 0
    $bitCount = 0
    foreach ($row in $rows) {
        foreach ($pixel in $row.ToCharArray()) {
            foreach ($code in @(8, [int]$palette[[string]$pixel])) {
                $buffer = $buffer -bor ($code -shl $bitCount)
                $bitCount += 4
                while ($bitCount -ge 8) {
                    $packed.Add([byte]($buffer -band 255))
                    $buffer = $buffer -shr 8
                    $bitCount -= 8
                }
            }
        }
    }
    $buffer = $buffer -bor (9 -shl $bitCount) # end-of-information code
    $bitCount += 4
    while ($bitCount -gt 0) {
        $packed.Add([byte]($buffer -band 255))
        $buffer = $buffer -shr 8
        $bitCount -= 8
    }

    for ($offset = 0; $offset -lt $packed.Count; $offset += 255) {
        $length = [Math]::Min(255, $packed.Count - $offset)
        Add-GifByte $length
        for ($index = 0; $index -lt $length; $index++) { Add-GifByte $packed[$offset + $index] }
    }
    Add-GifByte 0
}

Add-GifText 'GIF89a'
foreach ($value in @(16, 0, 16, 0, 0xF2, 0, 0)) { Add-GifByte $value }

# Global palette: transparent, outline, body, highlight, shade, then padding entries.
foreach ($color in @(
    @(0, 0, 0),
    @(0x1E, 0x1E, 0x2E),
    @(0xB5, 0xDE, 0x56),
    @(0xE4, 0xF7, 0xA9),
    @(0x8F, 0xB1, 0x3F),
    @(0, 0, 0), @(0, 0, 0), @(0, 0, 0)
)) {
    foreach ($channel in $color) { Add-GifByte $channel }
}

# Loop forever.
foreach ($value in @(0x21, 0xFF, 11)) { Add-GifByte $value }
Add-GifText 'NETSCAPE2.0'
foreach ($value in @(3, 1, 0, 0, 0)) { Add-GifByte $value }

Add-GifFrame $normalFrame 250 # eyes open for 2.5 seconds
Add-GifFrame $blinkFrame 12   # brief blink for 0.12 seconds
Add-GifByte 0x3B

$outputPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Assets\pixel-lychee.gif'))
[System.IO.File]::WriteAllBytes($outputPath, $global:gifBytes.ToArray())
Write-Output "Created 16x16 two-frame Pixel Lychee GIF: $outputPath"
