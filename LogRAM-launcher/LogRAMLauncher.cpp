#include <windows.h>
#include <shellapi.h>
#include <shlobj.h>

#include <algorithm>
#include <string>
#include <vector>

namespace
{
constexpr int PayloadResourceId = 1;

struct Resource
{
    const BYTE* data;
    DWORD size;
};

void ShowError(const std::wstring& message)
{
    MessageBoxW(nullptr, message.c_str(), L"LogRAM", MB_OK | MB_ICONERROR);
}

std::wstring GetModulePath()
{
    std::vector<wchar_t> buffer(32768);
    const DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    return length == 0 || length >= buffer.size() - 1 ? L"" : std::wstring(buffer.data(), length);
}

std::wstring GetEnvironmentPath(const wchar_t* name)
{
    const DWORD length = GetEnvironmentVariableW(name, nullptr, 0);
    if (length == 0)
    {
        return L"";
    }

    std::vector<wchar_t> value(length);
    return GetEnvironmentVariableW(name, value.data(), length) == 0 ? L"" : value.data();
}

std::wstring ParentDirectory(std::wstring path)
{
    while (!path.empty() && (path.back() == L'\\' || path.back() == L'/'))
    {
        path.pop_back();
    }

    const size_t separator = path.find_last_of(L"\\/");
    return separator == std::wstring::npos ? L"" : path.substr(0, separator);
}

bool EnsureDirectory(const std::wstring& path)
{
    const int result = SHCreateDirectoryExW(nullptr, path.c_str(), nullptr);
    return result == ERROR_SUCCESS || result == ERROR_ALREADY_EXISTS || result == ERROR_FILE_EXISTS;
}

bool CanWriteTo(const std::wstring& directory)
{
    if (directory.empty() || !EnsureDirectory(directory))
    {
        return false;
    }

    const std::wstring probe = directory + L"\\LogRAM-" + std::to_wstring(GetCurrentProcessId()) + L".tmp";
    const HANDLE file = CreateFileW(probe.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    CloseHandle(file);
    DeleteFileW(probe.c_str());
    return true;
}

std::wstring SelectExtractionBase()
{
    std::wstring temporaryPath = GetEnvironmentPath(L"TEMP");
    if (temporaryPath.empty())
    {
        temporaryPath = GetEnvironmentPath(L"TMP");
    }

    if (temporaryPath.empty())
    {
        wchar_t localAppData[MAX_PATH];
        if (SUCCEEDED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, SHGFP_TYPE_CURRENT, localAppData)))
        {
            temporaryPath = localAppData;
        }
    }

    if (CanWriteTo(temporaryPath))
    {
        return temporaryPath;
    }

    const std::wstring parent = ParentDirectory(temporaryPath);
    return CanWriteTo(parent) ? parent : L"";
}

std::wstring GetPayloadDirectory(const std::wstring& extractionBase, const std::wstring& launcherPath)
{
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!GetFileAttributesExW(launcherPath.c_str(), GetFileExInfoStandard, &attributes))
    {
        return L"";
    }

    const auto size = (static_cast<unsigned long long>(attributes.nFileSizeHigh) << 32) | attributes.nFileSizeLow;
    const auto stamp = (static_cast<unsigned long long>(attributes.ftLastWriteTime.dwHighDateTime) << 32) |
                       attributes.ftLastWriteTime.dwLowDateTime;
    // ponytail: keep one cache per launcher build; prune old caches only if disk use becomes material.
    return extractionBase + L"\\LogRAM\\" + std::to_wstring(size) + L"-" + std::to_wstring(stamp);
}

bool LoadPayload(Resource* payload)
{
    const HRSRC resource = FindResourceW(nullptr, MAKEINTRESOURCEW(PayloadResourceId), RT_RCDATA);
    if (resource == nullptr)
    {
        return false;
    }

    const HGLOBAL loaded = LoadResource(nullptr, resource);
    payload->data = static_cast<const BYTE*>(LockResource(loaded));
    payload->size = SizeofResource(nullptr, resource);
    return payload->data != nullptr && payload->size > 0;
}

bool HasPayload(const std::wstring& path, DWORD expectedSize)
{
    WIN32_FILE_ATTRIBUTE_DATA attributes{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attributes))
    {
        return false;
    }

    const auto size = (static_cast<unsigned long long>(attributes.nFileSizeHigh) << 32) | attributes.nFileSizeLow;
    return size == expectedSize;
}

