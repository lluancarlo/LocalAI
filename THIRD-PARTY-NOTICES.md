# Third-party notices

Local AI includes the third-party software below. Each component keeps its own license; this file does not change
Local AI's own terms (see the License section of the README).

Models and voices are not included: they are downloaded from inside the app and come with their own licenses, shown on
their download pages (Hugging Face, GitHub).

## Components

| Component | License | Source |
|---|---|---|
| llama.cpp / ggml (`runtime\llama.cpp`) | MIT (full text below) | https://github.com/ggml-org/llama.cpp |
| NVIDIA CUDA runtime (`cudart`, `cublas`, `cublasLt`) | NVIDIA CUDA Toolkit EULA (redistributable files) | https://docs.nvidia.com/cuda/eula/ |
| LLVM OpenMP runtime (`libomp.dll`) | Apache-2.0 with LLVM exception (`runtime\llama.cpp\LICENSE-LLVM-OpenMP`) | https://github.com/llvm/llvm-project |
| .NET runtime and Microsoft.Extensions.* | MIT | https://github.com/dotnet/runtime |
| Avalonia | MIT | https://github.com/AvaloniaUI/Avalonia |
| SkiaSharp, HarfBuzzSharp | MIT | https://github.com/mono/SkiaSharp |
| ANGLE (`av_libglesv2.dll`) | BSD-3-Clause | https://chromium.googlesource.com/angle/angle |
| Inter font | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| MicroCom, Tmds.DBus.Protocol | MIT | https://github.com/kekekeks/MicroCom, https://github.com/tmds/Tmds.DBus |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Data.Sqlite | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | Public domain | https://sqlite.org/copyright.html |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Logging | Apache-2.0 | https://github.com/serilog/serilog |
| Whisper.net | MIT | https://github.com/sandrohanea/whisper.net |
| whisper.cpp | MIT | https://github.com/ggml-org/whisper.cpp |
| sherpa-onnx | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| ONNX Runtime | MIT | https://github.com/microsoft/onnxruntime |
| NAudio | MIT | https://github.com/naudio/NAudio |
| SharpZipLib | MIT | https://github.com/icsharpcode/SharpZipLib |

## llama.cpp license

```
MIT License

Copyright (c) 2023-2026 The ggml authors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
