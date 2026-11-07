# Name: Secure Boot status
# Description: Shows whether the PC booted in UEFI mode and whether Secure Boot is on, plus the TPM state.
$firmware = $env:firmware_type
try {
    $type = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name PEFirmwareType -ErrorAction Stop).PEFirmwareType
    $firmware = if ($type -eq 2) { 'UEFI' } elseif ($type -eq 1) { 'Legacy BIOS' } else { "Unknown ($type)" }
} catch { }
Write-Output "Boot mode  : $firmware"

try {
    $enabled = Confirm-SecureBootUEFI -ErrorAction Stop
    Write-Output ("Secure Boot: {0}" -f $(if ($enabled) { 'ON' } else { 'OFF' }))
} catch [System.PlatformNotSupportedException] {
    Write-Output 'Secure Boot: not supported (Legacy BIOS boot, or the firmware has no Secure Boot)'
} catch {
    Write-Output "Secure Boot: could not be read - $($_.Exception.Message)"
}

try {
    $tpm = Get-Tpm -ErrorAction Stop
    Write-Output "TPM        : present=$($tpm.TpmPresent) ready=$($tpm.TpmReady) enabled=$($tpm.TpmEnabled)"
    $ver = (Get-CimInstance -Namespace 'root\cimv2\Security\MicrosoftTpm' -ClassName Win32_Tpm -ErrorAction SilentlyContinue).SpecVersion
    if ($ver) { Write-Output "TPM version: $(($ver -split ',')[0])" }
} catch {
    Write-Output 'TPM        : could not be read'
}
