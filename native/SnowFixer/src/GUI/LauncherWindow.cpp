#include "GUI/LauncherWindow.hpp"

#include "SFLocale.hpp"
#include "GUI/components/PGCustomListctrlChangedEvent.hpp"

#include <wx/notebook.h>
#include <wx/statline.h>

using namespace std;

namespace {
constexpr int BORDER_SIZE = 5;

// Ice-blue palette - distinct from AutoBlend's green, since this app is specifically about snow.
const wxColour ACCENT_DARK(15, 46, 76); // header banner background
const wxColour ACCENT(30, 111, 168); // primary button, section label text
const wxColour ACCENT_TEXT(255, 255, 255); // text on top of ACCENT_DARK/ACCENT

auto makeSectionLabel(wxWindow* parent, const wxString& text) -> wxStaticText*
{
    auto* label = new wxStaticText(parent, wxID_ANY, text);
    wxFont font = label->GetFont();
    font.SetWeight(wxFONTWEIGHT_BOLD);
    label->SetFont(font);
    label->SetForegroundColour(ACCENT);
    return label;
}
}

LauncherWindow::LauncherWindow(const SFParams& initParams, filesystem::path exePath)
    : wxDialog(nullptr, wxID_ANY, "Snow Fixer", wxDefaultPosition, wxSize(760, 620), wxDEFAULT_DIALOG_STYLE | wxRESIZE_BORDER)
    , m_exePath(std::move(exePath))
{
    const wxIcon appIcon(wxICON(IDI_ICON1));
    SetIcon(appIcon);

    auto* mainSizer = new wxBoxSizer(wxVERTICAL);

    // Header banner - same treatment as AutoBlend's own launcher, gives the dialog an identity of
    // its own at a glance.
    auto* headerPanel = new wxPanel(this);
    headerPanel->SetBackgroundColour(ACCENT_DARK);
    auto* headerSizer = new wxBoxSizer(wxHORIZONTAL);
    auto* headerIcon = new wxStaticBitmap(headerPanel, wxID_ANY, wxBitmap(appIcon).ConvertToImage().Scale(32, 32, wxIMAGE_QUALITY_HIGH));
    headerSizer->Add(headerIcon, 0, wxALL | wxALIGN_CENTER_VERTICAL, BORDER_SIZE * 2);
    auto* headerTitle = new wxStaticText(headerPanel, wxID_ANY, "Snow Fixer");
    wxFont headerFont = headerTitle->GetFont();
    headerFont.SetPointSize(headerFont.GetPointSize() + 4);
    headerFont.SetWeight(wxFONTWEIGHT_BOLD);
    headerTitle->SetFont(headerFont);
    headerTitle->SetForegroundColour(ACCENT_TEXT);
    headerSizer->Add(headerTitle, 0, wxALIGN_CENTER_VERTICAL);
    headerPanel->SetSizerAndFit(headerSizer);
    mainSizer->Add(headerPanel, 0, wxEXPAND);

    auto* notebook = new wxNotebook(this, wxID_ANY);

    // "General" tab - the actual per-run settings, same split AutoBlend uses between per-run
    // settings and app-wide preferences.
    auto* generalPanel = new wxPanel(notebook);
    auto* generalSizer = new wxBoxSizer(wxVERTICAL);

    auto* introText = new wxStaticText(generalPanel, wxID_ANY,
        SFTr("launcher.intro",
            "Scans the vanilla Skyrim SE Data folder for every record whose EditorID or mesh path "
            "mentions snow, duplicates the winning mesh for each one, and writes a plugin pointing "
            "at the duplicates - so they stay freely editable without touching any original asset."));
    introText->Wrap(500);
    generalSizer->Add(introText, 0, wxALL, BORDER_SIZE * 2);

    // Game location
    generalSizer->Add(makeSectionLabel(generalPanel, SFTr("launcher.gameLocation.label", "Game Location")), 0,
        wxLEFT | wxRIGHT | wxTOP, BORDER_SIZE);

    m_gameLocationTextbox = new wxTextCtrl(generalPanel, wxID_ANY, initParams.gameLocation);
    auto* gameBrowseButton = new wxButton(generalPanel, wxID_ANY, SFTr("common.browse", "Browse"));
    gameBrowseButton->Bind(wxEVT_BUTTON, &LauncherWindow::onBrowseGameLocation, this);

    auto* gameLocationSizer = new wxBoxSizer(wxHORIZONTAL);
    gameLocationSizer->Add(m_gameLocationTextbox, 1, wxEXPAND | wxALL, BORDER_SIZE);
    gameLocationSizer->Add(gameBrowseButton, 0, wxALL, BORDER_SIZE);
    generalSizer->Add(gameLocationSizer, 0, wxEXPAND);

    // Output location
    generalSizer->Add(makeSectionLabel(generalPanel, SFTr("launcher.outputLocation.label", "Output Location")), 0,
        wxLEFT | wxRIGHT | wxTOP, BORDER_SIZE);

    m_outputLocationTextbox = new wxTextCtrl(generalPanel, wxID_ANY, initParams.outputLocation);
    auto* outputBrowseButton = new wxButton(generalPanel, wxID_ANY, SFTr("common.browse", "Browse"));
    outputBrowseButton->Bind(wxEVT_BUTTON, &LauncherWindow::onBrowseOutputLocation, this);

    auto* outputLocationSizer = new wxBoxSizer(wxHORIZONTAL);
    outputLocationSizer->Add(m_outputLocationTextbox, 1, wxEXPAND | wxALL, BORDER_SIZE);
    outputLocationSizer->Add(outputBrowseButton, 0, wxALL, BORDER_SIZE);
    generalSizer->Add(outputLocationSizer, 0, wxEXPAND);

    // Mod manager - "None / Vortex" covers both: Vortex (in its default hardlink deployment mode)
    // writes mods straight into the real Data folder, so there's no separate virtual filesystem to
    // read, unlike MO2's own USVFS-backed setup.
    generalSizer->Add(makeSectionLabel(generalPanel, SFTr("launcher.modManager.label", "Mod Manager")), 0,
        wxLEFT | wxRIGHT | wxTOP, BORDER_SIZE);

    wxArrayString modManagerChoices;
    modManagerChoices.Add(SFTr("launcher.modManager.none", "None / Vortex"));
    modManagerChoices.Add(SFTr("launcher.modManager.mo2", "Mod Organizer 2"));
    m_modManagerChoice = new wxChoice(generalPanel, wxID_ANY, wxDefaultPosition, wxDefaultSize, modManagerChoices);
    m_modManagerChoice->SetSelection(initParams.modManager == SFModManagerType::ModOrganizer2 ? 1 : 0);
    m_modManagerChoice->Bind(wxEVT_CHOICE, &LauncherWindow::onModManagerChanged, this);
    generalSizer->Add(m_modManagerChoice, 0, wxEXPAND | wxALL, BORDER_SIZE);

    // MO2 instance path - always present (rather than appearing/disappearing with the mod manager
    // choice above), just enabled/disabled - matches AutoBlend's own LauncherWindow, which found
    // that toggling visibility made users miss the field entirely.
    m_mo2InstancePathLabel = makeSectionLabel(generalPanel, SFTr("launcher.mo2InstancePath.label", "MO2 Instance Path"));
    generalSizer->Add(m_mo2InstancePathLabel, 0, wxLEFT | wxRIGHT | wxTOP, BORDER_SIZE);

    m_mo2InstancePathTextbox = new wxTextCtrl(generalPanel, wxID_ANY, initParams.mo2InstancePath);
    m_mo2InstanceBrowseButton = new wxButton(generalPanel, wxID_ANY, SFTr("common.browse", "Browse"));
    m_mo2InstanceBrowseButton->Bind(wxEVT_BUTTON, &LauncherWindow::onBrowseMo2Instance, this);

    auto* mo2InstanceSizer = new wxBoxSizer(wxHORIZONTAL);
    mo2InstanceSizer->Add(m_mo2InstancePathTextbox, 1, wxEXPAND | wxALL, BORDER_SIZE);
    mo2InstanceSizer->Add(m_mo2InstanceBrowseButton, 0, wxALL, BORDER_SIZE);
    generalSizer->Add(mo2InstanceSizer, 0, wxEXPAND);

    updateMo2FieldState();

    // Landscape vertex color mode - entirely separate feature from the mesh duplication above (LAND
    // terrain records have no NIF/Model.File at all), so it's its own section rather than tucked
    // under an unrelated one. Paired side-by-side with Mesh Vertex Colors below to keep the dialog
    // from growing too tall.
    constexpr int HELP_WRAP_PAIRED = 300;

    auto* vertexColorsRowSizer = new wxBoxSizer(wxHORIZONTAL);
    auto* landscapeColumnSizer = new wxBoxSizer(wxVERTICAL);
    auto* meshVertexColumnSizer = new wxBoxSizer(wxVERTICAL);

    landscapeColumnSizer->Add(makeSectionLabel(generalPanel, SFTr("launcher.landscapeVertexColors.label", "Landscape Vertex Colors")), 0,
        wxTOP, BORDER_SIZE);

    auto* landscapeHelpText = new wxStaticText(generalPanel, wxID_ANY,
        SFTr("launcher.landscapeVertexColors.help",
            "Clears baked-in vertex color tinting on terrain (LAND) records, so they render "
            "standalone instead of carrying leftover color grading meant for a different look."));
    landscapeHelpText->Wrap(HELP_WRAP_PAIRED);
    landscapeColumnSizer->Add(landscapeHelpText, 0, wxTOP, BORDER_SIZE);

    m_landscapeModeNoneRadio = new wxRadioButton(generalPanel, wxID_ANY,
        SFTr("launcher.landscapeVertexColors.none", "Don't touch landscape vertex colors"), wxDefaultPosition, wxDefaultSize, wxRB_GROUP);
    m_landscapeModeAllRadio = new wxRadioButton(generalPanel, wxID_ANY,
        SFTr("launcher.landscapeVertexColors.all", "Clear on all terrain"));
    m_landscapeModeSnowRadio = new wxRadioButton(generalPanel, wxID_ANY,
        SFTr("launcher.landscapeVertexColors.snow", "Clear only where a snow-classified texture is used"));

    switch (initParams.landscapeVertexColorMode) {
    case SFLandscapeVertexColorMode::All:
        m_landscapeModeAllRadio->SetValue(true);
        break;
    case SFLandscapeVertexColorMode::SnowOnly:
        m_landscapeModeSnowRadio->SetValue(true);
        break;
    default:
        m_landscapeModeNoneRadio->SetValue(true);
        break;
    }

    landscapeColumnSizer->Add(m_landscapeModeNoneRadio, 0, wxTOP, BORDER_SIZE);
    landscapeColumnSizer->Add(m_landscapeModeAllRadio, 0, wxTOP, BORDER_SIZE);
    landscapeColumnSizer->Add(m_landscapeModeSnowRadio, 0, wxTOP, BORDER_SIZE);

    // Mesh vertex color mode - separate from the LAND-record mode above: this one clears vertex
    // colors on the landscape-folder MESH geometry itself (rocks, cliffs, icebergs, ...), not the
    // terrain records. "All" additionally duplicates non-snow landscape meshes purely to clear their
    // vertex colors too, so terrain and the object meshes sitting on it stay visually consistent
    // once the terrain's own vertex colors are cleared above.
    meshVertexColumnSizer->Add(makeSectionLabel(generalPanel, SFTr("launcher.meshVertexColors.label", "Mesh Vertex Colors")), 0,
        wxTOP, BORDER_SIZE);

    auto* meshVertexColorsHelpText = new wxStaticText(generalPanel, wxID_ANY,
        SFTr("launcher.meshVertexColors.help",
            "Clears baked-in vertex color tinting on landscape-folder meshes (rocks, cliffs, "
            "icebergs, ...), including DLC variants under DLC01\\Landscape\\, DLC02\\Landscape\\, "
            "etc."));
    meshVertexColorsHelpText->Wrap(HELP_WRAP_PAIRED);
    meshVertexColumnSizer->Add(meshVertexColorsHelpText, 0, wxTOP, BORDER_SIZE);

    m_meshVertexColorModeNoneRadio = new wxRadioButton(generalPanel, wxID_ANY,
        SFTr("launcher.meshVertexColors.none", "Don't touch mesh vertex colors"), wxDefaultPosition, wxDefaultSize, wxRB_GROUP);
    m_meshVertexColorModeSnowOnlyRadio = new wxRadioButton(generalPanel, wxID_ANY,
        SFTr("launcher.meshVertexColors.snowOnly", "Only on the generated snow meshes"));
    m_meshVertexColorModeAllRadio = new wxRadioButton(generalPanel, wxID_ANY,
        SFTr("launcher.meshVertexColors.all", "On all landscape meshes, including non-snow originals"));

    switch (initParams.meshVertexColorMode) {
    case SFMeshVertexColorMode::All:
        m_meshVertexColorModeAllRadio->SetValue(true);
        break;
    case SFMeshVertexColorMode::None:
        m_meshVertexColorModeNoneRadio->SetValue(true);
        break;
    default:
        m_meshVertexColorModeSnowOnlyRadio->SetValue(true);
        break;
    }

    meshVertexColumnSizer->Add(m_meshVertexColorModeNoneRadio, 0, wxTOP, BORDER_SIZE);
    meshVertexColumnSizer->Add(m_meshVertexColorModeSnowOnlyRadio, 0, wxTOP, BORDER_SIZE);
    meshVertexColumnSizer->Add(m_meshVertexColorModeAllRadio, 0, wxTOP, BORDER_SIZE);

    vertexColorsRowSizer->Add(landscapeColumnSizer, 1, wxEXPAND | wxLEFT | wxRIGHT, BORDER_SIZE);
    vertexColorsRowSizer->Add(meshVertexColumnSizer, 1, wxEXPAND | wxLEFT | wxRIGHT, BORDER_SIZE);
    generalSizer->Add(vertexColorsRowSizer, 0, wxEXPAND);

    // Collision material mode - remaps collision materials on the generated snow meshes toward
    // their snow equivalent (currently just DIRT/GRASS -> SNOW, kept deliberately conservative) so
    // footstep sounds match the now-snowy visual. No "All" option, unlike the vertex color mode
    // above - duplicating a non-snow mesh purely to fix its collision wasn't judged worth the extra
    // mesh. Off by default since this is a brand new, gameplay-audible capability. Paired side-by-side
    // with the DirtCliffs snow variant below - same space-saving reasoning as the pair above.
    auto* collisionDirtCliffsRowSizer = new wxBoxSizer(wxHORIZONTAL);
    auto* collisionColumnSizer = new wxBoxSizer(wxVERTICAL);
    auto* dirtCliffsColumnSizer = new wxBoxSizer(wxVERTICAL);

    collisionColumnSizer->Add(makeSectionLabel(generalPanel, SFTr("launcher.collisionMaterial.label", "Collision Material (Footstep Sounds)")), 0,
        wxTOP, BORDER_SIZE);

    auto* collisionMaterialHelpText = new wxStaticText(generalPanel, wxID_ANY,
        SFTr("launcher.collisionMaterial.help",
            "Remaps collision materials on the generated snow meshes toward their snow equivalent "
            "(currently just Dirt/Grass -> Snow) so footstep sounds match the snowy visual. Works "
            "per collision chunk, so unrelated materials in the same mesh are left alone."));
    collisionMaterialHelpText->Wrap(HELP_WRAP_PAIRED);
    collisionColumnSizer->Add(collisionMaterialHelpText, 0, wxTOP, BORDER_SIZE);

    m_collisionMaterialModeNoneRadio = new wxRadioButton(generalPanel, wxID_ANY,
        SFTr("launcher.collisionMaterial.none", "Don't touch collision materials"), wxDefaultPosition, wxDefaultSize, wxRB_GROUP);
    m_collisionMaterialModeSnowOnlyRadio = new wxRadioButton(generalPanel, wxID_ANY,
        SFTr("launcher.collisionMaterial.snowOnly", "Apply on the generated snow meshes"));

    switch (initParams.collisionMaterialMode) {
    case SFCollisionMaterialMode::SnowOnly:
        m_collisionMaterialModeSnowOnlyRadio->SetValue(true);
        break;
    default:
        m_collisionMaterialModeNoneRadio->SetValue(true);
        break;
    }

    collisionColumnSizer->Add(m_collisionMaterialModeNoneRadio, 0, wxTOP, BORDER_SIZE);
    collisionColumnSizer->Add(m_collisionMaterialModeSnowOnlyRadio, 0, wxTOP, BORDER_SIZE);

    // DirtCliffsRoots snow variant - one specific, hardcoded texture pair ("landscape\dirtcliffs\
    // dirtcliffsroots01" composited with "landscape\snow01"), not a general texture-generation
    // engine. Off by default.
    dirtCliffsColumnSizer->Add(makeSectionLabel(generalPanel, SFTr("launcher.dirtCliffsSnowVariant.label", "DirtCliffsRoots Snow Variant")), 0,
        wxTOP, BORDER_SIZE);

    m_generateDirtCliffsSnowVariantCheckbox = new wxCheckBox(generalPanel, wxID_ANY,
        SFTr("launcher.dirtCliffsSnowVariant.checkbox", "Generate a snow variant of DirtCliffsRoots01 texture"));
    m_generateDirtCliffsSnowVariantCheckbox->SetValue(initParams.generateDirtCliffsSnowVariant);
    dirtCliffsColumnSizer->Add(m_generateDirtCliffsSnowVariantCheckbox, 0, wxTOP, BORDER_SIZE);

    auto* dirtCliffsSnowVariantHelpText = new wxStaticText(generalPanel, wxID_ANY,
        SFTr("launcher.dirtCliffsSnowVariant.help",
            "Generates the texture file(s) and applies them to the \"Skirt\" part of the "
            "generated DirtCliffs meshes."));
    dirtCliffsSnowVariantHelpText->Wrap(HELP_WRAP_PAIRED);
    dirtCliffsColumnSizer->Add(dirtCliffsSnowVariantHelpText, 0, wxTOP, BORDER_SIZE);

    collisionDirtCliffsRowSizer->Add(collisionColumnSizer, 1, wxEXPAND | wxLEFT | wxRIGHT, BORDER_SIZE);
    collisionDirtCliffsRowSizer->Add(dirtCliffsColumnSizer, 1, wxEXPAND | wxLEFT | wxRIGHT, BORDER_SIZE);
    generalSizer->Add(collisionDirtCliffsRowSizer, 0, wxEXPAND | wxBOTTOM, BORDER_SIZE);

    // Mesh blacklist and EditorID blacklist keywords - inline editable tables, same pattern as
    // AutoBlend's own. Paired side-by-side - same space-saving reasoning as the pairs above.
    auto* blacklistRowSizer = new wxBoxSizer(wxHORIZONTAL);
    auto* meshBlacklistColumnSizer = new wxBoxSizer(wxVERTICAL);
    auto* editorIdKeywordsColumnSizer = new wxBoxSizer(wxVERTICAL);

    meshBlacklistColumnSizer->Add(makeSectionLabel(generalPanel, SFTr("launcher.meshBlacklist.label", "Mesh Blacklist")), 0,
        wxTOP, BORDER_SIZE);

    auto* meshBlacklistHelpText = new wxStaticText(generalPanel, wxID_ANY,
        SFTr("launcher.meshBlacklist.help",
            "Meshes matching a rule here are never duplicated or patched. Wildcards (*) allowed, "
            "e.g. \"*\\trees\\*\". Right click to add/remove rows."));
    meshBlacklistHelpText->Wrap(HELP_WRAP_PAIRED);
    meshBlacklistColumnSizer->Add(meshBlacklistHelpText, 0, wxTOP, BORDER_SIZE);

    m_meshBlacklistCtrl = new PGModifiableListCtrl(
        generalPanel, wxID_ANY, wxDefaultPosition, wxDefaultSize, wxLC_REPORT | wxLC_EDIT_LABELS | wxLC_NO_HEADER);
    m_meshBlacklistCtrl->AppendColumn(SFTr("launcher.meshBlacklist.column", "Rule"), wxLIST_FORMAT_LEFT, wxLIST_AUTOSIZE_USEHEADER);
    m_meshBlacklistCtrl->SetColumnWidth(0, wxLIST_AUTOSIZE_USEHEADER);

    long meshBlacklistIndex = 0;
    for (const auto& rule : initParams.meshBlacklist) {
        m_meshBlacklistCtrl->InsertItem(meshBlacklistIndex++, wxString(rule));
    }
    m_meshBlacklistCtrl->InsertItem(m_meshBlacklistCtrl->GetItemCount(), "");

    meshBlacklistColumnSizer->Add(m_meshBlacklistCtrl, 1, wxEXPAND | wxTOP, BORDER_SIZE);

    // EditorID blacklist keywords - same inline editable table pattern.
    editorIdKeywordsColumnSizer->Add(makeSectionLabel(generalPanel, SFTr("launcher.editorIdKeywords.label", "EditorID Blacklist Keywords")), 0,
        wxTOP, BORDER_SIZE);

    auto* editorIdKeywordsHelpText = new wxStaticText(generalPanel, wxID_ANY,
        SFTr("launcher.editorIdKeywords.help",
            "Records whose EditorID contains one of these words (case-insensitive) are skipped "
            "entirely - useful for excluding false-positive matches. Right click to add/remove rows."));
    editorIdKeywordsHelpText->Wrap(HELP_WRAP_PAIRED);
    editorIdKeywordsColumnSizer->Add(editorIdKeywordsHelpText, 0, wxTOP, BORDER_SIZE);

    m_editorIdKeywordsCtrl = new PGModifiableListCtrl(
        generalPanel, wxID_ANY, wxDefaultPosition, wxDefaultSize, wxLC_REPORT | wxLC_EDIT_LABELS | wxLC_NO_HEADER);
    m_editorIdKeywordsCtrl->AppendColumn(
        SFTr("launcher.editorIdKeywords.column", "Keyword"), wxLIST_FORMAT_LEFT, wxLIST_AUTOSIZE_USEHEADER);
    m_editorIdKeywordsCtrl->SetColumnWidth(0, wxLIST_AUTOSIZE_USEHEADER);

    long editorIdKeywordIndex = 0;
    for (const auto& keyword : initParams.editorIdBlacklistKeywords) {
        m_editorIdKeywordsCtrl->InsertItem(editorIdKeywordIndex++, wxString(keyword));
    }
    m_editorIdKeywordsCtrl->InsertItem(m_editorIdKeywordsCtrl->GetItemCount(), "");

    editorIdKeywordsColumnSizer->Add(m_editorIdKeywordsCtrl, 1, wxEXPAND | wxTOP, BORDER_SIZE);

    blacklistRowSizer->Add(meshBlacklistColumnSizer, 1, wxEXPAND | wxLEFT | wxRIGHT, BORDER_SIZE);
    blacklistRowSizer->Add(editorIdKeywordsColumnSizer, 1, wxEXPAND | wxLEFT | wxRIGHT, BORDER_SIZE);
    generalSizer->Add(blacklistRowSizer, 1, wxEXPAND | wxBOTTOM, BORDER_SIZE);

    generalPanel->SetSizer(generalSizer);
    notebook->AddPage(generalPanel, SFTr("launcher.tab.general", "General"));

    // "Options" tab - app-wide preferences (language, theme) rather than per-run settings, same
    // split AutoBlend uses.
    auto* optionsPanel = new wxPanel(notebook);
    auto* optionsSizer = new wxBoxSizer(wxVERTICAL);

    // Language selector - changing it immediately relaunches the window (see onLanguageChanged)
    // rather than requiring a separate settings dialog/OK click.
    optionsSizer->Add(makeSectionLabel(optionsPanel, SFTr("launcher.language.label", "Language")), 0,
        wxLEFT | wxRIGHT | wxTOP, BORDER_SIZE);

    m_languages = SFLocale::getAvailableLanguages();
    wxArrayString languageChoices;
    int selectedLanguageIndex = 0;
    for (size_t i = 0; i < m_languages.size(); i++) {
        languageChoices.Add(m_languages.at(i).displayName);
        if (m_languages.at(i).code == SFLocale::getCurrentLanguage()) {
            selectedLanguageIndex = static_cast<int>(i);
        }
    }
    m_languageChoice = new wxChoice(optionsPanel, wxID_ANY, wxDefaultPosition, wxDefaultSize, languageChoices);
    if (!m_languages.empty()) {
        m_languageChoice->SetSelection(selectedLanguageIndex);
    }
    m_languageChoice->Bind(wxEVT_CHOICE, &LauncherWindow::onLanguageChanged, this);
    optionsSizer->Add(m_languageChoice, 0, wxEXPAND | wxALL, BORDER_SIZE);

    // Theme selector - same immediate-relaunch pattern as the language selector, since applying a
    // wxWidgets appearance change requires it to be set before the window it affects is created.
    optionsSizer->Add(makeSectionLabel(optionsPanel, SFTr("launcher.theme.label", "Theme")), 0,
        wxLEFT | wxRIGHT | wxTOP, BORDER_SIZE);

    wxArrayString themeChoices;
    themeChoices.Add(SFTr("launcher.theme.system", "System"));
    themeChoices.Add(SFTr("launcher.theme.light", "Light"));
    themeChoices.Add(SFTr("launcher.theme.dark", "Dark"));
    m_themeChoice = new wxChoice(optionsPanel, wxID_ANY, wxDefaultPosition, wxDefaultSize, themeChoices);
    int selectedThemeIndex = 0;
    if (initParams.uiTheme == "light") {
        selectedThemeIndex = 1;
    } else if (initParams.uiTheme == "dark") {
        selectedThemeIndex = 2;
    }
    m_themeChoice->SetSelection(selectedThemeIndex);
    m_themeChoice->Bind(wxEVT_CHOICE, &LauncherWindow::onThemeChanged, this);
    optionsSizer->Add(m_themeChoice, 0, wxEXPAND | wxALL, BORDER_SIZE);

    optionsPanel->SetSizer(optionsSizer);
    notebook->AddPage(optionsPanel, SFTr("launcher.tab.options", "Options"));

    mainSizer->Add(notebook, 1, wxEXPAND | wxALL, BORDER_SIZE);

    Bind(wxEVT_SIZE, [this](wxSizeEvent& event) -> void {
        updateListColumnWidths();
        event.Skip();
    });
    m_meshBlacklistCtrl->Bind(pgEVT_LISTCTRL_CHANGED, [this](PGCustomListctrlChangedEvent& event) -> void {
        updateListColumnWidths();
        event.Skip();
    });
    m_editorIdKeywordsCtrl->Bind(pgEVT_LISTCTRL_CHANGED, [this](PGCustomListctrlChangedEvent& event) -> void {
        updateListColumnWidths();
        event.Skip();
    });

    mainSizer->Add(new wxStaticLine(this, wxID_ANY), 0, wxEXPAND | wxALL, BORDER_SIZE);

    // Buttons
    auto* buttonSizer = new wxBoxSizer(wxHORIZONTAL);
    auto* cancelButton = new wxButton(this, wxID_CANCEL, SFTr("common.cancel", "Cancel"));
    m_okButton = new wxButton(this, wxID_ANY, SFTr("launcher.startButton", "Start"));
    m_okButton->SetBackgroundColour(ACCENT);
    m_okButton->SetForegroundColour(ACCENT_TEXT);
    m_okButton->Bind(wxEVT_BUTTON, &LauncherWindow::onOkButtonPressed, this);
    buttonSizer->AddStretchSpacer();
    buttonSizer->Add(cancelButton, 0, wxALL, BORDER_SIZE);
    buttonSizer->Add(m_okButton, 0, wxALL, BORDER_SIZE);
    mainSizer->Add(buttonSizer, 0, wxEXPAND);

    SetSizerAndFit(mainSizer);
    updateListColumnWidths();
}

