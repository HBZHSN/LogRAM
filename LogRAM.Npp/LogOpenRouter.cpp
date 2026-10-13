#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include "LogOpenRouter.h"
#include <cwctype>
#include <cstring>
#include <map>
#include <vector>

static decltype(&CreateFileW) originalCreateFile = &CreateFileW;
static decltype(&GetFileAttributesExW) originalAttributes = &GetFileAttributesExW;
static SRWLOCK routedLock = SRWLOCK_INIT;
static std::map<std::wstring, FILETIME> routedPaths;
static std::vector<std::wstring> loadCandidates;
static void** createSlot{};
static void** attributesSlot{};

static bool HasLogExtension(const wchar_t* path) {
    if (!path) return false;
    const auto length = wcslen(path);
    return length >= 4 && _wcsicmp(path + length - 4, L".log") == 0;
}

static std::wstring PathKey(const wchar_t* path) {
    wchar_t full[32768]{};
    const DWORD length = GetFullPathNameW(path, 32768, full, nullptr);
    std::wstring result = length && length < 32768 ? full : path;
    if (result.compare(0, 8, L"\\\\?\\UNC\\") == 0) result = L"\\\\" + result.substr(8);
    else if (result.compare(0, 4, L"\\\\?\\") == 0) result.erase(0, 4);
    for (auto& ch : result) ch = static_cast<wchar_t>(towlower(ch));
    return result;
}

bool IsRoutedLog(const std::wstring& path) {
    const auto key = PathKey(path.c_str());
    AcquireSRWLockShared(&routedLock);
    const bool found = routedPaths.find(key) != routedPaths.end();
    ReleaseSRWLockShared(&routedLock);
    return found;
}

void BeginLogLoad() {
    AcquireSRWLockExclusive(&routedLock);
    try { loadCandidates.emplace_back(); }
    catch (...) { loadCandidates.clear(); }
    ReleaseSRWLockExclusive(&routedLock);
}

void EndLogLoad() {
    AcquireSRWLockExclusive(&routedLock);
    if (!loadCandidates.empty()) loadCandidates.pop_back();
    ReleaseSRWLockExclusive(&routedLock);
}

