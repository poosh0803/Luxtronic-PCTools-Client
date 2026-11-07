# Name: Check Windows image (DISM)
# Description: Read-only check of the Windows component store (DISM /CheckHealth then /ScanHealth). Takes a few minutes; does not change anything.
DISM /Online /Cleanup-Image /CheckHealth
DISM /Online /Cleanup-Image /ScanHealth