void LauncherWindow::getParams(SFParams& outParams) const
{
    outParams.uiLanguage = SFLocale::getCurrentLanguage();
    switch (m_themeChoice->GetSelection()) {
    case 1:
        outParams.uiTheme = "light";
        break;
    case 2:
        outParams.uiTheme = "dark";
        break;
    default:
        outParams.uiTheme = "system";
        break;
    }

    outParams.gameLocation = m_gameLocationTextbox->GetValue().ToStdWstring();
    outParams.outputLocation = m_outputLocationTextbox->GetValue().ToStdWstring();

    outParams.modManager
        = m_modManagerChoice->GetSelection() == 1 ? SFModManagerType::ModOrganizer2 : SFModManagerType::None;
    outParams.mo2InstancePath = m_mo2InstancePathTextbox->GetValue().ToStdWstring();
    // mo2ProfileName is intentionally not editable here - always "Default", matching AutoBlend's
    // own native shell (see SFConfig.hpp's doc comment on the field).

    if (m_meshVertexColorModeAllRadio->GetValue()) {
        outParams.meshVertexColorMode = SFMeshVertexColorMode::All;
    } else if (m_meshVertexColorModeNoneRadio->GetValue()) {
        outParams.meshVertexColorMode = SFMeshVertexColorMode::None;
    } else {
        outParams.meshVertexColorMode = SFMeshVertexColorMode::SnowOnly;
    }

    if (m_collisionMaterialModeSnowOnlyRadio->GetValue()) {
        outParams.collisionMaterialMode = SFCollisionMaterialMode::SnowOnly;
    } else {
        outParams.collisionMaterialMode = SFCollisionMaterialMode::None;
    }

    if (m_landscapeModeAllRadio->GetValue()) {
        outParams.landscapeVertexColorMode = SFLandscapeVertexColorMode::All;
    } else if (m_landscapeModeSnowRadio->GetValue()) {
        outParams.landscapeVertexColorMode = SFLandscapeVertexColorMode::SnowOnly;
    } else {
        outParams.landscapeVertexColorMode = SFLandscapeVertexColorMode::None;
    }

    outParams.meshBlacklist.clear();
    long item = -1;
    while ((item = m_meshBlacklistCtrl->GetNextItem(item)) != -1) {
        const wxString text = m_meshBlacklistCtrl->GetItemText(item);
        if (!text.IsEmpty()) {
            outParams.meshBlacklist.push_back(text.ToStdWstring());
        }
    }

    outParams.editorIdBlacklistKeywords.clear();
    item = -1;
    while ((item = m_editorIdKeywordsCtrl->GetNextItem(item)) != -1) {
        const wxString text = m_editorIdKeywordsCtrl->GetItemText(item);
        if (!text.IsEmpty()) {
            outParams.editorIdBlacklistKeywords.push_back(text.ToStdWstring());
        }
    }

    outParams.generateDirtCliffsSnowVariant = m_generateDirtCliffsSnowVariantCheckbox->GetValue();
}

