[CmdletBinding()]
param(
    [string] $PublishDirectory,
    [string] $MakePriPath,
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $OutputPath) { $OutputPath = Join-Path $repository '.runtime\qa-app-icons\results.json' }
$output = [IO.Path]::GetFullPath($OutputPath)
[void] [IO.Directory]::CreateDirectory((Split-Path -Parent $output))
$assets = if ($PublishDirectory) { Join-Path ([IO.Path]::GetFullPath($PublishDirectory)) 'Assets' }
          else { Join-Path $repository 'src\ClearCast\Assets' }
$required = @{
    'StoreLogo.png' = 50; 'StoreLogo.scale-200.png' = 100; 'StoreLogo.scale-400.png' = 200
    'Square44x44Logo.png' = 44; 'Square44x44Logo.scale-200.png' = 88; 'Square44x44Logo.scale-400.png' = 176
    'Square150x150Logo.png' = 150; 'Square150x150Logo.scale-200.png' = 300; 'Square150x150Logo.scale-400.png' = 600
}
# Exact Shell sizes and both unplated theme qualifiers prevent the regression
# where only default 44/150 logos existed and Windows supplied a backplate.
foreach ($size in 16,20,24,30,32,36,40,48,60,64,72,80,96,256) {
    foreach ($suffix in '', '_altform-unplated', '_altform-lightunplated') {
        $required["Square44x44Logo.targetsize-$size$suffix.png"] = $size
    }
}
Add-Type -AssemblyName System.Drawing
$checks = @()
foreach ($name in @($required.Keys | Sort-Object)) {
    $path = Join-Path $assets $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing Shell icon: $name" }
    $image = [Drawing.Bitmap]::new($path)
    try {
        $size = $required[$name]
        if ($image.Width -ne $size -or $image.Height -ne $size -or
            $image.GetPixel(0, 0).A -ne 0 -or $image.GetPixel($size-1, 0).A -ne 0 -or
            $image.GetPixel(0, $size-1).A -ne 0 -or $image.GetPixel($size-1, $size-1).A -ne 0 -or
            $image.GetPixel([int]($size/2), [int]($size/2)).A -eq 0) {
            throw "Incorrect dimensions or lost transparent corners: $name"
        }
        $checks += [ordered]@{file=$name;size=$size;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    } finally { $image.Dispose() }
}

$priVerified = $false
if ($PublishDirectory) {
    if (-not $MakePriPath -or -not (Test-Path -LiteralPath $MakePriPath -PathType Leaf)) {
        throw 'A Windows SDK MakePriPath is required to verify published Shell resources.'
    }
    $pri = Join-Path $PublishDirectory 'resources.pri'
    if (-not (Test-Path -LiteralPath $pri)) { $pri = Join-Path $PublishDirectory 'ClearCast.pri' }
    $indexes = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $PublishDirectory -File -Filter (([IO.Path]::GetFileNameWithoutExtension($pri)) + '*.pri'))) {
        $dump = Join-Path (Split-Path -Parent $output) ([IO.Path]::GetFileNameWithoutExtension($output) + '-' + $file.Name + '.xml')
        & $MakePriPath dump /if $file.FullName /of $dump /dt detailed /o *> ($dump + '.log')
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the published resource index.' }
        $indexes += [xml](Get-Content -LiteralPath $dump -Raw)
    }
    $paths = @($indexes | ForEach-Object { $_.SelectNodes('//Candidate/Value') } | ForEach-Object { $_.InnerText.Replace('\','/') })
    foreach ($name in $required.Keys) {
        if ($paths -notcontains ('Assets/' + $name)) { throw "Shell icon absent from PRI: $name" }
    }
    foreach ($size in 16,20,24,30,32,36,40,48,60,64,72,80,96,256) {
        foreach ($form in 'unplated','lightunplated') {
            $path = "Assets/Square44x44Logo.targetsize-${size}_altform-$form.png"
            $candidate = @($indexes | ForEach-Object { $_.SelectNodes('//Candidate') } | Where-Object {
                @($_.SelectNodes('Value') | ForEach-Object { $_.InnerText.Replace('\','/') }) -contains $path
            })
            $qualifiers = if ($candidate.Count -eq 1) { @($candidate[0].SelectNodes('QualifierSet/Qualifier')) } else { @() }
            if ($candidate.Count -ne 1 -or
                @($qualifiers | Where-Object { $_.name -eq 'TargetSize' -and $_.value -eq [string]$size }).Count -ne 1 -or
                @($qualifiers | Where-Object { $_.name -eq 'AlternateForm' -and $_.value -eq $form }).Count -ne 1) {
                throw "Shell qualifiers not indexed for $path"
            }
        }
    }
    $priVerified = $true
}
[ordered]@{passed=$true;assets=$assets;fileCount=$checks.Count;priVerified=$priVerified;files=$checks} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding utf8NoBOM
Get-Item -LiteralPath $output
