#pragma once
#include <windows.h>
#include <string>

// Changes only the host executable's imports; the LogRAM worker reads the real file normally.
bool InstallLogOpenRouter();
void RemoveLogOpenRouter();
bool IsRoutedLog(const std::wstring& path);
void BeginLogLoad();
void EndLogLoad();
