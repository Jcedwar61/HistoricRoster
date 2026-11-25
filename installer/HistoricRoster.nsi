; Historic Roster installer (NSIS 3). Build with build.ps1, which publishes the program first.
; Per-user install (no administrator rights): the program keeps its race database and catalogs
; next to itself, so it must live in a folder the user can write to.

Unicode true
!include "MUI2.nsh"
!include "StrFunc.nsh"
${StrStr}
${UnStrStr}

!ifndef VERSION
  !define VERSION "1.1.0"
!endif
!ifndef SOURCE
  !define SOURCE "..\dist\HistoricRoster"
!endif
!define APPNAME "Historic Roster"
!define EXE "HistoricRoster.exe"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\HistoricRoster"

Name "${APPNAME}"
OutFile "..\dist\HistoricRosterSetup.exe"
InstallDir "$LOCALAPPDATA\Programs\${APPNAME}"
InstallDirRegKey HKCU "${UNINST_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
BrandingText "${APPNAME} ${VERSION}"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} installer"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"

!define MUI_ICON "..\App\app.ico"
!define MUI_UNICON "..\App\app.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TEXT "This installs ${APPNAME} ${VERSION} for your Windows user.$\r$\n$\r$\nRecreate real races in Assetto Corsa: real grids, real cars, real tracks.$\r$\n$\r$\nNo administrator rights are needed."
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APPNAME}"
!define MUI_FINISHPAGE_SHOWREADME "$INSTDIR\USER_GUIDE.md"
!define MUI_FINISHPAGE_SHOWREADME_TEXT "Open the user guide"
!define MUI_FINISHPAGE_SHOWREADME_NOTCHECKED

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; Asks to close a running copy (its files can't be replaced or removed while it runs).
Function WaitForClosed
  retry:
    nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${EXE}" /NH'
    Pop $0
    Pop $1
    ${StrStr} $2 $1 "${EXE}"
    StrCmp $2 "" done
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${APPNAME} is running. Close it, then click Retry." /SD IDCANCEL IDRETRY retry
    Abort
  done:
FunctionEnd

Function un.WaitForClosed
  retry:
    nsExec::ExecToStack 'tasklist /FI "IMAGENAME eq ${EXE}" /NH'
    Pop $0
    Pop $1
    ${UnStrStr} $2 $1 "${EXE}"
    StrCmp $2 "" done
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${APPNAME} is running. Close it, then click Retry." /SD IDCANCEL IDRETRY retry
    Abort
  done:
FunctionEnd

Section "Install"
  Call WaitForClosed
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File "${SOURCE}\${EXE}"
  File "${SOURCE}\*.dll"
  File "${SOURCE}\USER_GUIDE.md"

  ; The in-game (CSP) app, installed into Assetto Corsa by the program when you watch an AI race.
  RMDir /r "$INSTDIR\CspApp"
  SetOutPath "$INSTDIR\CspApp\HistoricRoster"
  File /r "${SOURCE}\CspApp\HistoricRoster\*"
  SetOutPath "$INSTDIR"

  ; Race database and catalog: only replaced by newer ones, so a database you updated in the
  ; program is kept. Your own catalogs (scan results, track choices) are never touched.
  SetOutPath "$INSTDIR\rosters"
  SetOverwrite ifnewer
  File "${SOURCE}\rosters\historic_rosters_db.json"
  File "${SOURCE}\rosters\content_catalog.json"
  SetOverwrite on

  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXE}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME} user guide.lnk" "$INSTDIR\USER_GUIDE.md"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Uninstall ${APPNAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXE}"

  ; Settings > Apps > Installed apps
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "Historic Roster"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${EXE}"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  ; EstimatedSize in KB
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" 200000
SectionEnd

Section "Uninstall"
  Call un.WaitForClosed
  Delete "$INSTDIR\${EXE}"
  Delete "$INSTDIR\*.dll"
  Delete "$INSTDIR\*.pdb"
  Delete "$INSTDIR\USER_GUIDE.md"
  RMDir /r "$INSTDIR\CspApp"
  Delete "$INSTDIR\Uninstall.exe"

  Delete "$DESKTOP\${APPNAME}.lnk"
  RMDir /r "$SMPROGRAMS\${APPNAME}"
  DeleteRegKey HKCU "${UNINST_KEY}"

  ; Your data: kept unless you say otherwise (a silent uninstall keeps it).
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Also delete your Historic Roster data?$\r$\n$\r$\nThat is the race database, your track choices and car scan results, your settings and your seasons. Keep it to pick up where you left off after a reinstall." /SD IDNO IDNO keep
    RMDir /r "$INSTDIR\rosters"
    RMDir /r "$APPDATA\HistoricRoster"
  keep:
  RMDir "$INSTDIR"
SectionEnd
