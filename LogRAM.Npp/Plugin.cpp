#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <commctrl.h>
#include <string>
#include <vector>
#include <map>
#include <set>
#include "LogOpenRouter.h"

// ABI and message values: Notepad++ PluginInterface.h and Notepad_plus_msgs.h.
// https://github.com/notepad-plus-plus/notepad-plus-plus/tree/master/PowerEditor/src
struct NppData { HWND npp, mainView, secondView; };
struct ShortcutKey { bool ctrl, alt, shift; unsigned char key; };
struct FuncItem { wchar_t name[64]; void (__cdecl *function)(); int command; bool checked; ShortcutKey* shortcut; };
constexpr UINT NppMessage = WM_USER + 1000;
constexpr UINT MenuCheck = NppMessage + 40, ConfigDirectory = NppMessage + 46, DarkMode = NppMessage + 107;
constexpr UINT CurrentPath = WM_USER + 3001, ViewerReady = WM_APP + 1, HideViewer = WM_APP + 2, HostCommand = WM_APP + 3;
static HINSTANCE instance;
static NppData npp{};
static HWND panel{}, viewer{};
static HANDLE input{}, process{}, job{};
static std::wstring pendingPath;
static bool editorsHidden{}, mainWasVisible{}, secondWasVisible{};
static FuncItem commands[7]{};
static ShortcutKey workspaceShortcut{true, true, false, 'L'};
static bool nppReady{}, routerInstalled{};
static UINT autoRouteMessage{};
static UINT_PTR routingGeneration{};
static std::map<UINT_PTR, std::wstring> routedBuffers;
static std::map<UINT_PTR, int> encodingCommands;
static std::set<UINT_PTR> restoredDirtyBuffers;

static void Error(const wchar_t* message) { MessageBoxW(npp.npp, message, L"LogRAM", MB_OK | MB_ICONERROR); }

static bool Send(const std::wstring& command) {
    if (!input) return false;
    auto line = command + L"\n";
    int length = WideCharToMultiByte(CP_UTF8, 0, line.data(), static_cast<int>(line.size()), nullptr, 0, nullptr, nullptr);
    std::string bytes(length, '\0');
    WideCharToMultiByte(CP_UTF8, 0, line.data(), static_cast<int>(line.size()), bytes.data(), length, nullptr, nullptr);
    DWORD written{};
    return WriteFile(input, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) && written == bytes.size();
}

static void SendShortcut() {
    const unsigned int keys = workspaceShortcut.key | (workspaceShortcut.ctrl ? 0x20000 : 0) |
        (workspaceShortcut.alt ? 0x40000 : 0) | (workspaceShortcut.shift ? 0x10000 : 0);
    Send(L"shortcut\t" + std::to_wstring(workspaceShortcut.key ? keys : 0));
}

static void SendLanguage() {
    char language[256]{};
    SendMessageW(npp.npp, NppMessage + 116, sizeof(language), reinterpret_cast<LPARAM>(language));
    Send(std::string(language).find("chinese") != std::string::npos ? L"language\tzh" : L"language\ten");
}

static void UpdateEncodingMenu() {
    const auto buffer = static_cast<UINT_PTR>(SendMessageW(npp.npp, NppMessage + 60, 0, 0));
    if (routedBuffers.find(buffer) == routedBuffers.end()) return;
    const auto selected = encodingCommands.find(buffer);
    const int command = selected == encodingCommands.end() ? 45008 : selected->second;
    HMENU menu = GetMenu(npp.npp);
    for (int id = 45004; id <= 45008; ++id) CheckMenuItem(menu, id, MF_BYCOMMAND | (id == command ? MF_CHECKED : MF_UNCHECKED));
    for (int id = 45020; id <= 45068; ++id) CheckMenuItem(menu, id, MF_BYCOMMAND | (id == command ? MF_CHECKED : MF_UNCHECKED));
}

static void Stop() {
    viewer = nullptr;
    pendingPath.clear();
    if (input) { CloseHandle(input); input = nullptr; }
    if (job) { CloseHandle(job); job = nullptr; }
    if (process) {
        if (WaitForSingleObject(process, 0) == WAIT_TIMEOUT) TerminateProcess(process, 0);
        CloseHandle(process); process = nullptr;
    }
}

