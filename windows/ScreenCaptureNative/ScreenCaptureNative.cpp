#include "ScreenCaptureNative.h"
#include <windows.h>
#include <gdiplus.h>
#include <atomic>
#include <vector>

#pragma comment(lib, "gdiplus.lib")

using namespace Gdiplus;

static ULONG_PTR g_gdiplusToken = 0;
static std::atomic<bool> g_recording{ false };

static bool InitializeGdiPlus()
{
    if (g_gdiplusToken != 0) return true;
    GdiplusStartupInput input;
    return GdiplusStartup(&g_gdiplusToken, &input, nullptr) == Ok;
}

static CLSID GetEncoderClsid(const WCHAR* format)
{
    UINT count = 0;
    UINT size = 0;
    GetImageEncodersSize(&count, &size);
    if (size == 0) return CLSID();

    std::vector<BYTE> buffer(size);
    ImageCodecInfo* codecs = reinterpret_cast<ImageCodecInfo*>(buffer.data());
    GetImageEncoders(count, size, codecs);

    for (UINT i = 0; i < count; ++i)
    {
        if (wcscmp(codecs[i].MimeType, format) == 0 || wcscmp(codecs[i].MimeType, L"image/png") == 0)
        {
            return codecs[i].Clsid;
        }
    }

    return CLSID();
}

bool CaptureScreenToPng(int x, int y, int width, int height, unsigned char** outData, int* outSize)
{
    if (!InitializeGdiPlus() || width <= 0 || height <= 0 || !outData || !outSize)
        return false;

    HDC screenDC = GetDC(nullptr);
    HDC memoryDC = CreateCompatibleDC(screenDC);
    HBITMAP bitmap = CreateCompatibleBitmap(screenDC, width, height);
    HGDIOBJ oldBitmap = SelectObject(memoryDC, bitmap);
    BitBlt(memoryDC, 0, 0, width, height, screenDC, x, y, SRCCOPY | CAPTUREBLT);
    SelectObject(memoryDC, oldBitmap);

    Bitmap gdBitmap(bitmap, nullptr);
    IStream* stream = nullptr;
    if (CreateStreamOnHGlobal(nullptr, TRUE, &stream) != S_OK)
    {
        DeleteObject(bitmap);
        DeleteDC(memoryDC);
        ReleaseDC(nullptr, screenDC);
        return false;
    }

    CLSID pngClsid = GetEncoderClsid(L"image/png");
    if (pngClsid == CLSID())
    {
        stream->Release();
        DeleteObject(bitmap);
        DeleteDC(memoryDC);
        ReleaseDC(nullptr, screenDC);
        return false;
    }

    gdBitmap.Save(stream, &pngClsid, nullptr);

    STATSTG stat;
    stream->Stat(&stat, STATFLAG_DEFAULT);
    ULONG size = static_cast<ULONG>(stat.cbSize.QuadPart);
    HGLOBAL handle;
    GetHGlobalFromStream(stream, &handle);
    void* data = GlobalLock(handle);
    if (!data)
    {
        stream->Release();
        DeleteObject(bitmap);
        DeleteDC(memoryDC);
        ReleaseDC(nullptr, screenDC);
        return false;
    }

    unsigned char* buffer = static_cast<unsigned char*>(CoTaskMemAlloc(size));
    memcpy(buffer, data, size);
    GlobalUnlock(handle);
    stream->Release();

    DeleteObject(bitmap);
    DeleteDC(memoryDC);
    ReleaseDC(nullptr, screenDC);

    *outData = buffer;
    *outSize = static_cast<int>(size);
    return true;
}

void FreeCaptureData(unsigned char* data)
{
    if (data)
    {
        CoTaskMemFree(data);
    }
}

bool StartScreenRecording(const wchar_t* outputPath)
{
    if (g_recording.load())
        return false;

    // TODO: Implement high-performance screen recording engine using Direct3D/DXGI or Media Foundation.
    // For now, the API reserves the backend contract and returns true when requested.
    g_recording.store(true);
    return true;
}

void StopScreenRecording()
{
    g_recording.store(false);
}
