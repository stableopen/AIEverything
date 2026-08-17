Unicode True
SetCompressor /SOLID lzma

!include "MUI2.nsh"

!ifndef PRODUCT_ROOT
  !error "PRODUCT_ROOT is required"
!endif
!ifndef PRODUCT_DIST
  !error "PRODUCT_DIST is required"
!endif

!define PRODUCT_NAME "AIEverything"
!define PRODUCT_VERSION "1.0.0"
!define PRODUCT_PUBLISHER "AIEverything"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AIEverything"

Name "${PRODUCT_NAME} ${PRODUCT_VERSION}"
OutFile "${PRODUCT_ROOT}\dist\AIEverything-Setup-${PRODUCT_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\AIEverything"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
BrandingText "AIEverything - Local filename and content search"
Icon "${PRODUCT_ROOT}\src\AIEverything.App\Assets\AIEverything.ico"
UninstallIcon "${PRODUCT_ROOT}\src\AIEverything.App\Assets\AIEverything.ico"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\AIEverything.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Launch AIEverything"
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

Section "AIEverything" SEC_MAIN
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=stop-daemon.ps1 "${PRODUCT_ROOT}\scripts\stop-installed-daemon.ps1"
  nsExec::ExecToLog '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$PLUGINSDIR\stop-daemon.ps1" -InstallDirectory "$INSTDIR"'

  SetOutPath "$INSTDIR"
  File /r "${PRODUCT_DIST}\*.*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\AIEverything"
  CreateShortcut "$SMPROGRAMS\AIEverything\AIEverything.lnk" "$INSTDIR\AIEverything.exe"
  CreateShortcut "$DESKTOP\AIEverything.lnk" "$INSTDIR\AIEverything.exe"

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${PRODUCT_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${PRODUCT_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${PRODUCT_PUBLISHER}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\AIEverything.exe"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  nsExec::ExecToLog '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\tools\stop-daemon.ps1" -InstallDirectory "$INSTDIR"'
  Delete "$DESKTOP\AIEverything.lnk"
  Delete "$SMPROGRAMS\AIEverything\AIEverything.lnk"
  RMDir "$SMPROGRAMS\AIEverything"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  RMDir /r "$INSTDIR"
SectionEnd
