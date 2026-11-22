# NVAPI (vendored subset)

Source: NVIDIA NVAPI open source SDK, R610 release
(https://github.com/NVIDIA/nvapi). Licensed MIT, see `License.txt`.

Only the files PeakMode needs to build are kept:

- `nvapi.h` and the `nvapi_lite_*.h` headers it includes
- `nvapi_interface.h`
- `NvApiDriverSettings.h` (DRS setting IDs and values)
- `amd64/nvapi64.lib` (x64 import library)

Samples, documentation, HLSL headers and the x86 library were removed.
To update, copy the same files from a newer SDK release and rebuild.