static void ResizeViewer() {
    if (!IsWindow(viewer)) return;
    RECT rect{};
    GetClientRect(panel, &rect);
    SetWindowPos(viewer, nullptr, 0, 0, rect.right, rect.bottom, SWP_NOZORDER | SWP_NOACTIVATE);
}

static void LayoutCanvas() {
    if (!panel || !IsWindowVisible(panel)) return;
    RECT area{};
    bool haveArea = false;
    const HWND views[] = {npp.mainView, npp.secondView};
    for (int index = 0; index < 2; ++index) {
        HWND view = views[index];
        const bool enabled = editorsHidden ? (index == 0 ? mainWasVisible : secondWasVisible) :
            (IsWindowVisible(view) != FALSE);
        if (!enabled) continue;
        RECT rect{};
        GetWindowRect(view, &rect);
        MapWindowPoints(nullptr, npp.npp, reinterpret_cast<POINT*>(&rect), 2);
        if (!haveArea) { area = rect; haveArea = true; }
        else UnionRect(&area, &area, &rect);
    }
    if (!haveArea) return;
    RECT current{};
    GetWindowRect(panel, &current);
    MapWindowPoints(nullptr, npp.npp, reinterpret_cast<POINT*>(&current), 2);
    if (!EqualRect(&area, &current))
        SetWindowPos(panel, HWND_TOP, area.left, area.top, area.right - area.left, area.bottom - area.top, SWP_NOACTIVATE);
    if (!editorsHidden) {
        mainWasVisible = IsWindowVisible(npp.mainView) != FALSE;
        secondWasVisible = IsWindowVisible(npp.secondView) != FALSE;
        ShowWindow(npp.mainView, SW_HIDE);
        ShowWindow(npp.secondView, SW_HIDE);
        editorsHidden = true;
    }
}

static void ReturnToEditor() {
    ++routingGeneration;
    if (panel) ShowWindow(panel, SW_HIDE);
    if (editorsHidden) {
        if (mainWasVisible) ShowWindow(npp.mainView, SW_SHOW);
        if (secondWasVisible) ShowWindow(npp.secondView, SW_SHOW);
        editorsHidden = false;
    }
    SendMessageW(npp.npp, MenuCheck, commands[0].command, FALSE);
    int currentView{};
    SendMessageW(npp.npp, NppMessage + 4, 0, reinterpret_cast<LPARAM>(&currentView));
    HWND activeView = currentView == 1 ? npp.secondView : npp.mainView;
    if (IsWindowVisible(activeView)) SetFocus(activeView);
}

static INT_PTR CALLBACK PanelProc(HWND window, UINT message, WPARAM wparam, LPARAM) {
    switch (message) {
    case ViewerReady: {
        DWORD childPid{}, workerPid = process ? GetProcessId(process) : 0;
        GetWindowThreadProcessId(reinterpret_cast<HWND>(wparam), &childPid);
        if (!workerPid || childPid != workerPid) return TRUE;
        viewer = reinterpret_cast<HWND>(wparam);
        ResizeViewer();
        if (!SendMessageW(npp.npp, NppMessage + 76, commands[0].command, reinterpret_cast<LPARAM>(&workspaceShortcut))) workspaceShortcut.key = 0;
        SendShortcut();
        SendLanguage();
        Send(SendMessageW(npp.npp, DarkMode, 0, 0) ? L"theme\t1" : L"theme\t0");
        if (!pendingPath.empty()) { Send(pendingPath); pendingPath.clear(); }
        return TRUE;
    }
    case WM_SIZE: ResizeViewer(); return TRUE;
    case HideViewer: ReturnToEditor(); return TRUE;
    case HostCommand:
        if (wparam == 41002 || wparam == 41003 || wparam == 41001 || wparam == 44095 || wparam == 44096)
            PostMessageW(npp.npp, WM_COMMAND, wparam, 0);
        return TRUE;
    case WM_SHOWWINDOW: SendMessageW(npp.npp, MenuCheck, commands[0].command, wparam != 0); return TRUE;
    case WM_SETFOCUS: if (IsWindow(viewer)) Send(L"focus"); return TRUE;
    case WM_TIMER:
        LayoutCanvas();
        if (process && WaitForSingleObject(process, 0) != WAIT_TIMEOUT) {
            DWORD exitCode{}; GetExitCodeProcess(process, &exitCode);
            KillTimer(window, 1); Stop();
            ReturnToEditor();
            const auto exitMessage = L"The LogRAM worker has exited (code " + std::to_wstring(exitCode) + L"). Use Plugins > LogRAM > Restart LogRAM worker to reopen it.";
            Error(exitMessage.c_str());
        }
        return TRUE;
    }
    return FALSE;
}

