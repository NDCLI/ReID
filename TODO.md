# TODO: Electron Snip Tool App (theo plan SVG)

## 1. Thiết lập dự án
- [ ] Tạo dự án Electron + React + Vite.
- [ ] Thêm cấu hình `main` và `renderer` cho Electron.
- [ ] Cài `fabric` để làm annotation canvas.
- [ ] Cài `electron-store` hoặc local storage để lưu gallery tạm.
- [ ] Cài `electron-builder` hoặc `electron-forge` nếu cần đóng gói.

## 2. Màn hình chính (Main window)
- [ ] Tạo cửa sổ chính dark theme, kích thước nhỏ gọn (~480×200px) và always-on-top.
- [ ] Thêm title bar hiển thị "Snip Tool".
- [ ] Thêm toolbar với các nút: New, Camera, Rectangle, Freeform.
- [ ] Thêm nút menu "..." settings.
- [ ] Hiển thị hint text: "Press Win + Shift + S to start a snip".
- [ ] Main window tự động mở rộng khi gallery có ảnh, thu nhỏ lại khi gallery trống.

## 3. Hotkey & khởi tạo chụp màn hình
- [ ] Đăng ký hotkey toàn hệ thống `Win+Shift+S`.
- [ ] Cấu hình nút "New" chạy cùng chức năng này.
- [ ] Khi kích hoạt, mở overlay fullscreen tối với crosshair cursor.
- [ ] Hỗ trợ kéo chọn vùng sáng.
- [ ] Esc để hủy, enter / thả chuột để chụp.

## 4. Capture overlay
- [ ] Hiển thị overlay fullscreen tối mờ.
- [ ] Crosshair cursor rõ ràng.
- [ ] Vẽ vùng chọn kéo thả.
- [ ] Cho phép hủy qua Esc.
- [ ] Khi hoàn tất, chuyển ảnh vào annotation editor.

## 5. Annotation editor
- [ ] Mở trình chỉnh sửa annotation sau khi chụp.
- [ ] Tích hợp Fabric.js để vẽ và chỉnh sửa.
- [ ] Thêm các công cụ:
  - Bút vẽ.
  - Mũi tên.
  - Rectangle.
  - Ellipse.
  - Text.
  - Highlight.
  - Blur vùng.
- [ ] Cho phép chọn màu, độ dày.
- [ ] Hỗ trợ `Ctrl+Z` undo.
- [ ] Nút "Done" flatten ảnh và lưu vào gallery.

## 6. Gallery
- [ ] Hiển thị gallery inline trong main window.
- [ ] Grid thumbnail realtime.
- [ ] Mỗi thumbnail có timestamp.
- [ ] Click vào thumbnail mở full annotation editor để chỉnh sửa lại.
- [ ] Nút xóa ảnh.
- [ ] `Ctrl+C` copy ảnh.
- [ ] Sắp xếp ảnh mới nhất ở đầu.

## 7. Copy & Paste
- [ ] Thực hiện copy PNG bitmap vào clipboard.
- [ ] Hiển thị toast `Đã copy!`.
- [ ] Hỗ trợ `Ctrl+C` hoặc nút Copy.
- [ ] Hướng dẫn paste vào Excel: Alt+Tab → click cell → Ctrl+V.

## 8. Công nghệ chính
- [ ] Electron: shell + hotkey + desktopCapturer + clipboard.
- [ ] React + Vite: UI + gallery.
- [ ] Fabric.js: annotation.
- [ ] Electron `desktopCapturer` để chụp màn hình.
- [ ] Electron `clipboard.writeImage` để copy ảnh.

## 9. UX quan trọng
- [ ] Main window luôn ở trên cùng.
- [ ] Main window mở rộng khi gallery có ảnh.
- [ ] Dark theme giống Snipping Tool Windows 11.
- [ ] Ảnh mới chụp luôn hiện đầu tiên trong gallery.

## 10. Bước triển khai ban đầu
- [ ] Khởi tạo repo / project structure.
- [ ] Tạo `src/main.ts` cho Electron main process.
- [ ] Tạo `src/renderer/App.tsx` cho React UI.
- [ ] Tạo `src/renderer/components/Gallery.tsx` và `AnnotationEditor.tsx`.
- [ ] Tạo module capture overlay + hotkey handler.
