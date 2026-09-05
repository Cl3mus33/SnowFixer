#include "GUI/ProgressWindow.hpp"

#include "SFLocale.hpp"
#include "util/StringUtil.hpp"

#include <SnowFixer.NativeExportNE.h>
#include <nlohmann/json.hpp>

#include <chrono>
#include <sstream>

using namespace std;

namespace {
// nlohmann::json::value(key, default) only substitutes the default for a MISSING key - a key
// that's present but JSON null (as System.Text.Json writes a null `string?` C# property, e.g.
// ProgressSnapshot.ErrorMessage before a run has failed) still throws type_error.302 when asked to
// convert to string. This covers both cases.
auto readOptionalString(const nlohmann::json& json, const char* key) -> string
{
    if (!json.contains(key) || json[key].is_null()) {
        return {};
    }
    return json[key].get<string>();
}

constexpr int BORDER_SIZE = 5;
constexpr int GAUGE_WIDTH = 320;
constexpr int GAUGE_HEIGHT = 18;
constexpr int PULSE_INTERVAL_MS = 100;
constexpr int POLL_INTERVAL_MS = 150;
constexpr int WRAP_WIDTH = 340;

// Matches LauncherWindow's own accent colors, so the two windows read as one identity.
const wxColour ACCENT(30, 111, 168);
const wxColour ACCENT_TEXT(255, 255, 255);

// SnowFixer.NativeExportNE.h declares every [UnmanagedCallersOnly] export as taking/
// returning intptr_t (Exports.cs never applies a [DNNE.C99Type] override, so DNNE falls back to
// IntPtr's natural C mapping) - these two helpers are the only place that needs to know that.
auto toIntPtr(const string& utf8) -> intptr_t { return reinterpret_cast<intptr_t>(utf8.c_str()); }
auto fromIntPtr(intptr_t ptr) -> string
{
    return ptr == 0 ? string {} : string(reinterpret_cast<const char*>(ptr));
}
}

