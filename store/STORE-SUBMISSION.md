# Microsoft Store submission checklist

## Cần lấy từ Partner Center

1. Tạo tài khoản developer và xác minh danh tính.
2. Reserve tên **AutoMarkerReID**.
3. Vào **Product identity**, sao chép chính xác hai giá trị `Package/Identity/Name` và `Package/Identity/Publisher`.
4. Build gói chính thức:

```powershell
$env:Path = 'C:\Program Files\dotnet;' + $env:Path
.\store\Build-StoreMsix.ps1 `
  -Version 1.0.8.0
```

Script đã lưu Product identity chính thức `Hoakim.AutoMarkerReID`, publisher `CN=06970FBF-6DEA-4FD9-BB5E-DCC0D8D933EB` và PublisherDisplayName `Hoakim`.

Không upload gói được tạo bằng `-TestIdentity`.

## Store listing đề xuất

- **Category:** Utilities & tools
- **Pricing:** Free
- **Privacy policy:** `https://github.com/NDCLI/ReID/blob/main/PRIVACY.md`
- **Support:** `https://github.com/NDCLI/ReID/issues`
- **Short description:** Nhận diện Re-ID, đánh dấu kết quả Blaze và quản lý ảnh tham chiếu hoàn toàn trên thiết bị.

### Description

AutoMarker Re-ID hỗ trợ lưu ảnh tham chiếu theo Query, nhận diện người trong ảnh kết quả Blaze, kiểm tra và điều chỉnh khung trước khi lưu. Ứng dụng có công cụ chụp vùng màn hình, chỉnh sửa ảnh, OCR thời gian trên thẻ, quản lý cache AI và thư viện kết quả. Mọi mô hình và dữ liệu đều chạy cục bộ; ứng dụng không tải ảnh lên cloud và không thu thập telemetry.

### Notes for certification

AutoMarker Re-ID is an offline, full-trust WPF desktop utility. It requires no account, network service, special hardware, or external download. All OpenVINO models and OCR assets are included in the package.

The app monitors image changes in the Windows Clipboard so user-copied Blaze screenshots can be processed. It reads only image/file-drop Clipboard formats and performs all processing locally. It never transmits user data.

The `runFullTrust` capability is required for local OpenVINO/OpenCV inference, global hotkeys, screen-region capture, system tray operation, and a low-level mouse hook. The hook only suppresses right-click while Blaze Client is foreground to start region capture, and middle-click while Blaze Client or Microsoft Excel is foreground to switch between those two windows. Other applications and the Windows taskbar are not intercepted.

The optional startup task is disabled by default and passes `--startup` so the app starts hidden in the system tray. Users remain in control through Windows Startup Apps settings.

Suggested test:
1. Launch the app from Start; the main window opens without login.
2. Select **Chỉnh sửa ảnh** to open any local image and verify local editing.
3. Add reference images to a Query, then copy a Blaze results screenshot to the Clipboard to open Review.
4. Use **Xóa toàn bộ dữ liệu** to remove app-managed queries, AI cache, and output.

## Trước khi bấm Submit

- Upload `.msixupload` nếu script tạo ra; nếu không, upload `.msix`.
- Chạy Windows App Certification Kit trên gói chính thức.
- Cung cấp ít nhất một screenshot 1366×768 hoặc 1920×1080, logo Store và age rating.
- Khai báo app truy cập thông tin cá nhân vì app xử lý ảnh/screenshot; dùng Privacy Policy ở trên.
- Giải thích `runFullTrust` trong Restricted capabilities bằng nội dung Notes for certification.
- Không đổi binary sau khi đã upload trong cùng submission; tăng version bốn phần cho lần cập nhật tiếp theo.
