# AutoMarker Re-ID

Ứng dụng Windows desktop thuần C#/.NET 10 để thu thập Query, nhận diện Re-ID, review/đánh dấu kết quả và quản lý thư viện ảnh. Runtime không dùng Python.

## Tech stack

- C# 14, .NET 10, WPF
- OpenCvSharp cho ảnh, candidate, hash, LBP, editor và vẽ khung
- OpenVINO native runtime qua C# P/Invoke bridge cho 3 model OSNet, face detection/Re-ID và PP-OCRv6
- Microsoft Generic Host/DI, CommunityToolkit.Mvvm
- xUnit cho unit/integration tests

## Chạy ứng dụng

```powershell
dotnet run --project src\AutoMarkerReID.App\AutoMarkerReID.App.csproj -- --show
```

Không truyền `--show` thì app khởi động ẩn ở System Tray. Dữ liệu nằm trong `queries/` và `output/` tại thư mục chạy; có thể đặt `AUTOMARKER_DATA_DIR` để đổi thư mục dữ liệu.

Với bản cài đặt, dữ liệu người dùng được lưu tại `%LOCALAPPDATA%\AutoMarkerReID` để app không cần quyền ghi vào thư mục cài đặt.

## CLI

```powershell
dotnet run --project src\AutoMarkerReID.Cli\AutoMarkerReID.Cli.csproj -- --help
dotnet run --project src\AutoMarkerReID.Cli\AutoMarkerReID.Cli.csproj -- --single screenshot.png --query Query_1
```

Không có `--single`, CLI theo dõi Clipboard đến khi nhấn `Ctrl+C`.

## Kiểm tra và publish

```powershell
dotnet build AutoMarkerReID.slnx -c Release
dotnet test tests\AutoMarkerReID.Tests\AutoMarkerReID.Tests.csproj -c Release
dotnet publish src\AutoMarkerReID.App\AutoMarkerReID.App.csproj -c Release -r win-x64 --self-contained false -o artifacts\publish\gui
dotnet publish src\AutoMarkerReID.Cli\AutoMarkerReID.Cli.csproj -c Release -r win-x64 --self-contained false -o artifacts\publish\cli
```

Bản framework-dependent yêu cầu .NET Desktop Runtime 10 x64. Model và OCR asset được copy vào thư mục publish.

## Tạo bộ cài Windows

Icon của bản ReID cũ được dùng thống nhất cho cửa sổ, file thực thi, shortcut và bộ cài. Chạy:

```powershell
powershell -ExecutionPolicy Bypass -File installer\Build-Setup.ps1
```

Script tạo bản self-contained x64 và file `AutoMarkerReID-Setup-<version>-win-x64.exe` trong `artifacts\setup`. Nếu có chứng thư Authenticode, truyền `-CertificateThumbprint` hoặc `-PfxPath`; script sẽ ký SHA-256 và đóng dấu thời gian cho ứng dụng lẫn bộ cài.
