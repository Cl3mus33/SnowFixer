#include "SFConfig.hpp"
#include "SFLocale.hpp"
#include "GUI/LauncherWindow.hpp"
#include "GUI/ProgressWindow.hpp"
#include "util/ExceptionHandler.hpp"

#include <cpptrace/from_current.hpp>
#include <wx/wx.h>

#include <array>
#include <exception>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <knownfolders.h>
#include <shlobj.h>
#include <windows.h>

using namespace std;

namespace {
// Writes to %APPDATA%\SnowFixer\native-startup.log, flushed after every line. Exists
// specifically so a silent early-startup failure (e.g. wxEntryStart failing, where the console is
// hidden before anything gets a chance to print) still leaves a record on disk - mirrors
// AutoBlend's own main.cpp startupLog() reasoning.
void startupLog(const string& message)
{
    PWSTR rawPath = nullptr;
    filesystem::path logPath;
    if (SHGetKnownFolderPath(FOLDERID_RoamingAppData, 0, nullptr, &rawPath) == S_OK) {
        logPath = filesystem::path(rawPath) / "SnowFixer" / "native-startup.log";
    }
    if (rawPath != nullptr) {
        CoTaskMemFree(rawPath);
    }
    if (logPath.empty()) {
        return;
    }

    error_code ec;
    filesystem::create_directories(logPath.parent_path(), ec);

    ofstream file(logPath, ios::app);
    if (file.is_open()) {
        file << message << "\n";
    }
}

auto getExecutablePath() -> filesystem::path
{
    array<wchar_t, MAX_PATH> buffer {};
    if (GetModuleFileNameW(nullptr, buffer.data(), MAX_PATH) == 0) {
        startupLog("getExecutablePath: GetModuleFileNameW failed, error " + to_string(GetLastError()));
        cerr << "Error getting executable path: " << GetLastError() << "\n";
        exit(1);
    }

    filesystem::path outPath = filesystem::path(buffer.data());
    if (filesystem::exists(outPath)) {
        return outPath;
    }

    startupLog("getExecutablePath: resolved path does not exist");
    cerr << "Error getting executable path: path does not exist\n";
    exit(1);
    return {};
}

void configureDotnetLibDirectory(const filesystem::path& exeDir)
{
    // Removes the current working directory from the default LoadLibrary search order (Microsoft's
    // documented DLL-planting mitigation) - called unconditionally, before the
    // SnowFixer_dotnetlib existence check below, so a corrupted/incomplete install doesn't
    // leave the process running its whole lifetime with an unprotected search order.
    if (SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS) == 0) {
        startupLog("configureDotnetLibDirectory: SetDefaultDllDirectories failed, error " + to_string(GetLastError()));
        cerr << "Failed to configure DLL search directories.\n";
        exit(1);
    }

    // Deliberately named SnowFixer_dotnetlib rather than "dotnetlib" - AutoBlend ships its
    // own AutoBlend_dotnetlib/ full of similar self-contained .NET runtime files, so a generic name
    // would collide almost entirely when both tools are installed as MO2 mods in the same modlist.
    const auto libDir = exeDir / "SnowFixer_dotnetlib";
    if (!filesystem::exists(libDir)) {
        startupLog("configureDotnetLibDirectory: " + libDir.string() + " does not exist, skipping");
        return;
    }

    if (AddDllDirectory(libDir.c_str()) == nullptr) {
        startupLog("configureDotnetLibDirectory: AddDllDirectory failed, error " + to_string(GetLastError()));
        cerr << "Failed to add dotnetlib directory to DLL search path.\n";
        exit(1);
    }

    startupLog("configureDotnetLibDirectory: configured " + libDir.string());
}

// A C++ exception thrown from inside a wx event handler (button click, timer tick) is intercepted
// by wx's own event-dispatch machinery before it would ever reach the CPPTRACE_TRY in main() below
// - the plain wxApp used previously left that path unlogged. No handler currently throws, but this
// costs nothing and means a future one that does still leaves a record in native-startup.log
// instead of vanishing into wx's own (version-dependent) default handling.
class SnowFixerApp : public wxApp {
public:
    auto OnExceptionInMainLoop() -> bool override
    {
        try {
            throw;
        } catch (const exception& e) {
            startupLog(string("OnExceptionInMainLoop: ") + e.what());
        } catch (...) {
            startupLog("OnExceptionInMainLoop: exception of unknown type");
        }
        throw; // preserve wx's own default handling (typically terminates) once logged
    }
};

