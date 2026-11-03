Unicode true
!include "WinMessages.nsh"

Name "Turian"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\Turian"
RequestExecutionLevel user

Page directory
Page instfiles
UninstPage uninstConfirm
UninstPage instfiles

; Studio and the CLI are framework-dependent and compile user projects, so they need the .NET 10 SDK.
Function .onInit
  FindFirst $0 $1 "$PROGRAMFILES64\dotnet\sdk\10.*"
  FindClose $0
  StrCmp $1 "" 0 sdkFound
    MessageBox MB_YESNO|MB_ICONEXCLAMATION "Turian needs the .NET 10 SDK, which was not found.$\r$\n$\r$\nOpen the download page now? Installation continues either way." /SD IDNO IDNO sdkFound
    ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/10.0"
  sdkFound:
FunctionEnd

Section "Turian" SecMain
  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Turian"
  CreateShortcut "$SMPROGRAMS\Turian\Turian Studio.lnk" "$INSTDIR\turian-studio.exe"
  CreateShortcut "$SMPROGRAMS\Turian\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Turian" "DisplayName" "Turian"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Turian" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Turian" "Publisher" "MASS4"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Turian" "DisplayIcon" "$INSTDIR\turian-studio.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Turian" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Turian" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Turian" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Turian" "NoRepair" 1
  StrCpy $0 "add"
  Call UpdateUserPath
SectionEnd

; Adds ($0 = "add") or removes ($0 = "remove") $INSTDIR in the user PATH through user-path.ps1, so turian-cli and
; turian-studio work from any terminal. NSIS strings are capped at 1024 characters and would truncate a long PATH.
; Warns instead of failing when PowerShell errors.
!macro UPDATE_USER_PATH PREFIX
Function ${PREFIX}UpdateUserPath
  InitPluginsDir
  File "/oname=$PLUGINSDIR\user-path.ps1" "user-path.ps1"
  nsExec::ExecToLog '"$SYSDIR\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$PLUGINSDIR\user-path.ps1" $0 "$INSTDIR"'
  Pop $1
  StrCmp $1 "0" UpdateUserPathBroadcast
  MessageBox MB_OK|MB_ICONEXCLAMATION "Could not update the user PATH ($1). Please $0 $INSTDIR manually." /SD IDOK
  Return
UpdateUserPathBroadcast:
  SendMessage ${HWND_BROADCAST} ${WM_SETTINGCHANGE} 0 "STR:Environment" /TIMEOUT=5000
FunctionEnd
!macroend

!insertmacro UPDATE_USER_PATH ""
!insertmacro UPDATE_USER_PATH "un."

Section "Uninstall"
  StrCpy $0 "remove"
  Call un.UpdateUserPath
  Delete "$SMPROGRAMS\Turian\Turian Studio.lnk"
  Delete "$SMPROGRAMS\Turian\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Turian"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Turian"
  RMDir /r "$INSTDIR"
SectionEnd
