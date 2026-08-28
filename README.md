# AutoMarker Re-ID

Ứng dụng Windows chạy cục bộ để lưu ảnh tham chiếu theo Query, nhận diện Re-ID trên ảnh chụp, review kết quả và quản lý thư viện ảnh. Ứng dụng viết bằng C#/.NET 10, WPF, OpenCV và OpenVINO; không cần Python hoặc dịch vụ cloud khi chạy.

**Bản mới nhất:** [v1.0.14](https://github.com/NDCLI/ReID/releases/tag/v1.0.14)

## Cài đặt nhanh

1. Tải `AutoMarkerReID-Setup-1.0.14-win-x64.exe` từ [Release](https://github.com/NDCLI/ReID/releases/latest).
2. Chạy Setup và giữ lựa chọn **Khởi động AutoMarker Re-ID cùng Windows** nếu muốn app tự chạy nền sau khi đăng nhập.
3. Mở app từ Start Menu hoặc shortcut. App có thể khởi động ẩn ở System Tray; dùng menu tray để hiện cửa sổ chính hoặc thoát hoàn toàn.

Setup là bản x64 self-contained: máy cài không cần cài .NET runtime riêng. Windows 10 1809 trở lên được hỗ trợ.

## Chức năng chính

- Lưu crop người vào `Query_N` được chọn thủ công từ danh sách **Lưu ảnh tham chiếu vào**.
- Chọn một Query hoặc Root/Tất cả Query làm phạm vi nhận diện.
- Nhận diện Re-ID cục bộ bằng OpenVINO, OSNet, OCR timestamp; có tùy chọn đối chiếu trang phục (LBP).
- Chụp vùng mới, chụp lại vùng gần nhất, mở Image Editor, xem thư viện kết quả và copy ảnh đánh dấu.
- Tạo lại cache AI/OCR với log tiến độ theo từng ảnh tham chiếu.
- Xóa vĩnh viễn toàn bộ Query, cache và kết quả `output` sau khi xác nhận.
- Theo dõi Clipboard và nhận ảnh từ luồng chụp/sao chép được hỗ trợ.

Hai lựa chọn Query là độc lập:

- **Sidebar Query**: phạm vi người cần nhận diện.
- **Lưu ảnh tham chiếu vào**: nơi lưu crop người mới.

## Phím tắt và thao tác chuột

| Phím/thao tác | Chức năng |
| --- | --- |
| `F2` | Chọn Query trống để lưu ảnh tham chiếu (chỉ khi Blaze hoặc Excel đang foreground) |
| `F3` | Chọn Root/Tất cả Query để nhận diện |
| `F4` / `F5` | Chuyển Query nhận diện trước / tiếp theo |
| `Alt+PrintScreen` | Chụp vùng mới |
| `Alt+S` | Chụp lại vùng gần nhất |
| `Ctrl+Alt+Shift+F10` | Trigger capture dự phòng cho tích hợp Blaze/AHK |
| Chuột phải trong Blaze | Mở chụp vùng; click gốc không bị gửi đồng thời vào Blaze |
| Chuột giữa trong Blaze hoặc Excel | Chuyển nhanh foreground giữa Blaze và Excel; thao tác chuột giữa gốc bị chặn |

Trong Review: `Esc` hủy, click trái thêm/xóa khung, chuột phải lưu và copy. Trong thư viện: `←/→` đổi ảnh, `Ctrl+C` copy, `Delete` xóa, `Esc` đóng.

## Dữ liệu ứng dụng

Bản cài đặt lưu dữ liệu người dùng tại:

```text
%LOCALAPPDATA%\AutoMarkerReID\
├── queries\Query_N\       # ảnh tham chiếu
│   └── .cache\             # cache embedding và OCR
└── output\Query_N\        # ảnh marked, ảnh gốc và metadata JSON
```

Có thể đổi thư mục dữ liệu bằng biến môi trường `AUTOMARKER_DATA_DIR`. Nút **Xóa toàn bộ dữ liệu** xóa vĩnh viễn toàn bộ nội dung trên, không chuyển vào Recycle Bin.

## Phát triển và kiểm tra

Yêu cầu: Windows, .NET 10 SDK x64. Để build Setup cần thêm Inno Setup 6.

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test tests\AutoMarkerReID.Tests\AutoMarkerReID.Tests.csproj --no-restore --verbosity minimal
```

Tạo Setup self-contained:

```powershell
$env:Path = 'C:\Program Files\dotnet;' + $env:Path
.\installer\Build-Setup.ps1 -Version 1.0.14
```

File tạo ra nằm tại `artifacts\setup\AutoMarkerReID-Setup-<version>-win-x64.exe`. Có thể truyền `-CertificateThumbprint` hoặc `-PfxPath` để ký Authenticode khi phát hành.

### Microsoft Store (MSIX)

Đăng ký/tạo app trong Partner Center, rồi lấy chính xác hai giá trị **Package/Identity/Name** và **Package/Identity/Publisher** ở trang Product identity. Build gói Store bằng:

```powershell
$env:Path = 'C:\Program Files\dotnet;' + $env:Path
.\store\Build-StoreMsix.ps1 -Version 1.0.15.0
```

File tải lên Partner Center nằm tại `artifacts\store\*.msixupload`. Pipeline dùng Windows SDK BuildTools chính thức từ NuGet nên không cần Visual Studio; Microsoft Store sẽ ký gói sau khi duyệt. Có thể build kiểm tra cấu trúc bằng `-TestIdentity`, nhưng tuyệt đối không tải gói test đó lên Store.

Xem [hướng dẫn và checklist gửi Store](store/STORE-SUBMISSION.md) cùng [chính sách quyền riêng tư](PRIVACY.md).

## Tài liệu kỹ thuật

- [Đặc tả tính năng và logic](APP_FEATURES_AND_LOGIC.md)
- [Chuẩn bị Microsoft Store](store/STORE-SUBMISSION.md)
- [Chính sách quyền riêng tư](PRIVACY.md)
- [Lịch sử phát hành](https://github.com/NDCLI/ReID/releases)