auto runGUI(const filesystem::path& exePath) -> int
{
    startupLog("runGUI: entered");

    wxApp::SetInstance(new SnowFixerApp()); // NOLINT(cppcoreguidelines-owning-memory)
    if (!wxEntryStart(nullptr, nullptr)) {
        startupLog("runGUI: wxEntryStart failed");
        cerr << "Failed to initialize wxWidgets.\n";
        return 1;
    }
    startupLog("runGUI: wxEntryStart succeeded");

    // Only hide the console once wx is confirmed alive - if wxEntryStart or anything below it
    // fails, a console window (and this function's own cerr output) staying visible is one more
    // way to notice, on top of the startup log above.
    if (HWND consoleWindow = GetConsoleWindow(); consoleWindow != nullptr) {
        ShowWindow(consoleWindow, SW_HIDE);
    }

    auto params = SFConfig::load();
    startupLog("runGUI: settings loaded");
    if (params.outputLocation.empty()) {
        params.outputLocation = (exePath / "SnowFixer_Output").wstring();
    }

    SFLocale::init(exePath / "SnowFixer_translations", params.uiLanguage);

    int launcherResult = 0;
    do {
        // Applying the appearance must happen before the window it affects is created (it's a
        // wxApp-level setting, not something that live-updates already-shown windows) - re-applied
        // every loop iteration so a theme change made in the launcher takes effect on relaunch.
        wxApp::Appearance appearance = wxApp::Appearance::System;
        if (params.uiTheme == "light") {
            appearance = wxApp::Appearance::Light;
        } else if (params.uiTheme == "dark") {
            appearance = wxApp::Appearance::Dark;
        }
        if (wxTheApp->SetAppearance(appearance) != wxApp::AppearanceResult::Ok) {
            // Not fatal - the dialog still opens, just without the requested forced appearance.
            startupLog("runGUI: could not apply the requested \"" + params.uiTheme + "\" theme; falling back to system appearance.");
        }
        // SetAppearance() alone only covers a few high-level things (e.g. the title bar) -
        // painting individual controls in dark colors needs wx's separate MSW-specific dark mode
        // support turned on via MSWEnableDarkMode(). Deliberately only called for "dark"
        // specifically - see AutoBlend's own main.cpp for why DarkMode_Auto breaks explicit
        // "Light" whenever the OS itself is dark.
        if (params.uiTheme == "dark") {
            wxTheApp->MSWEnableDarkMode(wxApp::DarkMode_Always);
        }

        auto* launcher = new LauncherWindow(params, exePath); // NOLINT(cppcoreguidelines-owning-memory)
        startupLog("runGUI: LauncherWindow constructed, showing modal");
        launcherResult = launcher->ShowModal();
        if (launcherResult == wxID_OK || launcherResult == LauncherWindow::RESULT_RELAUNCH
            || launcherResult == LauncherWindow::RESULT_RESTART) {
            // Preserve the current (possibly unsaved) field values in memory across the rebuild
            // triggered by a language/theme change, so the relaunched window shows the same values.
            launcher->getParams(params);
        }
        launcher->Destroy();
    } while (launcherResult == LauncherWindow::RESULT_RELAUNCH);

    if (launcherResult == LauncherWindow::RESULT_RESTART) {
        SFConfig::save(params);

        // Respawn a fresh process so wx's MSW dark mode support starts clean with the new theme -
        // see LauncherWindow::onThemeChanged() for why an in-process relaunch isn't reliable here.
        STARTUPINFOW startupInfo {};
        startupInfo.cb = sizeof(startupInfo);
        PROCESS_INFORMATION processInfo {};
        const auto exeFullPath = (exePath / "SnowFixer.exe").wstring();
        const auto workingDir = exePath.wstring();
        if (CreateProcessW(exeFullPath.c_str(), nullptr, nullptr, nullptr, FALSE, 0, nullptr, workingDir.c_str(),
                &startupInfo, &processInfo)
            != 0) {
            CloseHandle(processInfo.hProcess);
            CloseHandle(processInfo.hThread);
        } else {
            const auto errorCode = GetLastError();
            startupLog("runGUI: failed to restart SnowFixer after the theme change (error " + to_string(errorCode) + ")");
            wxMessageBox(wxString::Format("Snow Fixer couldn't restart itself after the theme change (error %lu). "
                                          "Please relaunch it manually.",
                             errorCode),
                "Snow Fixer", wxOK | wxICON_ERROR);
        }

        wxEntryCleanup();
        return 0;
    }

    if (launcherResult != wxID_OK) {
        wxEntryCleanup();
        return 0;
    }

    SFConfig::save(params);

    auto* progress = new ProgressWindow(params, exePath); // NOLINT(cppcoreguidelines-owning-memory)
    progress->ShowModal();
    progress->Destroy();

    wxEntryCleanup();
    return 0;
}
}

auto main(int /*argC*/, char** /*argV*/) -> int
{
    SetConsoleOutputCP(CP_UTF8);
    ExceptionHandler::setMainThread();

    startupLog("=== SnowFixer starting ===");

    const auto exePath = getExecutablePath().parent_path();
    startupLog("main: exePath = " + exePath.string());
    configureDotnetLibDirectory(exePath);

    int returnCode = 0;
    CPPTRACE_TRY { returnCode = runGUI(exePath); }
    CPPTRACE_CATCH(const exception& e)
    {
        startupLog(string("main: caught exception: ") + e.what());
        ExceptionHandler::setException(e, cpptrace::from_current_exception().to_string());
    }

    if (ExceptionHandler::hasException()) {
        ExceptionHandler::throwExceptionOnMainThread();
        returnCode = 1;
    }

    startupLog("main: exiting with code " + to_string(returnCode));

    return returnCode;
}