ProgressWindow::ProgressWindow(const SFParams& params, const filesystem::path& /*exePath*/)
    : wxDialog(nullptr, wxID_ANY, "Snow Fixer", wxDefaultPosition, wxSize(420, 170), wxDEFAULT_DIALOG_STYLE | wxRESIZE_BORDER)
    , m_pulseTimer(this)
    , m_outputLocation(params.outputLocation)
{
    const wxIcon appIcon(wxICON(IDI_ICON1));
    SetIcon(appIcon);

    auto* mainSizer = new wxBoxSizer(wxVERTICAL);

    m_statusText = new wxStaticText(this, wxID_ANY, SFTr("progress.starting", "Starting..."));
    m_statusText->Wrap(WRAP_WIDTH);
    mainSizer->Add(m_statusText, 0, wxEXPAND | wxALL, BORDER_SIZE * 2);

    m_progressGauge = new wxGauge(
        this, wxID_ANY, 100, wxDefaultPosition, wxSize(GAUGE_WIDTH, GAUGE_HEIGHT), wxGA_SMOOTH);
    mainSizer->Add(m_progressGauge, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, BORDER_SIZE * 2);

    m_detailsPane = new wxCollapsiblePane(this, wxID_ANY, SFTr("progress.showDetails", "Show details"));
    auto* paneWindow = m_detailsPane->GetPane();
    auto* paneSizer = new wxBoxSizer(wxVERTICAL);

    m_logCtrl = new wxTextCtrl(paneWindow, wxID_ANY, "", wxDefaultPosition, wxSize(-1, 260),
        wxTE_MULTILINE | wxTE_READONLY | wxTE_RICH2);
    wxFont fixedFont(10, wxFONTFAMILY_TELETYPE, wxFONTSTYLE_NORMAL, wxFONTWEIGHT_NORMAL);
    m_logCtrl->SetFont(fixedFont);
    m_logCtrl->SetBackgroundColour(wxColour(24, 24, 24));
    m_logCtrl->SetForegroundColour(wxColour(220, 220, 220));
    m_logCtrl->SetDefaultStyle(wxTextAttr(wxColour(220, 220, 220), wxColour(24, 24, 24)));
    paneSizer->Add(m_logCtrl, 1, wxEXPAND);
    paneWindow->SetSizer(paneSizer);

    mainSizer->Add(m_detailsPane, 0, wxEXPAND | wxLEFT | wxRIGHT | wxBOTTOM, BORDER_SIZE * 2);
    m_detailsPane->Bind(wxEVT_COLLAPSIBLEPANE_CHANGED, &ProgressWindow::onDetailsPaneChanged, this);

    m_closeButton = new wxButton(this, wxID_ANY, SFTr("progress.working", "Working..."));
    m_closeButton->SetBackgroundColour(ACCENT);
    m_closeButton->SetForegroundColour(ACCENT_TEXT);
    m_closeButton->Enable(false);
    m_closeButton->Bind(wxEVT_BUTTON, &ProgressWindow::onCloseButtonPressed, this);

    auto* buttonSizer = new wxBoxSizer(wxHORIZONTAL);
    buttonSizer->AddStretchSpacer();
    buttonSizer->Add(m_closeButton, 0, wxALL, BORDER_SIZE);
    mainSizer->Add(buttonSizer, 0, wxEXPAND);

    SetSizerAndFit(mainSizer);
    Centre();

    // Closing the window (X button) while the run is still in flight would tear this object down
    // out from under the poll thread - refuse it until the run has finished.
    Bind(wxEVT_CLOSE_WINDOW, [this](wxCloseEvent& event) -> void {
        if (!m_closeButton->IsEnabled()) {
            event.Veto();
            return;
        }
        EndModal(wxID_OK);
    });

    Bind(wxEVT_TIMER, [this](wxTimerEvent&) -> void { m_progressGauge->Pulse(); });
    m_pulseTimer.Start(PULSE_INTERVAL_MS);

    const auto settingsJson = SFConfig::toJson(params).dump();
    start_extract_run(toIntPtr(settingsJson));

    m_pollThread = thread([this]() -> void {
        string lastStatus;
        bool success = true;
        wxString failureDetail;
        wxString failureDetails;
        string resultJson;

        while (!m_stopRequested.load()) {
            const intptr_t rawPtr = get_progress();
            const auto json = fromIntPtr(rawPtr);
            if (rawPtr != 0) {
                free_string(rawPtr);
            }

            try {
                const auto snapshot = nlohmann::json::parse(json);
                const auto status = snapshot.value("Status", string {});
                const auto current = snapshot.value("Current", 0);
                const auto total = snapshot.value("Total", 0);
                const auto isIndeterminate = snapshot.value("IsIndeterminate", true);
                const auto isDone = snapshot.value("IsDone", false);
                const auto isFailed = snapshot.value("IsFailed", false);
                const auto errorMessage = readOptionalString(snapshot, "ErrorMessage");
                const auto errorDetails = readOptionalString(snapshot, "ErrorDetails");

                if (status != lastStatus && !status.empty()) {
                    lastStatus = status;
                    const auto wxStatus = wxString::FromUTF8(status);
                    wxTheApp->CallAfter([this, wxStatus]() -> void { appendLog(wxStatus + "\n"); });
                }

                wxTheApp->CallAfter([this, wxStatus = wxString::FromUTF8(status), current, total, isIndeterminate]() -> void {
                    applySnapshot(wxStatus, current, total, isIndeterminate);
                });

                if (isDone) {
                    success = !isFailed;
                    failureDetail = wxString::FromUTF8(errorMessage);
                    failureDetails = wxString::FromUTF8(errorDetails);
                    resultJson = readOptionalString(snapshot, "ResultJson");
                    break;
                }
            } catch (const exception& e) {
                // A transient parse failure (e.g. the very first poll racing StartExtractRun's own
                // state initialization) isn't fatal - just try again next tick.
                wxTheApp->CallAfter([this, message = string(e.what())]() -> void {
                    appendLog("\n[WARN] Failed to parse progress snapshot: " + wxString::FromUTF8(message) + "\n");
                });
            }

            this_thread::sleep_for(chrono::milliseconds(POLL_INTERVAL_MS));
        }

        wxTheApp->CallAfter([this, success, failureDetail, failureDetails, resultJson]() -> void {
            onWorkerFinished(success, failureDetail, failureDetails, resultJson);
        });
    });
}

ProgressWindow::~ProgressWindow()
{
    m_stopRequested.store(true);
    if (m_pollThread.joinable()) {
        m_pollThread.join();
    }
}

void ProgressWindow::appendLog(const wxString& text) { m_logCtrl->AppendText(text); }

void ProgressWindow::applySnapshot(const wxString& status, int current, int total, bool isIndeterminate)
{
    if (!status.IsEmpty()) {
        m_statusText->SetLabel(status);
        m_statusText->Wrap(WRAP_WIDTH);
        // Wrap() can change how many lines the label needs - without re-running the sizer, the
        // gauge below keeps its old position and visually overlaps the now-taller text.
        Layout();
        GetSizer()->Fit(this);
    }

    if (isIndeterminate || total <= 0) {
        if (!m_pulseTimer.IsRunning()) {
            m_pulseTimer.Start(PULSE_INTERVAL_MS);
        }
        return;
    }

    m_pulseTimer.Stop();
    const int pct = clamp(static_cast<int>((static_cast<double>(current) / total) * 100.0), 0, 100);
    m_progressGauge->SetValue(pct);
}

