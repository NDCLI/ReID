# Snip Tool

Đã chuyển sang Windows-native với giao diện C# / XAML và backend capture bằng C++.

## Cấu trúc

- `windows/SnipTool.sln` — solution Visual Studio chứa cả frontend WPF và native DLL.
- `windows/SnipToolApp/` — giao diện C# / XAML.
- `windows/ScreenCaptureNative/` — native C++ capture engine.

## Hướng dẫn mở

1. Mở `windows/SnipTool.sln` trong Visual Studio 2022 hoặc mới hơn.
2. Build `ScreenCaptureNative` trước.
3. Build `SnipToolApp`.
4. Copy `ScreenCaptureNative.dll` vào thư mục output của `SnipToolApp` nếu cần.

## Lưu ý

- Đã loại bỏ toàn bộ stack Electron / React / Vite cũ.
- `npm`, `package.json`, `node_modules`, `src/`, `dist/` đã không còn.
