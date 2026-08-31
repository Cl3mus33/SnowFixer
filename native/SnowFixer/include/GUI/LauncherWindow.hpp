#pragma once

#include "SFConfig.hpp"
#include "SFLocale.hpp"
#include "GUI/components/PGModifiableListCtrl.hpp"

#include <wx/wx.h>

#include <filesystem>
#include <vector>

/**
 * @class LauncherWindow
 * @brief The main settings dialog - General tab (Game Location, Output Location, Mod Manager/MO2
 * Instance Path, Landscape Vertex Color mode, Mesh Vertex Color mode, Mesh Blacklist, EditorID
 * Blacklist Keywords) and Options tab (Language, Theme). Trimmed down from AutoBlend's own
 * LauncherWindow: no auto-generate allowlist, no PBR checkbox, no Config Profile load/save - none of
 * that exists in SnowFixer.Core.Configuration.ExtractSettings.
 */
class LauncherWindow : public wxDialog {
public:
    constexpr static int RESULT_RELAUNCH = wxID_HIGHEST + 1;
    constexpr static int RESULT_RESTART = wxID_HIGHEST + 2;

    LauncherWindow(const SFParams& initParams, std::filesystem::path exePath);

    void getParams(SFParams& outParams) const;

private:
    std::filesystem::path m_exePath;
    std::vector<SFLocale::Language> m_languages;

    wxChoice* m_languageChoice = nullptr;
    wxChoice* m_themeChoice = nullptr;
    wxTextCtrl* m_gameLocationTextbox = nullptr;
    wxTextCtrl* m_outputLocationTextbox = nullptr;
    wxChoice* m_modManagerChoice = nullptr;
    wxStaticText* m_mo2InstancePathLabel = nullptr;
    wxTextCtrl* m_mo2InstancePathTextbox = nullptr;
    wxButton* m_mo2InstanceBrowseButton = nullptr;
    wxRadioButton* m_landscapeModeNoneRadio = nullptr;
    wxRadioButton* m_landscapeModeAllRadio = nullptr;
    wxRadioButton* m_landscapeModeSnowRadio = nullptr;
    wxRadioButton* m_meshVertexColorModeNoneRadio = nullptr;
    wxRadioButton* m_meshVertexColorModeSnowOnlyRadio = nullptr;
    wxRadioButton* m_meshVertexColorModeAllRadio = nullptr;
    wxRadioButton* m_collisionMaterialModeNoneRadio = nullptr;
    wxRadioButton* m_collisionMaterialModeSnowOnlyRadio = nullptr;
    PGModifiableListCtrl* m_meshBlacklistCtrl = nullptr;
    PGModifiableListCtrl* m_editorIdKeywordsCtrl = nullptr;
    wxCheckBox* m_generateDirtCliffsSnowVariantCheckbox = nullptr;
    wxButton* m_okButton = nullptr;

    void onLanguageChanged(wxCommandEvent&);
    void onThemeChanged(wxCommandEvent&);
    void onBrowseGameLocation(wxCommandEvent&);
    void onBrowseOutputLocation(wxCommandEvent&);
    void onModManagerChanged(wxCommandEvent&);
    void onBrowseMo2Instance(wxCommandEvent&);
    void updateMo2FieldState();
    void commitPendingListEdits();
    void updateListColumnWidths();
    void onOkButtonPressed(wxCommandEvent&);
};