bool WritePayload(const std::wstring& path, const Resource& payload)
{
    if (HasPayload(path, payload.size))
    {
        return true;
    }

    const std::wstring temporaryPath = path + L"." + std::to_wstring(GetCurrentProcessId()) + L".tmp";
    const HANDLE file = CreateFileW(temporaryPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }

    DWORD offset = 0;
    while (offset < payload.size)
    {
        const DWORD chunkSize = std::min<DWORD>(payload.size - offset, 1024 * 1024);
        DWORD written = 0;
        if (!WriteFile(file, payload.data + offset, chunkSize, &written, nullptr) || written != chunkSize)
        {
            CloseHandle(file);
            DeleteFileW(temporaryPath.c_str());
            return false;
        }

        offset += written;
    }

    const bool written = FlushFileBuffers(file) != FALSE;
    CloseHandle(file);
    if (!written || !MoveFileExW(temporaryPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
    {
        DeleteFileW(temporaryPath.c_str());
        return false;
    }

    return true;
}

std::wstring QuoteArgument(const std::wstring& argument)
{
    if (argument.find_first_of(L" \t\"") == std::wstring::npos)
    {
        return argument;
    }

    std::wstring quoted = L"\"";
    size_t backslashes = 0;
    for (const wchar_t character : argument)
    {
        if (character == L'\\')
        {
            ++backslashes;
        }
        else if (character == L'\"')
        {
            quoted.append(backslashes * 2 + 1, L'\\');
            quoted += character;
            backslashes = 0;
        }
        else
        {
            quoted.append(backslashes, L'\\');
            quoted += character;
            backslashes = 0;
        }
    }

    quoted.append(backslashes * 2, L'\\');
    return quoted + L"\"";
}

bool LaunchPayload(const std::wstring& payloadPath, const std::wstring& workingDirectory, DWORD* exitCode)
{
    int argumentCount = 0;
    LPWSTR* arguments = CommandLineToArgvW(GetCommandLineW(), &argumentCount);
    std::wstring commandLine = QuoteArgument(payloadPath);
    for (int index = 1; index < argumentCount; ++index)
    {
        commandLine += L" " + QuoteArgument(arguments[index]);
    }
    LocalFree(arguments);

    std::vector<wchar_t> command(commandLine.begin(), commandLine.end());
    command.push_back(L'\0');
    STARTUPINFOW startupInfo{ sizeof(startupInfo) };
    PROCESS_INFORMATION processInfo{};
    if (!CreateProcessW(payloadPath.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, workingDirectory.c_str(), &startupInfo, &processInfo))
    {
        return false;
    }

    CloseHandle(processInfo.hThread);
    WaitForSingleObject(processInfo.hProcess, INFINITE);
    GetExitCodeProcess(processInfo.hProcess, exitCode);
    CloseHandle(processInfo.hProcess);
    return true;
}
}

int APIENTRY wWinMain(HINSTANCE, HINSTANCE, PWSTR, int)
{
    const std::wstring extractionBase = SelectExtractionBase();
    const std::wstring payloadDirectory = GetPayloadDirectory(extractionBase, GetModulePath());
    if (payloadDirectory.empty() || !EnsureDirectory(payloadDirectory))
    {
        ShowError(L"无法创建 LogRAM 运行环境。请检查临时目录或其上级目录的写入权限。");
        return 1;
    }

    Resource payload{};
    if (!LoadPayload(&payload))
    {
        ShowError(L"LogRAM 安装包已损坏，请重新下载。");
        return 1;
    }

    const std::wstring payloadPath = payloadDirectory + L"\\LogRAM.exe";
    if (!WritePayload(payloadPath, payload))
    {
        ShowError(L"无法写入 LogRAM 运行文件。请检查临时目录或其上级目录的写入权限。");
        return 1;
    }

    DWORD exitCode = 0;
    if (!SetEnvironmentVariableW(L"DOTNET_BUNDLE_EXTRACT_BASE_DIR", extractionBase.c_str()) ||
        !LaunchPayload(payloadPath, payloadDirectory, &exitCode))
    {
        ShowError(L"无法启动 LogRAM。请检查安全软件是否阻止了程序运行。");
        return 1;
    }

    if (exitCode != 0)
    {
        ShowError(L"LogRAM 启动失败，错误码：" + std::to_wstring(exitCode));
        return static_cast<int>(exitCode);
    }

    return 0;
}