void LauncherWindow::onLanguageChanged([[maybe_unused]] wxCommandEvent& event)
{
    const int selection = m_languageChoice->GetSelection();
    if (selection == wxNOT_FOUND) {
        return;
    }

    const auto& selectedLang = m_languages.at(static_cast<size_t>(selection));
    if (selectedLang.code == SFLocale::getCurrentLanguage()) {
        return;
    }

    commitPendingListEdits();
    SFLocale::init(m_exePath / "SnowFixer_translations", selectedLang.code);
    EndModal(RESULT_RELAUNCH);
}

void LauncherWindow::onThemeChanged([[maybe_unused]] wxCommandEvent& event)
{
    // Unlike a language change, this needs a full process restart, not just an internal rebuild -
    // wx's MSW dark mode support is a one-way switch once enabled for a process. See main.cpp's
    // handling of RESULT_RESTART.
    commitPendingListEdits();
    EndModal(RESULT_RESTART);
}

void LauncherWindow::onBrowseGameLocation([[maybe_unused]] wxCommandEvent& event)
{
    wxDirDialog dialog(this, SFTr("launcher.gameLocation.dialogTitle", "Select Game Location"), m_gameLocationTextbox->GetValue());
    if (dialog.ShowModal() == wxID_OK) {
        m_gameLocationTextbox->SetValue(dialog.GetPath());
    }
}

