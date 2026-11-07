# Name: Disk health
# Description: Physical disks with their health status, and free space on every volume.
Get-PhysicalDisk | Sort-Object DeviceId | Format-Table DeviceId, FriendlyName, MediaType, BusType, HealthStatus, OperationalStatus, @{n='SizeGB';e={[math]::Round($_.Size / 1GB)}} -AutoSize | Out-String -Width 200
Get-Volume | Where-Object DriveLetter | Sort-Object DriveLetter | Format-Table DriveLetter, FileSystemLabel, FileSystem, HealthStatus, @{n='SizeGB';e={[math]::Round($_.Size / 1GB)}}, @{n='FreeGB';e={[math]::Round($_.SizeRemaining / 1GB)}} -AutoSize | Out-String -Width 200
