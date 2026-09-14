# Convert PNG to multi-size .ico (embedded PNG, Vista+ format).
param(
    [Parameter(Mandatory=$true)][string]$Png,
    [Parameter(Mandatory=$true)][string]$Ico
)

Add-Type -AssemblyName System.Drawing

$src = [System.Drawing.Image]::FromFile($Png)
try {
    $sizes = @(16, 24, 32, 48, 64, 128, 256)

    $entries = New-Object System.Collections.Generic.List[System.Object]
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($src, $s, $s)
        try {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $bytes = $ms.ToArray()
            $ms.Dispose()
            $entries.Add(@{ Size = $s; Bytes = $bytes })
        } finally {
            $bmp.Dispose()
        }
    }

    $count = $entries.Count
    $headerLen = 6
    $entryLen = 16
    $dataOffset = $headerLen + $entryLen * $count

    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($out)

    $bw.Write([uint16]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]$count)

    $offset = $dataOffset
    foreach ($e in $entries) {
        $s = $e.Size
        $dim = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$dim)
        $bw.Write([byte]$dim)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Write([uint16]1)
        $bw.Write([uint16]32)
        $bw.Write([uint32]$e.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $e.Bytes.Length
    }

    foreach ($e in $entries) {
        $bw.Write($e.Bytes)
    }

    $bw.Flush()
    [System.IO.File]::WriteAllBytes($Ico, $out.ToArray())
    $bw.Dispose()
    $out.Dispose()

    Write-Output ("Generated " + $Ico + " with " + $count + " sizes")
} finally {
    $src.Dispose()
}