void LauncherWindow::onBrowseOutputLocation([[maybe_unused]] wxCommandEvent& event)
{
    wxDirDialog dialog(
        this, SFTr("launcher.outputLocation.dialogTitle", "Select Output Location"), m_outputLocationTextbox->GetValue());
    if (dialog.ShowModal() == wxID_OK) {
        m_outputLocationTextbox->SetValue(dialog.GetPath());
    }
}

void LauncherWindow::onModManagerChanged([[maybe_unused]] wxCommandEvent& event) { updateMo2FieldState(); }

void LauncherWindow::updateMo2FieldState()
{
    const bool isMo2 = m_modManagerChoice->GetSelection() == 1;
    m_mo2InstancePathLabel->Enable(isMo2);
    m_mo2InstancePathTextbox->Enable(isMo2);
    m_mo2InstanceBrowseButton->Enable(isMo2);
}

void LauncherWindow::onBrowseMo2Instance([[maybe_unused]] wxCommandEvent& event)
{
    wxDirDialog dialog(
        this, SFTr("launcher.mo2InstancePath.dialogTitle", "Select MO2 Instance Folder"), m_mo2InstancePathTextbox->GetValue());
    if (dialog.ShowModal() == wxID_OK) {
        m_mo2InstancePathTextbox->SetValue(dialog.GetPath());
    }
}

