# Name: Windows activation status
# Description: Shows whether Windows is activated, the license type and the partial product key.
Get-CimInstance SoftwareLicensingProduct -Filter "PartialProductKey IS NOT NULL AND Name LIKE 'Windows%'" | ForEach-Object {
    $state = switch ($_.LicenseStatus) { 0 {'Unlicensed'} 1 {'Licensed (activated)'} 2 {'Out-of-box grace'} 3 {'Out-of-tolerance grace'} 4 {'Non-genuine grace'} 5 {'Notification'} 6 {'Extended grace'} default {'Unknown'} }
    Write-Output "Edition    : $($_.Name)"
    Write-Output "Status     : $state"
    Write-Output "Channel    : $($_.ProductKeyChannel)"
    Write-Output "Partial key: $($_.PartialProductKey)"
}
