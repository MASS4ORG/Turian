Unicode true
!include "WinMessages.nsh"
!include "StrFunc.nsh"
${StrStr}
${UnStrRep}

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
    MessageBox MB_YESNO|MB_ICONEXCLAMATION "Turian needs the .NET 10 SDK, which was not found.$\r$\n$\r$\nOpen the download page now? Installation continues either way." IDNO sdkFound
    ExecShell "open" "https://dotnet.microsoft.com/download/dotnet/10.0"
  sdkFound:
FunctionEnd

Section "Turian" SecMain
  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}/*"
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
  Call AddToUserPath
SectionEnd

; Appends $INSTDIR to the user PATH so turian-cli and turian-studio work from any terminal. NSIS strings are
; capped at NSIS_MAX_STRLEN, so a PATH that long is left untouched rather than written back truncated.
Function AddToUserPath
  ReadRegStr $0 HKCU "Environment" "Path"
  ${StrStr} $1 ";$0;" ";$INSTDIR;"
  StrCmp $1 "" 0 done
  StrLen $2 "$0;$INSTDIR"
  IntCmp $2 ${NSIS_MAX_STRLEN} tooLong 0 tooLong
  StrCmp $0 "" 0 append
    WriteRegExpandStr HKCU "Environment" "Path" "$INSTDIR"
    Goto notify
  append:
    WriteRegExpandStr HKCU "Environment" "Path" "$0;$INSTDIR"
  notify:
    SendMessage ${HWND_BROADCAST} ${WM_SETTINGCHANGE} 0 "STR:Environment" /TIMEOUT=5000
    Goto done
  tooLong:
    DetailPrint "PATH is too long to update safely; add $INSTDIR to it manually."
  done:
FunctionEnd

Function un.RemoveFromUserPath
  ReadRegStr $0 HKCU "Environment" "Path"
  StrCmp $0 "" done
  StrLen $2 $0
  IntOp $2 $2 + 2
  IntCmp $2 ${NSIS_MAX_STRLEN} done 0 done
  ${UnStrRep} $1 ";$0;" ";$INSTDIR;" ";"
  StrCpy $1 $1 "" 1
  StrCpy $1 $1 -1
  StrCmp $1 $0 done
  StrCmp $1 "" 0 write
    DeleteRegValue HKCU "Environment" "Path"
    Goto notify
  write:
    WriteRegExpandStr HKCU "Environment" "Path" $1
  notify:
    SendMessage ${HWND_BROADCAST} ${WM_SETTINGCHANGE} 0 "STR:Environment" /TIMEOUT=5000
  done:
FunctionEnd

Section "Uninstall"
  Call un.RemoveFromUserPath
  Delete "$SMPROGRAMS\Turian\Turian Studio.lnk"
  Delete "$SMPROGRAMS\Turian\Uninstall.lnk"
  RMDir "$SMPROGRAMS\Turian"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\Turian"
  RMDir /r "$INSTDIR"
SectionEnd
