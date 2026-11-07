# Name: Network check
# Description: Active adapters with their addresses, then a ping to the gateway, the LAN server and the internet.
Get-NetIPConfiguration | Where-Object { $_.NetAdapter.Status -eq 'Up' } | ForEach-Object {
    Write-Output "$($_.InterfaceAlias): $($_.IPv4Address.IPAddress)  gateway $($_.IPv4DefaultGateway.NextHop)  dns $($_.DnsServer.ServerAddresses -join ', ')"
}
Write-Output ""
$targets = @('192.168.68.255', '1.1.1.1', 'www.google.com')
$gw = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Select-Object -First 1).NextHop
if ($gw) { $targets = @($gw) + $targets }
foreach ($t in $targets) {
    $ok = Test-Connection -ComputerName $t -Count 2 -Quiet
    Write-Output ("{0,-18} {1}" -f $t, $(if ($ok) { 'reachable' } else { 'NO REPLY' }))
}
