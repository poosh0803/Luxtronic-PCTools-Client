# Name: System summary
# Description: Windows version, CPU, RAM, GPU, BIOS and uptime in one list.
$os = Get-CimInstance Win32_OperatingSystem
$cs = Get-CimInstance Win32_ComputerSystem
Write-Output "Computer : $($cs.Manufacturer) $($cs.Model)"
Write-Output "Windows  : $($os.Caption) $($os.Version) (build $($os.BuildNumber))"
Write-Output "Installed: $($os.InstallDate)"
Write-Output "Uptime   : $([int]((Get-Date) - $os.LastBootUpTime).TotalHours) hours"
Write-Output "BIOS     : $((Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion)"
Get-CimInstance Win32_Processor | ForEach-Object { Write-Output "CPU      : $($_.Name.Trim()) ($($_.NumberOfCores) cores / $($_.NumberOfLogicalProcessors) threads)" }
Write-Output "RAM      : $([math]::Round($cs.TotalPhysicalMemory / 1GB, 1)) GB"
Get-CimInstance Win32_VideoController | ForEach-Object { Write-Output "GPU      : $($_.Name) (driver $($_.DriverVersion))" }
