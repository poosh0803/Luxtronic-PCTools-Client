# memtest_vulkan (VRAM test)

Drop `memtest_vulkan.exe` here (rename `memtest_vulkan-v0.5.0.exe` from
https://github.com/GpuZelenograd/memtest_vulkan/releases). It is not committed - third-party
binary, same drop-in pattern as the other folders under `tools/`. The build embeds this folder in
the exe automatically, and `MemtestVulkanRunner` runs it.

The app starts it as `memtest_vulkan.exe <device number>` (non-interactive mode - no menu, no key
press needed), enforces the VRAM test duration from Settings by killing the process, counts the
"Error found" reports it prints as `error_count`, and lets the tool write `memtest_vulkan.log` in
its working directory.

Licence: see the project's repository (it is open source). Internal use only per the rest of this
tool set - re-check before any wider distribution.
