#requires -Version 5.1
[CmdletBinding()]
param([string]$CustomModelName = 'Custom local GGUF', [string]$CustomModelLicensePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$dialog = New-Object System.Windows.Forms.OpenFileDialog
$dialog.Title = 'Select the externally prepared GGUF model'
$dialog.Filter = 'GGUF model (*.gguf)|*.gguf'
$dialog.CheckFileExists = $true
try {
    if ($dialog.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { Write-Host 'Cancelled. No package was created.'; exit 0 }
    $selectedModel = $dialog.FileName
} finally { $dialog.Dispose() }
$options = @{ CustomModelPath = $selectedModel; CustomModelName = $CustomModelName; ModelRuntimeOnly = $true }
if ($CustomModelLicensePath) { $options.CustomModelLicensePath = $CustomModelLicensePath }
& (Join-Path $PSScriptRoot 'Prepare-OfflineBundle.ps1') @options
