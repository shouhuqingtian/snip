Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

!define APP_NAME "自用截图"
!define APP_VERSION "2.0.1"
!define APP_EXE "Snip.exe"
!define APP_KEY "Software\SelfSnip"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\SelfSnip"

Name "${APP_NAME}"
OutFile "..\artifacts\installer\SelfSnip-${APP_VERSION}-Setup-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\SelfSnip"
InstallDirRegKey HKCU "${APP_KEY}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
ManifestDPIAware true
VIProductVersion "2.0.1.0"
VIAddVersionKey /LANG=2052 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=2052 "FileDescription" "${APP_NAME}安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "自用截图"

!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "启动自用截图"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"

!macro CheckRunning Prefix
Function ${Prefix}CheckRunning
  retry:
  System::Call 'kernel32::OpenMutexW(i 0x00100000, i 0, w "Local\SelfSnip.Instance.v1") p .r0'
  ${If} $0 P<> 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "自用截图正在运行。请先通过托盘菜单退出，再点击重试。" /SD IDCANCEL IDRETRY retry
    Abort
  ${EndIf}
FunctionEnd
!macroend
!insertmacro CheckRunning ""
!insertmacro CheckRunning "un."

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "此版本需要 Windows 10/11 x64。" /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "此版本需要 Windows 10 22H2 或 Windows 11。" /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastBuild} 19045
    MessageBox MB_OK|MB_ICONSTOP "请更新到 Windows 10 22H2 或 Windows 11 后安装。" /SD IDOK
    SetErrorLevel 1
    Quit
  ${EndIf}
  SetRegView 64
  SetShellVarContext current
  Call CheckRunning
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext current
  Call un.CheckRunning
FunctionEnd

Section "主程序（必选）" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"
  File "..\artifacts\portable\win-x64\${APP_EXE}"
  File "..\docs\使用说明.md"
  File "..\THIRD-PARTY-NOTICES.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\卸载.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${APP_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "自用截图"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
SectionEnd

Section /o "桌面快捷方式" SecDesktop
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMain} "安装主程序、使用说明以及开始菜单入口。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDesktop} "为当前用户创建桌面快捷方式。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  Delete "$INSTDIR\${APP_EXE}"
  Delete "$INSTDIR\使用说明.md"
  Delete "$INSTDIR\THIRD-PARTY-NOTICES.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\卸载.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  DeleteRegKey HKCU "${APP_KEY}"
SectionEnd