static bool Start() {
    if (process && WaitForSingleObject(process, 0) == WAIT_TIMEOUT) return true;
    Stop();
    wchar_t module[32768]{};
    GetModuleFileNameW(instance, module, 32768);
    std::wstring executable = module;
    executable.resize(executable.find_last_of(L"\\/") + 1);
    executable += L"LogRAM.Npp.Viewer.exe";
    if (GetFileAttributesW(executable.c_str()) == INVALID_FILE_ATTRIBUTES) {
        Error(L"LogRAM.Npp.Viewer.exe is missing. Copy the entire LogRAM.Npp plugin folder, including its runtime files.");
        return false;
    }
    wchar_t config[32768]{};
    SendMessageW(npp.npp, ConfigDirectory, 32768, reinterpret_cast<LPARAM>(config));
    if (!config[0]) { Error(L"Cannot locate the Notepad++ plugin configuration directory."); return false; }
    DWORD owner{};
    GetWindowThreadProcessId(npp.npp, &owner);
    std::wstring arguments = L"\"" + executable + L"\" " + std::to_wstring(reinterpret_cast<INT_PTR>(panel)) + L" " +
        std::to_wstring(owner) + L" \"" + config + L"\"";
    SECURITY_ATTRIBUTES security{sizeof(SECURITY_ATTRIBUTES), nullptr, TRUE};
    HANDLE read{};
    if (!CreatePipe(&read, &input, &security, 0) || !SetHandleInformation(input, HANDLE_FLAG_INHERIT, 0)) {
        if (read) CloseHandle(read);
        Stop(); Error(L"Cannot create the LogRAM command pipe."); return false;
    }
    HANDLE nullOutput = CreateFileW(L"NUL", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, &security, OPEN_EXISTING, 0, nullptr);
    CreateDirectoryW(config, nullptr);
    auto logPath = std::wstring(config) + L"\\LogRAM.Npp.worker.log";
    HANDLE errorOutput = CreateFileW(logPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, &security, CREATE_ALWAYS, 0, nullptr);
    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = read; startup.hStdOutput = nullOutput;
    startup.hStdError = errorOutput == INVALID_HANDLE_VALUE ? nullOutput : errorOutput;
    PROCESS_INFORMATION child{};
    BOOL started = CreateProcessW(executable.c_str(), arguments.data(), nullptr, nullptr, TRUE,
        CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr, nullptr, &startup, &child);
    CloseHandle(read);
    if (nullOutput != INVALID_HANDLE_VALUE) CloseHandle(nullOutput);
    if (errorOutput != INVALID_HANDLE_VALUE) CloseHandle(errorOutput);
    if (!started) { Stop(); Error(L"Cannot start LogRAM.Npp.Viewer.exe. Check the complete plugin package and its permissions."); return false; }
    process = child.hProcess;
    job = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    if (!job || !SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) || !AssignProcessToJobObject(job, process)) {
        CloseHandle(child.hThread); Stop(); Error(L"Cannot attach the LogRAM worker to its shutdown job."); return false;
    }
    ResumeThread(child.hThread);
    CloseHandle(child.hThread);
    SetTimer(panel, 1, 250, nullptr);
    return true;
}

