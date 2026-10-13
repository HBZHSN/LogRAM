#define WIN32_LEAN_AND_MEAN
#include "LogOpenRouter.h"
#include <cstdio>
#include <stdexcept>

static void Check(bool result, const char* message) { if (!result) throw std::runtime_error(message); }
// Reload the host's import on each call, including after the test removes its hook.
__declspec(noinline) static BOOL Attributes(const std::wstring& path, WIN32_FILE_ATTRIBUTE_DATA& attributes) {
    return GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes);
}
__declspec(noinline) static HANDLE Open(const std::wstring& path, DWORD access, DWORD disposition) {
    return CreateFileW(path.c_str(), access, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, disposition, 0, nullptr);
}
static void WriteFixture(const std::wstring& path) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, CREATE_ALWAYS, 0, nullptr);
    Check(file != INVALID_HANDLE_VALUE, "create fixture");
    const char text[] = "ERROR first\nsecond\n";
    DWORD written{};
    const BOOL ok = WriteFile(file, text, sizeof(text) - 1, &written, nullptr);
    CloseHandle(file);
    Check(ok && written == sizeof(text) - 1, "write fixture");
}

int main() {
    wchar_t temporary[MAX_PATH]{}, name[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temporary);
    if (!GetTempFileNameW(temporary, L"LRA", 0, name)) return 1;
    DeleteFileW(name);
    const auto log = std::wstring(name) + L".LOG";
    const auto text = std::wstring(name) + L".txt";
    const auto backup = std::wstring(name) + L"-backup.log";
    int result = 1;
    try {
        WriteFixture(log); WriteFixture(text); WriteFixture(backup);
        Check(InstallLogOpenRouter(), "install imports");
        BeginLogLoad();
        WIN32_FILE_ATTRIBUTE_DATA attributes{};
        Check(Attributes(log, attributes) && attributes.nFileSizeLow == 0, "log allocation stays empty");
        Check(Attributes(text, attributes) && attributes.nFileSizeLow > 0, "text metadata unchanged");
        HANDLE file = Open(log, GENERIC_READ, OPEN_EXISTING);
        Check(file != INVALID_HANDLE_VALUE, "log opens");
        char buffer[64]{}; DWORD read{};
        const BOOL ok = ReadFile(file, buffer, sizeof(buffer), &read, nullptr);
        CloseHandle(file);
        Check(ok && read == 0 && IsRoutedLog(log), "host receives no log content");
        Check(Attributes(backup, attributes) && attributes.nFileSizeLow == 19, "snapshot backup metadata is preserved");
        file = Open(backup, GENERIC_READ, OPEN_EXISTING);
        Check(file != INVALID_HANDLE_VALUE, "snapshot backup opens");
        const BOOL backupRead = ReadFile(file, buffer, sizeof(buffer), &read, nullptr);
        CloseHandle(file);
        Check(backupRead && read == 19 && !IsRoutedLog(backup), "unsaved snapshot backup is not redirected");
        EndLogLoad();
        Check(Attributes(log, attributes), "routed metadata");
        const auto originalTime = attributes.ftLastWriteTime;
        auto realCreate = reinterpret_cast<decltype(&CreateFileW)>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "CreateFileW"));
        file = realCreate(log.c_str(), FILE_WRITE_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
        Check(file != INVALID_HANDLE_VALUE, "external change fixture opens");
        ULARGE_INTEGER changed{}; changed.LowPart = originalTime.dwLowDateTime; changed.HighPart = originalTime.dwHighDateTime; changed.QuadPart += 10000000;
        const FILETIME changedTime{changed.LowPart, changed.HighPart};
        const BOOL changedOk = SetFileTime(file, nullptr, nullptr, &changedTime);
        CloseHandle(file);
        Check(changedOk && Attributes(log, attributes) && CompareFileTime(&attributes.ftLastWriteTime, &originalTime) == 0, "native reload prompt timestamp stays stable");
        file = Open(log, GENERIC_WRITE, CREATE_ALWAYS);
        Check(file == INVALID_HANDLE_VALUE && GetLastError() == ERROR_ACCESS_DENIED, "placeholder cannot overwrite source");
        WriteFixture(text);
        RemoveLogOpenRouter();
        Check(Attributes(log, attributes) && attributes.nFileSizeLow == 19, "source intact and metadata restored");
        file = Open(log, GENERIC_READ, OPEN_EXISTING);
        Check(file != INVALID_HANDLE_VALUE, "real source reopens after removal");
        const BOOL originalRead = ReadFile(file, buffer, sizeof(buffer), &read, nullptr);
        CloseHandle(file);
        Check(originalRead && read == 19 && buffer[0] == 'E', "real source content restored");
        std::puts("PASS: .LOG routing, zero host reads, normal text I/O, unsaved backups, reload prompts, source write protection and import restoration.");
        result = 0;
    }
    catch (const std::exception& error) { std::fprintf(stderr, "%s\n", error.what()); }
    RemoveLogOpenRouter();
    DeleteFileW(log.c_str()); DeleteFileW(text.c_str()); DeleteFileW(backup.c_str());
    return result;
}