static BOOL WINAPI LogAttributes(LPCWSTR path, GET_FILEEX_INFO_LEVELS level, LPVOID data) {
    const BOOL result = originalAttributes(path, level, data);
    if (result && level == GetFileExInfoStandard && HasLogExtension(path)) {
        auto* attributes = static_cast<WIN32_FILE_ATTRIBUTE_DATA*>(data);
        if (!(attributes->dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
            try {
                const auto key = PathKey(path);
                AcquireSRWLockExclusive(&routedLock);
                bool route{};
                try {
                    const auto found = routedPaths.find(key);
                    if (found != routedPaths.end()) {
                        // LogRAM owns live updates; do not trigger NPP's external-change reload prompts.
                        attributes->ftLastWriteTime = found->second;
                        route = true;
                    } else if (!loadCandidates.empty()) {
                        if (loadCandidates.back().empty()) loadCandidates.back() = key;
                        route = loadCandidates.back() == key;
                    }
                } catch (...) { ReleaseSRWLockExclusive(&routedLock); throw; }
                ReleaseSRWLockExclusive(&routedLock);
                // Allocate only an empty tab; a different backup path keeps its actual contents.
                if (route) attributes->nFileSizeLow = attributes->nFileSizeHigh = 0;
            } catch (...) { }
        }
    }
    return result;
}

static HANDLE WINAPI LogCreateFile(LPCWSTR path, DWORD access, DWORD share, LPSECURITY_ATTRIBUTES security,
    DWORD disposition, DWORD flags, HANDLE templateFile) {
    if (!HasLogExtension(path)) return originalCreateFile(path, access, share, security, disposition, flags, templateFile);
    try {
        if ((access & (GENERIC_WRITE | DELETE | FILE_WRITE_DATA | FILE_APPEND_DATA)) && IsRoutedLog(path)) {
            // A placeholder must never be saved back over the actual log, even if read-only is manually unset.
            SetLastError(ERROR_ACCESS_DENIED);
            return INVALID_HANDLE_VALUE;
        }
        if ((access & GENERIC_READ) && !(access & GENERIC_WRITE) && disposition == OPEN_EXISTING) {
            const auto key = PathKey(path);
            AcquireSRWLockShared(&routedLock);
            const bool route = routedPaths.find(key) != routedPaths.end() ||
                (!loadCandidates.empty() && loadCandidates.back() == key);
            ReleaseSRWLockShared(&routedLock);
            if (!route) return originalCreateFile(path, access, share, security, disposition, flags, templateFile);
            WIN32_FILE_ATTRIBUTE_DATA attributes{};
            if (originalAttributes(path, GetFileExInfoStandard, &attributes) && !(attributes.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) {
                HANDLE source = originalCreateFile(path, access, share, security, disposition, flags, templateFile);
                if (source == INVALID_HANDLE_VALUE) return source;
                CloseHandle(source);
                HANDLE empty = originalCreateFile(L"NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    security, OPEN_EXISTING, 0, nullptr);
                if (empty == INVALID_HANDLE_VALUE) return empty;
                AcquireSRWLockExclusive(&routedLock);
                try { routedPaths.try_emplace(key, attributes.ftLastWriteTime); }
                catch (...) { ReleaseSRWLockExclusive(&routedLock); CloseHandle(empty); throw; }
                ReleaseSRWLockExclusive(&routedLock);
                return empty;
            }
        }
    }
    catch (...) { SetLastError(ERROR_NOT_ENOUGH_MEMORY); return INVALID_HANDLE_VALUE; }
    return originalCreateFile(path, access, share, security, disposition, flags, templateFile);
}

static bool ReplaceImport(void** slot, void* replacement) {
    DWORD protection{};
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &protection)) return false;
    InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(slot), replacement);
    DWORD ignored{};
    VirtualProtect(slot, sizeof(void*), protection, &ignored);
    return true;
}

bool InstallLogOpenRouter() {
    auto* image = reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr));
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(image);
    if (!image || dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(image + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return false;
    const auto imports = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!imports.VirtualAddress) return false;
    auto* descriptor = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(image + imports.VirtualAddress);
    for (; descriptor->Name; ++descriptor) {
        if (!descriptor->OriginalFirstThunk) continue;
        auto* names = reinterpret_cast<IMAGE_THUNK_DATA*>(image + descriptor->OriginalFirstThunk);
        auto* slots = reinterpret_cast<IMAGE_THUNK_DATA*>(image + descriptor->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++slots) {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            const auto* entry = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(image + names->u1.AddressOfData);
            if (std::strcmp(reinterpret_cast<const char*>(entry->Name), "CreateFileW") == 0) createSlot = reinterpret_cast<void**>(&slots->u1.Function);
            if (std::strcmp(reinterpret_cast<const char*>(entry->Name), "GetFileAttributesExW") == 0) attributesSlot = reinterpret_cast<void**>(&slots->u1.Function);
        }
    }
    if (!createSlot || !attributesSlot) return false;
    originalCreateFile = reinterpret_cast<decltype(originalCreateFile)>(*createSlot);
    originalAttributes = reinterpret_cast<decltype(originalAttributes)>(*attributesSlot);
    if (!ReplaceImport(createSlot, reinterpret_cast<void*>(&LogCreateFile))) return false;
    if (!ReplaceImport(attributesSlot, reinterpret_cast<void*>(&LogAttributes))) {
        ReplaceImport(createSlot, reinterpret_cast<void*>(originalCreateFile));
        return false;
    }
    return true;
}

void RemoveLogOpenRouter() {
    if (createSlot && *createSlot == reinterpret_cast<void*>(&LogCreateFile)) ReplaceImport(createSlot, reinterpret_cast<void*>(originalCreateFile));
    if (attributesSlot && *attributesSlot == reinterpret_cast<void*>(&LogAttributes)) ReplaceImport(attributesSlot, reinterpret_cast<void*>(originalAttributes));
}
