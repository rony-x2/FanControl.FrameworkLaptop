# Third-party notices

## framework-system (Framework Computer Inc)

The EC access in `src/FanControl.FrameworkLaptop/CrosEc.cs` (IOCTL codes, buffer layout of the CrosEC driver interface, memory map offsets and EC host command structures) follows
[framework-system](https://github.com/FrameworkComputer/framework-system), in particular `framework_lib/src/chromium_ec/windows.rs`, `power.rs` and `commands.rs`.
The parser in `ThermalParser.cs` reads the text output of its `framework_tool`.

```
BSD 3-Clause License

Copyright (c) 2023, Framework Computer Inc

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## FanControl.Plugins (Rem0o)

The plugin is built against `FanControl.Plugins.dll` from [FanControl](https://github.com/Rem0o/FanControl.Releases). That file is part of FanControl, is not included in this repository and is not redistributed with the release.
