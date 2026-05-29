#pragma once

#ifdef _WIN32
#define EXPORT_API __declspec(dllexport)
#else
#define EXPORT_API
#endif

extern "C" EXPORT_API bool CaptureScreenToPng(int x, int y, int width, int height, unsigned char** outData, int* outSize);
extern "C" EXPORT_API void FreeCaptureData(unsigned char* data);
extern "C" EXPORT_API bool StartScreenRecording(const wchar_t* outputPath);
extern "C" EXPORT_API void StopScreenRecording();