void ProgressWindow::onWorkerFinished(
    bool success, const wxString& failureDetail, const wxString& failureDetails, const string& resultJson)
{
    m_pulseTimer.Stop();
    m_progressGauge->SetValue(100);
    m_statusText->SetLabel(success ? SFTr("progress.done", "Done.") : SFTr("progress.failed", "Failed - see details below."));
    m_statusText->Wrap(WRAP_WIDTH);
    m_closeButton->SetLabel(success ? SFTr("progress.doneClose", "Done - Close") : SFTr("progress.failedClose", "Failed - Close"));
    m_closeButton->Enable(true);

    // SnowFixer.Core.Pipeline.ExtractOrchestrator already writes SnowFixer-log.txt
    // itself as part of Run() (unlike AutoBlend, whose native shell writes AutoBlend-log.txt) - this
    // just mirrors the same summary into this window's own in-app log, it doesn't write the file.
    auto diagnosticCount = 0;
    if (!resultJson.empty()) {
        try {
            const auto result = nlohmann::json::parse(resultJson);
            ostringstream summary;
            summary << "\n=== Result ===\n";
            summary << "Records matched: " << result.value("RecordsMatched", 0) << "\n";
            summary << "Meshes duplicated: " << result.value("MeshesDuplicated", 0) << "\n";
            summary << "Meshes failed to resolve: " << result.value("MeshesFailed", 0) << "\n";
            summary << "Malformed records skipped: " << result.value("MalformedRecordsSkipped", 0) << "\n";
            summary << "Alternate Textures baked: " << result.value("AlternateTexturesBaked", 0) << "\n";
            summary << "Alternate Textures that couldn't be baked: " << result.value("AlternateTexturesFailed", 0) << "\n";
            summary << "Meshes with ZBuffer_Write/No_Fade shader flag fixups: " << result.value("ShaderFlagsPatched", 0) << "\n";
            summary << "Meshes with vertex colors neutralized: " << result.value("VertexColorsNeutralized", 0) << "\n";
            summary << "Landscape records with vertex colors cleared: " << result.value("LandscapesPatched", 0) << "\n";
            const auto outputEspPath = readOptionalString(result, "OutputEspPath");
            summary << (outputEspPath.empty() ? "No plugin written.\n" : "Plugin written: " + outputEspPath + "\n");

            if (result.contains("Diagnostics") && result["Diagnostics"].is_array() && !result["Diagnostics"].empty()) {
                diagnosticCount = static_cast<int>(result["Diagnostics"].size());
                summary << "\n" << diagnosticCount << " diagnostic(s):\n";
                for (const auto& diagnostic : result["Diagnostics"]) {
                    summary << " - " << diagnostic.get<string>() << "\n";
                }
            }

            appendLog(wxString::FromUTF8(summary.str()));
        } catch (const exception& e) {
            appendLog("\n[WARN] Failed to parse run result: " + wxString::FromUTF8(e.what()) + "\n");
        }
    }

    if (!success || diagnosticCount > 0) {
        if (!failureDetail.IsEmpty()) {
            appendLog("\n[ERROR] " + failureDetail + "\n");
        }
        if (!failureDetails.IsEmpty() && failureDetails != failureDetail) {
            appendLog("\n[ERROR DETAILS]\n" + failureDetails + "\n");
        }
        if (!m_detailsPane->IsExpanded()) {
            m_detailsPane->Expand();
        }
        Layout();
        GetSizer()->Fit(this);
        if (!success) {
            return;
        }
    }

    wxMessageBox(
        SFTr("progress.completion.message",
            "Generation complete. Don't forget to enable Snow Fixer's output plugin in your mod manager's "
            "plugin list if it isn't already active."),
        SFTr("progress.completion.title", "Snow Fixer"), wxOK | wxICON_INFORMATION, this);
    // Dismissing this message box means the same thing as clicking "Done - Close" - just close.
    EndModal(wxID_OK);
}

void ProgressWindow::onCloseButtonPressed([[maybe_unused]] wxCommandEvent& event) { EndModal(wxID_OK); }

void ProgressWindow::onDetailsPaneChanged([[maybe_unused]] wxCollapsiblePaneEvent& event)
{
    Layout();
    GetSizer()->Fit(this);
}