static bool Show() {
    if (!panel) {
        struct DialogTemplate { DLGTEMPLATE dialog; WORD menu, windowClass, title; } layout{};
        layout.dialog.style = WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | DS_CONTROL;
        layout.dialog.cx = 600; layout.dialog.cy = 400;
        panel = CreateDialogIndirectParamW(instance, &layout.dialog, npp.npp, PanelProc, 0);
        if (!panel) { Error(L"Cannot create the LogRAM workspace."); return false; }
    }
    ShowWindow(panel, SW_SHOW);
    LayoutCanvas();
    SetWindowPos(panel, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    if (!Start()) { ReturnToEditor(); return false; }
    return true;
}

static void ShowPanel() {
    ++routingGeneration;
    if (panel && IsWindowVisible(panel)) { ReturnToEditor(); return; }
    if (Show()) Send(L"focus");
}
static void OpenLog() { PostMessageW(npp.npp, WM_COMMAND, 41002, 0); }
static void OpenCurrent() {
    ++routingGeneration;
    wchar_t path[32768]{};
    SendMessageW(npp.npp, CurrentPath, 32768, reinterpret_cast<LPARAM>(path));
    if (!path[0] || GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES) { Error(L"The current document must be saved before opening its on-disk content in LogRAM."); return; }
    if (!Show()) return;
    const auto buffer = static_cast<UINT_PTR>(SendMessageW(npp.npp, NppMessage + 60, 0, 0));
    const auto encoding = encodingCommands.find(buffer);
    const std::wstring command = std::wstring(encoding != encodingCommands.end() && encoding->second == 45061 ? L"open\tgbk\t" : L"open\tutf8\t") + path;
    if (viewer) Send(command); else pendingPath = command;
    UpdateEncodingMenu();
}

static void RouteCurrentLog() {
    const auto buffer = static_cast<UINT_PTR>(SendMessageW(npp.npp, NppMessage + 60, 0, 0));
    if (restoredDirtyBuffers.find(buffer) != restoredDirtyBuffers.end()) { ReturnToEditor(); return; }
    wchar_t path[32768]{};
    SendMessageW(npp.npp, NppMessage + 58, buffer, reinterpret_cast<LPARAM>(path));
    if (!path[0] || !IsRoutedLog(path)) { if (panel && IsWindowVisible(panel)) ReturnToEditor(); return; }
    HWND editor = SendMessageW(npp.npp, NppMessage + 88, 0, 0) == 1 ? npp.secondView : npp.mainView;
    // Preserve restored, unsaved session snapshots instead of replacing them with an on-disk log.
    if (SendMessageW(editor, 2159, 0, 0)) { ReturnToEditor(); return; } // SCI_GETMODIFY
    if (SendMessageW(editor, 2006, 0, 0) == 0) { // SCI_GETLENGTH
        const char notice[] = "This .log is viewed in the LogRAM workspace.\r\nUse Plugins > LogRAM > Toggle LogRAM workspace to return.\r\n";
        SendMessageW(editor, 2171, FALSE, 0); // SCI_SETREADONLY
        SendMessageW(editor, 2001, sizeof(notice) - 1, reinterpret_cast<LPARAM>(notice)); // SCI_ADDTEXT: same process
        SendMessageW(editor, 2014, 0, 0); // SCI_SETSAVEPOINT
        SendMessageW(editor, 2175, 0, 0); // SCI_EMPTYUNDOBUFFER
    }
    if (!SendMessageW(editor, 2140, 0, 0)) SendMessageW(npp.npp, NppMessage + 48, 0, 42028); // NPPM_MENUCOMMAND: Toggle Read-Only
    routedBuffers[buffer] = path;
    OpenCurrent();
}

static LRESULT CALLBACK HostProc(HWND window, UINT message, WPARAM wparam, LPARAM lparam, UINT_PTR subclass, DWORD_PTR) {
    if (message == WM_COMMAND && HIWORD(wparam) == 0 && LOWORD(wparam) >= 45004 && LOWORD(wparam) <= 45068) {
        const auto buffer = static_cast<UINT_PTR>(SendMessageW(npp.npp, NppMessage + 60, 0, 0));
        if (routedBuffers.find(buffer) != routedBuffers.end()) {
            const int command = LOWORD(wparam);
            // NPP's encoding handlers can dirty or reload the placeholder; change only the viewer's decoding.
            if (command == 45005 || command == 45008 || command == 45061 || (command == 45004 && GetACP() == 936)) {
                encodingCommands[buffer] = command == 45004 ? 45061 : command;
                OpenCurrent();
            } else Error(L"LogRAM is read-only and supports UTF-8 and GBK. Select Encoding > UTF-8 or Character sets > Chinese > GB2312. Encoding conversion is unavailable for log files.");
            return 0;
        }
    }
    if (autoRouteMessage && message == autoRouteMessage) {
        if (routerInstalled && nppReady && static_cast<UINT_PTR>(lparam) == routingGeneration &&
            static_cast<UINT_PTR>(SendMessageW(npp.npp, NppMessage + 60, 0, 0)) == wparam) RouteCurrentLog();
        return 0;
    }
    if (message == WM_NCDESTROY) { RemoveLogOpenRouter(); RemoveWindowSubclass(window, HostProc, subclass); }
    const auto result = DefSubclassProc(window, message, wparam, lparam);
    if (message == WM_INITMENUPOPUP) UpdateEncodingMenu();
    return result;
}
static void CloseLogs() { if (input) Send(L"closeall"); }
static void Restart() { Stop(); if (panel && !Start()) ReturnToEditor(); }
static void About() {
    MessageBoxW(npp.npp, L"LogRAM 1.3.4 — Notepad++ plugin\n\nRead-only large-log viewer and SIMD/multicore search.\n"
        L".log files automatically open in LogRAM without loading their content into the NPP editor.\nThe current saved file command reads the disk file, not unsaved edits.\n\n"
        L"Ctrl+F: Search   Ctrl+G: Go to line   F3 / Shift+F3: Results\nUTF-8 / GBK; in(error,warning);notin(retry)", L"LogRAM", MB_OK | MB_ICONINFORMATION);
}

extern "C" __declspec(dllexport) void setInfo(NppData data) {
    npp = data;
    autoRouteMessage = RegisterWindowMessageW(L"LogRAM.Npp.AutoRouteCurrentLog");
    routerInstalled = InstallLogOpenRouter();
    if (!SetWindowSubclass(npp.npp, HostProc, reinterpret_cast<UINT_PTR>(instance), 0)) { RemoveLogOpenRouter(); routerInstalled = false; }
}
extern "C" __declspec(dllexport) const wchar_t* getName() { return L"LogRAM"; }
extern "C" __declspec(dllexport) BOOL isUnicode() { return TRUE; }
extern "C" __declspec(dllexport) FuncItem* getFuncsArray(int* count) {
    const wchar_t* names[] = {L"Toggle LogRAM workspace", L"Open log...", L"Open current saved file", L"Return to Notepad++", L"Close all logs / release memory", L"Restart LogRAM worker", L"About"};
    void (__cdecl *functions[])() = {ShowPanel, OpenLog, OpenCurrent, ReturnToEditor, CloseLogs, Restart, About};
    for (int i = 0; i < 7; ++i) { lstrcpynW(commands[i].name, names[i], 64); commands[i].function = functions[i]; }
    commands[0].shortcut = &workspaceShortcut;
    *count = 7; return commands;
}
extern "C" __declspec(dllexport) void beNotified(void* notification) {
    const auto* header = static_cast<NMHDR*>(notification);
    if (!header) return;
    if (header->code == 1018) restoredDirtyBuffers.insert(header->idFrom);
    if (routerInstalled && header->code == 1014) BeginLogLoad();
    if (routerInstalled && (header->code == 1004 || header->code == 1015)) EndLogLoad();
    if (header->code == 1001) {
        nppReady = true;
        if (!routerInstalled) Error(L"This Notepad++ build does not support automatic .log routing. Open an existing document with Plugins > LogRAM > Open current saved file.");
        else PostMessageW(npp.npp, autoRouteMessage, SendMessageW(npp.npp, NppMessage + 60, 0, 0), static_cast<LPARAM>(routingGeneration));
    }
    if (header->code == 1009) { nppReady = false; ReturnToEditor(); Stop(); RemoveLogOpenRouter(); }
    if ((header->code == 1004 || header->code == 1010) && nppReady) PostMessageW(npp.npp, autoRouteMessage, header->idFrom, static_cast<LPARAM>(routingGeneration));
    if (header->code == 1005) {
        encodingCommands.erase(header->idFrom);
        restoredDirtyBuffers.erase(header->idFrom);
        const auto closed = routedBuffers.find(header->idFrom);
        if (closed != routedBuffers.end()) { Send(L"close\t" + closed->second); routedBuffers.erase(closed); }
    }
    if (header->code == 1013 && header->idFrom == static_cast<UINT_PTR>(commands[0].command) && header->hwndFrom) {
        workspaceShortcut = *reinterpret_cast<ShortcutKey*>(header->hwndFrom);
        SendShortcut();
    }
    if (header->code == 1027 && viewer) Send(SendMessageW(npp.npp, DarkMode, 0, 0) ? L"theme\t1" : L"theme\t0");
    if (header->code == 1031 && viewer) SendLanguage();
}
extern "C" __declspec(dllexport) LRESULT messageProc(UINT, WPARAM, LPARAM) { return TRUE; }
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) { instance = module; DisableThreadLibraryCalls(module); }
    return TRUE;
}
