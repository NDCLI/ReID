using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace AutoMarkerReID.App;

public static class LocalizationService
{
    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["AutoMarker Re-ID"] = "AutoMarker Re-ID",
        ["Thư viện ảnh chụp"] = "Capture library", ["Thư viện kết quả"] = "Results library", ["ẢNH CHỤP GẦN ĐÂY"] = "RECENT CAPTURES",
        ["Phím tắt toàn cục"] = "Global hotkeys", ["Đang kiểm tra kết quả nhận diện"] = "Review recognition results",
        ["Nhận diện, đánh dấu và quản lý ảnh tham chiếu ngay trên thiết bị"] = "Recognize, mark and manage reference images locally",
        ["PHẠM VI NHẬN DIỆN"] = "RECOGNITION SCOPE", ["Phạm vi nhận diện: Tất cả Query"] = "Recognition scope: All Queries",
        ["Tất cả Query"] = "All Queries", ["CÔNG CỤ"] = "TOOLS", ["Chọn vùng chụp"] = "Select capture area",
        ["Chụp lại vùng cũ"] = "Repeat previous capture", ["Kết quả đã lưu"] = "Saved results", ["Chỉnh sửa ảnh"] = "Edit image",
        ["Ảnh chụp gần đây"] = "Recent captures", ["Xóa toàn bộ dữ liệu"] = "Delete all data", ["Tạo lại dữ liệu AI"] = "Rebuild AI data",
        ["Dọn nhật ký"] = "Clear log", ["LƯU ẢNH THAM CHIẾU VÀO"] = "SAVE REFERENCE IMAGE TO",
        ["Chọn Query trống"] = "Choose empty Query", ["Hỗ trợ đối chiếu trang phục (LBP)"] = "Use clothing comparison (LBP)",
        ["Tự động lưu ảnh chụp vào Pictures\\Screenshots"] = "Automatically save captures to Pictures\\Screenshots",
        ["TÌNH TRẠNG HỆ THỐNG"] = "SYSTEM STATUS", ["NHẬT KÝ HOẠT ĐỘNG"] = "ACTIVITY LOG",
        ["Tất cả"] = "All", ["Cảnh báo"] = "Warnings", ["Lỗi"] = "Errors", ["Đóng"] = "Close",
        ["Trước"] = "Previous", ["Tiếp"] = "Next", ["Tải lại danh sách"] = "Refresh list", ["Sao chép ảnh"] = "Copy image",
        ["Xóa kết quả"] = "Delete result", ["KẾT QUẢ ĐÃ LƯU"] = "SAVED RESULTS", ["THƯ VIỆN ẢNH CHỤP"] = "CAPTURE LIBRARY",
        ["CẮT / CHỈNH SỬA"] = "CROP / EDIT", ["HỦY"] = "CANCEL", ["LƯU VÀ SAO CHÉP"] = "SAVE AND COPY",
        ["Cắt lấy vùng chọn"] = "Crop selection", ["Xóa một dải"] = "Remove strip", ["Hoàn tác"] = "Undo",
        ["Đặt lại"] = "Reset", ["Ghép ảnh bên trái"] = "Merge left", ["Ghép ảnh bên phải"] = "Merge right",
        ["LƯU"] = "SAVE", ["Kéo chuột để chọn vùng cần chụp · Nhấn Esc để hủy"] = "Drag to select an area · Press Esc to cancel",
        ["Kiểm tra kết quả nhận diện"] = "Review recognition results", ["Đang theo dõi Clipboard"] = "Monitoring Clipboard",
        ["Đang nhận diện…"] = "Recognizing…", ["Đang khởi động hệ thống nhận diện…"] = "Starting recognition system…",
    };

    public static bool IsEnglish { get; private set; }

    public static void Configure(string? language)
    {
        IsEnglish = string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => Apply((Window)sender)), true);
    }

    public static string Translate(string value) => IsEnglish && English.TryGetValue(value, out var translated) ? translated : value;

    public static void Apply(Window window)
    {
        if (!IsEnglish) return;
        window.Title = Translate(window.Title);
        ApplyElement(window);
    }

    private static void ApplyElement(DependencyObject element)
    {
        switch (element)
        {
            case WpfButton button:
                if (!BindingOperations.IsDataBound(button, ContentControl.ContentProperty) && button.Content is string buttonText) button.Content = Translate(buttonText);
                break;
            case WpfCheckBox checkBox:
                if (!BindingOperations.IsDataBound(checkBox, ContentControl.ContentProperty) && checkBox.Content is string checkBoxText) checkBox.Content = Translate(checkBoxText);
                break;
            case WpfRadioButton radio:
                if (!BindingOperations.IsDataBound(radio, ContentControl.ContentProperty) && radio.Content is string radioText) radio.Content = Translate(radioText);
                break;
            case TextBlock textBlock:
                if (!BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty)) textBlock.Text = Translate(textBlock.Text);
                break;
            case WpfToolTip toolTip:
                if (toolTip.Content is string toolTipText) toolTip.Content = Translate(toolTipText);
                break;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            ApplyElement(VisualTreeHelper.GetChild(element, index));
    }
}
