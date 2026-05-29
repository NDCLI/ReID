# SnipTool Windows Migration

This folder contains a Windows-native version of the Snip Tool application:

- `SnipToolApp`: C# WPF frontend built with XAML
- `ScreenCaptureNative`: C++ native DLL for screen capture and recording APIs

## Structure

- `SnipToolApp/`: WPF UI with Fluent-style rounded corners, preview, and overlay selection.
- `ScreenCaptureNative/`: Native C++ screenshot engine exposing P/Invoke-compatible exports.

## Build instructions

1. Open `windows/SnipToolApp/SnipToolApp.csproj` in Visual Studio 2022 or newer.
2. Open the `ScreenCaptureNative` project in the same solution or separately.
3. Build `ScreenCaptureNative` first, then build `SnipToolApp`.
4. Copy `ScreenCaptureNative.dll` into `SnipToolApp\bin\Debug\net7.0-windows\` or configure post-build copy.

## Notes

- The C# frontend uses an overlay window to perform region selection.
- `NativeMethods` provides the P/Invoke bridge to the native DLL.
- `StartScreenRecording` / `StopScreenRecording` are exposed by the native layer and ready for a high-performance recording implementation.