void LauncherWindow::updateListColumnWidths()
{
    if (m_meshBlacklistCtrl != nullptr && m_meshBlacklistCtrl->GetColumnCount() > 0) {
        m_meshBlacklistCtrl->SetColumnWidth(0, m_meshBlacklistCtrl->GetClientSize().GetWidth());
    }
    if (m_editorIdKeywordsCtrl != nullptr && m_editorIdKeywordsCtrl->GetColumnCount() > 0) {
        m_editorIdKeywordsCtrl->SetColumnWidth(0, m_editorIdKeywordsCtrl->GetClientSize().GetWidth());
    }
}

void LauncherWindow::commitPendingListEdits()
{
    // If the user just typed into one of the modifiable list controls' own trailing blank row
    // (Mesh Blacklist / EditorID Keywords) and then triggered a modal close directly - without
    // pressing Enter or clicking elsewhere first to commit that in-place label edit - wxListCtrl
    // still has the edit control open, and GetItemText() on that row keeps returning the OLD
    // (empty) text until the edit is actually ended. getParams() would then silently drop whatever
    // was just typed, with no error. EndEditLabel(false) commits (does not cancel) any edit in
    // progress on each control; a no-op if that control has no edit open at all. Mirrors AutoBlend's
    // own LauncherWindow.cpp, which added this after a real report of exactly this data loss.
    m_meshBlacklistCtrl->EndEditLabel(false);
    m_editorIdKeywordsCtrl->EndEditLabel(false);
}

void LauncherWindow::onOkButtonPressed([[maybe_unused]] wxCommandEvent& event)
{
    commitPendingListEdits();

    if (m_gameLocationTextbox->GetValue().IsEmpty()) {
        wxMessageBox(SFTr("launcher.missingGameLocation.message", "Please select your game's install location."),
            SFTr("launcher.missingGameLocation.title", "Missing Game Location"), wxOK | wxICON_WARNING, this);
        return;
    }

    if (m_outputLocationTextbox->GetValue().IsEmpty()) {
        wxMessageBox(SFTr("launcher.missingOutputLocation.message", "Please select an output location."),
            SFTr("launcher.missingOutputLocation.title", "Missing Output Location"), wxOK | wxICON_WARNING, this);
        return;
    }

    if (m_modManagerChoice->GetSelection() == 1 && m_mo2InstancePathTextbox->GetValue().IsEmpty()) {
        wxMessageBox(SFTr("launcher.missingMo2Instance.message", "Please select your Mod Organizer 2 instance folder."),
            SFTr("launcher.missingMo2Instance.title", "Missing MO2 Instance"), wxOK | wxICON_WARNING, this);
        return;
    }

    EndModal(wxID_OK);
}
