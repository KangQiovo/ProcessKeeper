; Pinned NSIS 3.13 embeds the official portable EXE and a read-only native setup guard.
Unicode true
Target x86-unicode
RequestExecutionLevel admin
ManifestSupportedOS all
ManifestDPIAware true
SetCompressor /SOLID zlib
SetDatablockOptimize on
CRCCheck on

!include MUI2.nsh
!include LogicLib.nsh
!include FileFunc.nsh
!include WinVer.nsh
!include x64.nsh
!include Win\COM.nsh

!ifndef PORTABLE_FILE
!error "PORTABLE_FILE is required"
!endif
!ifndef OUTPUT_FILE
!error "OUTPUT_FILE is required"
!endif
!ifndef APP_VERSION
!error "APP_VERSION is required"
!endif
!ifndef PACKAGE_TARGET
!error "PACKAGE_TARGET is required"
!endif
!ifndef PRODUCT_ICON
!error "PRODUCT_ICON is required"
!endif
!ifndef LICENSE_FILE
!error "LICENSE_FILE is required"
!endif
!ifndef SETUP_GUARD_FILE
!error "SETUP_GUARD_FILE is required"
!endif
!ifndef PORTABLE_SHA256
!error "PORTABLE_SHA256 is required"
!endif
!ifndef BUILD_CHANNEL
!define BUILD_CHANNEL "Preview"
!endif
!if "${PACKAGE_TARGET}" != "Windows7Compat"
!if "${PACKAGE_TARGET}" != "Windows10x64"
!if "${PACKAGE_TARGET}" != "Windows10arm64"
!error "Only the three official installed package targets are supported"
!endif
!endif
!endif

!define PRODUCT_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\ProcessKeeper"
!define REPOSITORY "KangQiovo/ProcessKeeper"
Name "Process Keeper ${APP_VERSION}"
OutFile "${OUTPUT_FILE}"
InstallDir "$PROGRAMFILES32\Process Keeper"
BrandingText "Process Keeper | KangQi"
ShowInstDetails show
ShowUninstDetails show
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "Process Keeper"
VIAddVersionKey "CompanyName" "KangQi"
VIAddVersionKey "FileDescription" "Process Keeper Installer | ${PACKAGE_TARGET} | ${BUILD_CHANNEL}"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "ProcessKeeperDistribution" "Installer"
VIAddVersionKey "ProcessKeeperPackageTarget" "${PACKAGE_TARGET}"
VIAddVersionKey "ProcessKeeperRepository" "${REPOSITORY}"
VIAddVersionKey "ProcessKeeperInstallerContract" "1"
VIAddVersionKey "LegalCopyright" "Copyright KangQi | MIT"
!define MUI_ICON "${PRODUCT_ICON}"
!define MUI_UNICON "${PRODUCT_ICON}"
!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE "${LICENSE_FILE}"
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE VerifyDirectory
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "TradChinese"

LangString Unsupported ${LANG_ENGLISH} "This installer is not compatible with this Windows version or architecture. Choose the appropriate package from the official releases."
LangString Unsupported ${LANG_SIMPCHINESE} "此安装包不支持当前 Windows 版本或架构，请从官方发布页选择对应安装包。"
LangString Unsupported ${LANG_TRADCHINESE} "此安裝包不支援目前 Windows 版本或架構，請從官方發佈頁選擇對應安裝包。"
LangString UnsafeDirectory ${LANG_ENGLISH} "Choose a dedicated local folder. Linked paths, drive roots, Windows folders, and folders containing an unrelated ProcessKeeper.exe are refused."
LangString UnsafeDirectory ${LANG_SIMPCHINESE} "请选择独立的本地目录。链接路径、盘符根目录、Windows 目录以及包含其他同名程序的目录不可使用。"
LangString UnsafeDirectory ${LANG_TRADCHINESE} "請選擇獨立的本機目錄。連結路徑、磁碟根目錄、Windows 目錄以及包含其他同名程式的目錄不可使用。"
LangString CloseFirst ${LANG_ENGLISH} "Close Process Keeper and any pending update, then try again. No running program will be forcibly terminated."
LangString CloseFirst ${LANG_SIMPCHINESE} "请先关闭 Process Keeper 并完成或取消待处理更新后重试。安装器不会强制终止程序。"
LangString CloseFirst ${LANG_TRADCHINESE} "請先關閉 Process Keeper 並完成或取消待處理更新後重試。安裝器不會強制終止程式。"
LangString OwnershipChanged ${LANG_ENGLISH} "The installation ownership metadata does not match. Uninstallation was stopped to protect unrelated files."
LangString OwnershipChanged ${LANG_SIMPCHINESE} "安装归属信息不一致，已停止卸载以保护无关文件。"
LangString OwnershipChanged ${LANG_TRADCHINESE} "安裝歸屬資訊不一致，已停止解除安裝以保護無關檔案。"
LangString PreservedData ${LANG_ENGLISH} "Personal profiles, history, startup recovery backups and protected runtime caches are preserved. See the project's removal documentation for optional cleanup of your own user data."
LangString PreservedData ${LANG_SIMPCHINESE} "个人配置、记录、自启动恢复备份和受保护运行缓存将保留。如需清理本用户数据，请参阅项目卸载说明。"
LangString PreservedData ${LANG_TRADCHINESE} "個人設定、記錄、自啟動復原備份與受保護執行快取將保留。如需清理本使用者資料，請參閱專案解除安裝說明。"
LangString InstallFailed ${LANG_ENGLISH} "Installation failed. A rollback was attempted; any retained original files are in the setup backup folder shown in the details. Do not delete it before checking the installation."
LangString InstallFailed ${LANG_SIMPCHINESE} "安装失败，已尝试回滚。原文件如有留存，位于详情显示的安装备份目录；检查安装状态前请勿删除。"
LangString InstallFailed ${LANG_TRADCHINESE} "安裝失敗，已嘗試復原。原檔案如有保留，位於詳細資料顯示的安裝備份目錄；檢查安裝狀態前請勿刪除。"
LangString ShortcutFailed ${LANG_ENGLISH} "The application was installed, but a shortcut could not be created. Launch ProcessKeeper.exe from the installation directory."
LangString ShortcutFailed ${LANG_SIMPCHINESE} "应用已安装，但某个快捷方式未能创建。可从安装目录运行 ProcessKeeper.exe。"
LangString ShortcutFailed ${LANG_TRADCHINESE} "應用已安裝，但某個捷徑未能建立。可從安裝目錄執行 ProcessKeeper.exe。"
LangString VerificationFailed ${LANG_ENGLISH} "Running-session ownership could not be verified safely. Check permissions or damaged session metadata; setup/uninstall has stopped without terminating any process."
LangString VerificationFailed ${LANG_SIMPCHINESE} "无法安全核实运行会话归属。请检查权限或损坏的会话信息；已停止安装或卸载，不会终止任何进程。"
LangString VerificationFailed ${LANG_TRADCHINESE} "無法安全核實執行工作階段歸屬。請檢查權限或損壞的工作階段資訊；已停止安裝或解除安裝，不會終止任何處理程序。"
LangString UninstallFailed ${LANG_ENGLISH} "Some owned files or registration could not be removed. Uninstallation stopped; check permissions and the details before retrying. Personal data and unrelated files were preserved."
LangString UninstallFailed ${LANG_SIMPCHINESE} "部分所属文件或登记未能移除，卸载已停止。请检查权限和详细信息后重试；个人数据及无关文件将保留。"
LangString UninstallFailed ${LANG_TRADCHINESE} "部分所屬檔案或登錄未能移除，解除安裝已停止。請檢查權限與詳細資訊後重試；個人資料與無關檔案將保留。"
LangString UninstallRollback ${LANG_ENGLISH} "Uninstallation failed and restoration was attempted. Any unrestored originals remain in the backup folder shown in the details. Keep that folder until you verify the installation."
LangString UninstallRollback ${LANG_SIMPCHINESE} "卸载失败，已尝试恢复。未恢复的原文件将保留在详情显示的备份目录，请核实安装状态后再处理该目录。"
LangString UninstallRollback ${LANG_TRADCHINESE} "解除安裝失敗，已嘗試復原。未復原的原檔案將保留於詳細資訊顯示的備份目錄，請核實安裝狀態後再處理該目錄。"
LangString ConfirmMigration ${LANG_ENGLISH} "The installed package is $ActualTarget. This setup will change it to ${PACKAGE_TARGET} and replace the application, uninstaller and ownership records together. Cancelling keeps the current copy available. Continue?"
LangString ConfirmMigration ${LANG_SIMPCHINESE} "当前已安装的包为 $ActualTarget。此向导将切换为 ${PACKAGE_TARGET}，并一起更换程序、卸载器和归属登记。取消后当前副本仍可使用。是否继续？"
LangString ConfirmMigration ${LANG_TRADCHINESE} "目前已安裝的套件為 $ActualTarget。此精靈將切換為 ${PACKAGE_TARGET}，並一起更換程式、解除安裝器和歸屬登錄。取消後目前副本仍可使用。是否繼續？"

Var RegisteredDirectory
Var ShortcutPath
Var ShortcutOwned
Var SetupStage
Var PreviousVersion
Var PreviousTarget
Var PreviousPayloadTarget
Var PreviousPayloadTargetPresent
Var ActualTarget
Var MigrationAccepted
Var MovedApp
Var MovedUninstaller
Var MovedMarker
Var NewApp
Var NewUninstaller
Var NewMarker
Var RegistrationChanged
Var SetupMutex
Var StageCreated

!macro SetupGuard Prefix
Function ${Prefix}InitSetupGuard
  System::Call 'kernel32::CreateMutexW(p0,i0,w "Global\ProcessKeeper.Setup")p.r0 ?e'
  Pop $1
  StrCpy $SetupMutex $0
  ${If} $0 P= 0
  ${OrIf} $1 = 183
    MessageBox MB_OK|MB_ICONSTOP "$(CloseFirst)"
    Abort
  ${EndIf}
  InitPluginsDir
  ClearErrors
  File /oname=$PLUGINSDIR\ProcessKeeper.SetupGuard.dll "${SETUP_GUARD_FILE}"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "$(VerificationFailed)"
    Abort
  ${EndIf}
FunctionEnd
Function ${Prefix}RequireStopped
  StrCpy $0 2
  System::Call '$PLUGINSDIR\ProcessKeeper.SetupGuard.dll::CheckInstalledSessionW(w "$INSTDIR\ProcessKeeper.exe")i.r0'
  StrCmp $0 0 stopped
  StrCmp $0 1 active
  MessageBox MB_OK|MB_ICONSTOP "$(VerificationFailed)"
  Abort
active:
  MessageBox MB_OK|MB_ICONSTOP "$(CloseFirst)"
  Abort
stopped:
FunctionEnd
!macroend
!insertmacro SetupGuard ""
!insertmacro SetupGuard "un."

; A fixed metadata contract binds the directory, repository and native uninstaller.
!macro VerifyOwnership Prefix
Function ${Prefix}VerifyOwnership
  ReadRegStr $0 HKLM "${PRODUCT_KEY}" "InstallLocation"
  GetFullPathName $0 "$0"
  StrCmp $0 $INSTDIR 0 mismatch
  ReadRegStr $0 HKLM "${PRODUCT_KEY}" "ProcessKeeperRepository"
  StrCmp $0 "${REPOSITORY}" 0 mismatch
  ReadRegStr $0 HKLM "${PRODUCT_KEY}" "DisplayName"
  StrCmp $0 "Process Keeper" 0 mismatch
  ReadRegStr $0 HKLM "${PRODUCT_KEY}" "Publisher"
  StrCmp $0 "KangQi" 0 mismatch
  ReadRegStr $0 HKLM "${PRODUCT_KEY}" "DisplayIcon"
  StrCmp $0 '"$INSTDIR\ProcessKeeper.exe",0' 0 mismatch
  ReadRegStr $0 HKLM "${PRODUCT_KEY}" "URLInfoAbout"
  StrCmp $0 "https://github.com/${REPOSITORY}" 0 mismatch
  ReadRegStr $0 HKLM "${PRODUCT_KEY}" "ProcessKeeperPackageTarget"
!if "${Prefix}" == ""
  StrCmp $0 "Windows7Compat" ownerTargetKnown
  StrCmp $0 "Windows10x64" ownerTargetKnown
  StrCmp $0 "Windows10arm64" ownerTargetKnown mismatch
ownerTargetKnown:
  StrCpy $PreviousTarget $0
!else
  StrCmp $0 "${PACKAGE_TARGET}" 0 mismatch
!endif
  ReadRegDWORD $0 HKLM "${PRODUCT_KEY}" "ProcessKeeperInstallerContract"
  StrCmp $0 1 0 mismatch
  ReadRegStr $0 HKLM "${PRODUCT_KEY}" "UninstallString"
  StrCmp $0 '"$INSTDIR\Uninstall.exe"' 0 mismatch
  ReadINIStr $0 "$INSTDIR\install.ini" "Installation" "Repository"
  StrCmp $0 "${REPOSITORY}" 0 mismatch
  ReadINIStr $0 "$INSTDIR\install.ini" "Installation" "PackageTarget"
!if "${Prefix}" == ""
  StrCmp $0 $PreviousTarget 0 mismatch
!else
  StrCmp $0 "${PACKAGE_TARGET}" 0 mismatch
!endif
  ReadINIStr $0 "$INSTDIR\install.ini" "Installation" "Contract"
  StrCmp $0 1 0 mismatch
  IfFileExists "$INSTDIR\Uninstall.exe" 0 mismatch
!if "${Prefix}" == ""
  ; Validate the locked original image itself, independently of historical uninstall
  ; ownership metadata retained by an authorized portable package switch.
  StrCpy $0 -1
  System::Call '$PLUGINSDIR\ProcessKeeper.SetupGuard.dll::GetInstalledPackageTargetW(w "$INSTDIR\ProcessKeeper.exe")i.r0'
  StrCmp $0 0 actualUniversal
  StrCmp $0 1 actualCompat
  StrCmp $0 2 actualIntel
  StrCmp $0 3 actualArm mismatch
actualUniversal:
  StrCpy $ActualTarget "Universal"
  Goto identityVerified
actualCompat:
  StrCpy $ActualTarget "Windows7Compat"
  Goto identityVerified
actualIntel:
  StrCpy $ActualTarget "Windows10x64"
  Goto identityVerified
actualArm:
  StrCpy $ActualTarget "Windows10arm64"
identityVerified:
!endif
  Return
mismatch:
  MessageBox MB_OK|MB_ICONSTOP "$(OwnershipChanged)"
  Abort
FunctionEnd
!macroend
!insertmacro VerifyOwnership ""
!insertmacro VerifyOwnership "un."

; Never traverse a junction, symlink or reparse point while writing/removing files.
!macro VerifyPlainDirectory Prefix
Function ${Prefix}VerifyPlainDirectory
  Push $0
  Push $1
  GetFullPathName $INSTDIR "$INSTDIR"
  StrLen $0 $INSTDIR
  IntCmp $0 3 unsafe unsafe 0
  StrCpy $0 $INSTDIR 2
  StrCmp $0 "\\" unsafe
  StrCmp $INSTDIR $WINDIR unsafe
  StrCmp $INSTDIR $SYSDIR unsafe
  StrCmp $INSTDIR $PROGRAMFILES32 unsafe
  StrCmp $INSTDIR $PROGRAMFILES64 unsafe
  StrCpy $0 $INSTDIR
again:
  StrCmp $0 $WINDIR unsafe
  StrCmp $0 $SYSDIR unsafe
  System::Call 'kernel32::GetFileAttributesW(w r0)i.r1'
  IntCmp $1 -1 parent
  IntOp $1 $1 & 0x400
  IntCmp $1 0 parent unsafe unsafe
parent:
  ${GetParent} "$0" $1
  StrCmp $1 "" safe
  StrCmp $1 $0 safe
  StrCpy $0 $1
  Goto again
safe:
  Pop $1
  Pop $0
  Return
unsafe:
  Pop $1
  Pop $0
  MessageBox MB_OK|MB_ICONSTOP "$(UnsafeDirectory)"
  Abort
FunctionEnd
!macroend
!insertmacro VerifyPlainDirectory ""
!insertmacro VerifyPlainDirectory "un."

; Read an exact named shortcut without resolving/launching its target or loading icons.
!macro ShortcutOwnership Prefix
Function ${Prefix}ShortcutOwnership
  Push $0
  Push $1
  Push $2
  Push $3
  Push $4
  StrCpy $ShortcutOwned 0
  IfFileExists "$ShortcutPath" 0 done
  System::Call 'kernel32::GetFileAttributesW(w "$ShortcutPath")i.r0'
  IntOp $0 $0 & 0x400
  IntCmp $0 0 0 done done
  System::Call 'ole32::CoInitializeEx(p0,i2)i.r4'
  !insertmacro ComHlpr_CreateInProcInstance ${CLSID_ShellLink} ${IID_IShellLink} r1 ""
  ${If} $1 P<> 0
    ${IUnknown::QueryInterface} $1 '("${IID_IPersistFile}",.r2)'
    ${If} $2 P<> 0
      ${IPersistFile::Load} $2 '("$ShortcutPath",0).r0'
      ${If} $0 = 0
        ${IShellLink::GetPath} $1 '(.r3,${NSIS_MAX_STRLEN},0,0).r0'
        ${If} $0 = 0
          ${If} $3 == "$INSTDIR\ProcessKeeper.exe"
            ${IShellLink::GetArguments} $1 '(.r3,${NSIS_MAX_STRLEN}).r0'
            ${If} $0 = 0
            ${AndIf} $3 == ""
              StrCpy $ShortcutOwned 1
            ${EndIf}
          ${EndIf}
        ${EndIf}
      ${EndIf}
      ${IUnknown::Release} $2 ""
    ${EndIf}
    ${IUnknown::Release} $1 ""
  ${EndIf}
  ${If} $4 = 0
  ${OrIf} $4 = 1
    System::Call 'ole32::CoUninitialize()'
  ${EndIf}
done:
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
FunctionEnd
!macroend
!insertmacro ShortcutOwnership ""
!insertmacro ShortcutOwnership "un."

Function .onInit
  SetRegView 32
  SetShellVarContext all
  !insertmacro MUI_LANGDLL_DISPLAY
  ${IfNot} ${AtLeastWin7}
    Goto unsupported
  ${EndIf}
  ${If} ${IsWin7}
  ${AndIfNot} ${AtLeastServicePack} 1
    Goto unsupported
  ${EndIf}
  ${If} ${IsWin8}
    Goto unsupported
  ${EndIf}
!if "${PACKAGE_TARGET}" == "Windows10arm64"
  ${IfNot} ${IsNativeARM64}
    Goto unsupported
  ${EndIf}
  ${IfNot} ${AtLeastBuild} 19041
    Goto unsupported
  ${EndIf}
  StrCpy $INSTDIR "$PROGRAMFILES64\Process Keeper"
!else
  ${IfNot} ${IsNativeIA32}
  ${AndIfNot} ${IsNativeAMD64}
    Goto unsupported
  ${EndIf}
!if "${PACKAGE_TARGET}" == "Windows10x64"
  ${IfNot} ${AtLeastWin10}
    Goto unsupported
  ${EndIf}
  ${If} ${RunningX64}
    StrCpy $INSTDIR "$PROGRAMFILES64\Process Keeper"
  ${EndIf}
!endif
!endif
  Call InitSetupGuard
  System::Call 'advapi32::RegOpenKeyExW(p0x80000002,w "${PRODUCT_KEY}",i0,i0x20219,*p.r0)i.r1'
  ${If} $1 = 0
    System::Call 'advapi32::RegCloseKey(p r0)'
    ReadRegStr $RegisteredDirectory HKLM "${PRODUCT_KEY}" "InstallLocation"
    ${If} $RegisteredDirectory == ""
      MessageBox MB_OK|MB_ICONSTOP "$(OwnershipChanged)"
      Abort
    ${EndIf}
  ${ElseIf} $1 != 2
  ${AndIf} $1 != 3
    MessageBox MB_OK|MB_ICONSTOP "$(OwnershipChanged)"
    Abort
  ${EndIf}
  ReadRegStr $RegisteredDirectory HKLM "${PRODUCT_KEY}" "InstallLocation"
  ${If} $RegisteredDirectory != ""
    GetFullPathName $INSTDIR "$RegisteredDirectory"
    Call VerifyPlainDirectory
    Call VerifyOwnership
  ${EndIf}
  Return
unsupported:
  MessageBox MB_OK|MB_ICONSTOP "$(Unsupported)"
  Abort
FunctionEnd

Function VerifyDirectory
  Call VerifyPlainDirectory
  ${If} $RegisteredDirectory != ""
    GetFullPathName $0 "$RegisteredDirectory"
    ${If} $INSTDIR != $0
      MessageBox MB_OK|MB_ICONSTOP "$(OwnershipChanged)"
      Abort
    ${EndIf}
    Call VerifyOwnership
  ${Else}
    IfFileExists "$INSTDIR\ProcessKeeper.exe" unsafe 0
    IfFileExists "$INSTDIR\Uninstall.exe" unsafe 0
    IfFileExists "$INSTDIR\install.ini" unsafe 0
  ${EndIf}
  Return
unsafe:
  MessageBox MB_OK|MB_ICONSTOP "$(UnsafeDirectory)"
  Abort
FunctionEnd

Function ConfirmPackageMigration
  ${If} $RegisteredDirectory != ""
  ${AndIf} $ActualTarget != "${PACKAGE_TARGET}"
  ${AndIf} $MigrationAccepted != "${PACKAGE_TARGET}"
    MessageBox MB_YESNO|MB_ICONEXCLAMATION|MB_DEFBUTTON2 "$(ConfirmMigration)" IDYES migrationConfirmed
    Abort
migrationConfirmed:
    StrCpy $MigrationAccepted "${PACKAGE_TARGET}"
  ${EndIf}
FunctionEnd

Section "Process Keeper" SEC_APP
  Call VerifyDirectory
  Call ConfirmPackageMigration
  Call RequireStopped
  StrCpy $MovedApp 0
  StrCpy $MovedUninstaller 0
  StrCpy $MovedMarker 0
  StrCpy $NewApp 0
  StrCpy $NewUninstaller 0
  StrCpy $NewMarker 0
  StrCpy $RegistrationChanged 0
  StrCpy $StageCreated 0
  ClearErrors
  CreateDirectory "$INSTDIR"
  IfErrors rollback 0
  ReadRegStr $PreviousVersion HKLM "${PRODUCT_KEY}" "DisplayVersion"
  StrCpy $PreviousPayloadTargetPresent 0
  ClearErrors
  ReadRegStr $PreviousPayloadTarget HKLM "${PRODUCT_KEY}" "ProcessKeeperPayloadTarget"
  ${IfNot} ${Errors}
    StrCpy $PreviousPayloadTargetPresent 1
  ${EndIf}
  System::Call 'kernel32::GetCurrentProcessId()i.r0'
  System::Call 'kernel32::GetTickCount()i.r1'
  StrCpy $SetupStage "$INSTDIR\.ProcessKeeper.setup-$0-$1"
  System::Call 'kernel32::CreateDirectoryW(w "$SetupStage",p0)i.r0'
  StrCmp $0 0 stageCollision
  StrCpy $StageCreated 1
  SetOutPath "$SetupStage"
  SetOverwrite on
  ClearErrors
  File /oname=ProcessKeeper.exe "${PORTABLE_FILE}"
  IfErrors rollback 0
  ClearErrors
  WriteUninstaller "$SetupStage\Uninstall.exe"
  IfErrors rollback 0
  ClearErrors
  WriteINIStr "$SetupStage\install.ini" "Installation" "Repository" "${REPOSITORY}"
  IfErrors rollback 0
  ClearErrors
  WriteINIStr "$SetupStage\install.ini" "Installation" "PackageTarget" "${PACKAGE_TARGET}"
  IfErrors rollback 0
  ClearErrors
  WriteINIStr "$SetupStage\install.ini" "Installation" "Contract" "1"
  IfErrors rollback 0
  StrCpy $0 2
  System::Call '$PLUGINSDIR\ProcessKeeper.SetupGuard.dll::CheckPortableFileW(w "$SetupStage\ProcessKeeper.exe",w "${PORTABLE_SHA256}")i.r0'
  StrCmp $0 0 0 rollback
  Call VerifyPlainDirectory
  StrCpy $0 2
  System::Call '$PLUGINSDIR\ProcessKeeper.SetupGuard.dll::CheckInstalledSessionW(w "$INSTDIR\ProcessKeeper.exe")i.r0'
  StrCmp $0 0 0 rollback
  ${If} $RegisteredDirectory != ""
    Call VerifyOwnership
    ClearErrors
    Rename "$INSTDIR\ProcessKeeper.exe" "$SetupStage\previous-app.exe"
    IfErrors rollback 0
    StrCpy $MovedApp 1
    ClearErrors
    Rename "$INSTDIR\Uninstall.exe" "$SetupStage\previous-uninstaller.exe"
    IfErrors rollback 0
    StrCpy $MovedUninstaller 1
    ClearErrors
    Rename "$INSTDIR\install.ini" "$SetupStage\previous-install.ini"
    IfErrors rollback 0
    StrCpy $MovedMarker 1
  ${EndIf}
  ClearErrors
  Rename "$SetupStage\ProcessKeeper.exe" "$INSTDIR\ProcessKeeper.exe"
  IfErrors rollback 0
  StrCpy $NewApp 1
  ClearErrors
  Rename "$SetupStage\Uninstall.exe" "$INSTDIR\Uninstall.exe"
  IfErrors rollback 0
  StrCpy $NewUninstaller 1
  ClearErrors
  Rename "$SetupStage\install.ini" "$INSTDIR\install.ini"
  IfErrors rollback 0
  StrCpy $NewMarker 1
  !macro CheckedWrite Kind Name Value
    ClearErrors
    ${Kind} HKLM "${PRODUCT_KEY}" "${Name}" "${Value}"
    IfErrors rollback 0
  !macroend
  ${If} $RegisteredDirectory == ""
    !insertmacro CheckedWrite WriteRegStr ProcessKeeperRepository "${REPOSITORY}"
    StrCpy $RegistrationChanged 1
    !insertmacro CheckedWrite WriteRegStr ProcessKeeperPackageTarget "${PACKAGE_TARGET}"
    !insertmacro CheckedWrite WriteRegDWORD ProcessKeeperInstallerContract 1
    !insertmacro CheckedWrite WriteRegStr DisplayName "Process Keeper"
    !insertmacro CheckedWrite WriteRegStr Publisher "KangQi"
    !insertmacro CheckedWrite WriteRegStr InstallLocation "$INSTDIR"
    ClearErrors
    WriteRegStr HKLM "${PRODUCT_KEY}" "DisplayIcon" '"$INSTDIR\ProcessKeeper.exe",0'
    IfErrors rollback 0
    ClearErrors
    WriteRegStr HKLM "${PRODUCT_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    IfErrors rollback 0
    !insertmacro CheckedWrite WriteRegStr URLInfoAbout "https://github.com/${REPOSITORY}"
    !insertmacro CheckedWrite WriteRegDWORD NoModify 1
    !insertmacro CheckedWrite WriteRegDWORD NoRepair 1
  ${EndIf}
  StrCpy $RegistrationChanged 1
  !insertmacro CheckedWrite WriteRegStr ProcessKeeperPackageTarget "${PACKAGE_TARGET}"
  !insertmacro CheckedWrite WriteRegStr ProcessKeeperPayloadTarget "${PACKAGE_TARGET}"
  !insertmacro CheckedWrite WriteRegStr DisplayVersion "${APP_VERSION}"
  StrCpy $RegistrationChanged 1
  ; Original backups are deleted only after all exact files/registry commit succeeded.
  Delete "$SetupStage\previous-app.exe"
  Delete "$SetupStage\previous-uninstaller.exe"
  Delete "$SetupStage\previous-install.ini"
  SetOutPath "$INSTDIR"
  RMDir "$SetupStage"
  ClearErrors
  CreateDirectory "$SMPROGRAMS\Process Keeper"
  IfErrors shortcutFailure 0
  StrCpy $ShortcutPath "$SMPROGRAMS\Process Keeper\Process Keeper.lnk"
  Call ShortcutOwnership
  IfFileExists "$ShortcutPath" 0 createAppLink
  StrCmp $ShortcutOwned 1 0 desktopLink
createAppLink:
  ClearErrors
  CreateShortcut "$ShortcutPath" "$INSTDIR\ProcessKeeper.exe"
  IfErrors shortcutFailure 0
desktopLink:
  SetShellVarContext current
  StrCpy $ShortcutPath "$DESKTOP\Process Keeper.lnk"
  Call ShortcutOwnership
  IfFileExists "$ShortcutPath" 0 createDesktopLink
  StrCmp $ShortcutOwned 1 0 complete
createDesktopLink:
  ClearErrors
  CreateShortcut "$ShortcutPath" "$INSTDIR\ProcessKeeper.exe"
  IfErrors shortcutFailure 0
complete:
  Return
shortcutFailure:
  MessageBox MB_OK|MB_ICONEXCLAMATION "$(ShortcutFailed)"
  Return
rollback:
  ${If} $NewApp = 1
    Delete "$INSTDIR\ProcessKeeper.exe"
  ${EndIf}
  ${If} $NewUninstaller = 1
    Delete "$INSTDIR\Uninstall.exe"
  ${EndIf}
  ${If} $NewMarker = 1
    Delete "$INSTDIR\install.ini"
  ${EndIf}
  ${If} $MovedApp = 1
    Rename "$SetupStage\previous-app.exe" "$INSTDIR\ProcessKeeper.exe"
  ${EndIf}
  ${If} $MovedUninstaller = 1
    Rename "$SetupStage\previous-uninstaller.exe" "$INSTDIR\Uninstall.exe"
  ${EndIf}
  ${If} $MovedMarker = 1
    Rename "$SetupStage\previous-install.ini" "$INSTDIR\install.ini"
  ${EndIf}
  ${If} $RegistrationChanged = 1
    ${If} $RegisteredDirectory == ""
      ReadRegStr $0 HKLM "${PRODUCT_KEY}" "ProcessKeeperRepository"
      ${If} $0 == "${REPOSITORY}"
        DeleteRegKey HKLM "${PRODUCT_KEY}"
      ${EndIf}
    ${Else}
      WriteRegStr HKLM "${PRODUCT_KEY}" "ProcessKeeperPackageTarget" "$PreviousTarget"
      ${If} $PreviousPayloadTargetPresent = 1
        WriteRegStr HKLM "${PRODUCT_KEY}" "ProcessKeeperPayloadTarget" "$PreviousPayloadTarget"
      ${Else}
        DeleteRegValue HKLM "${PRODUCT_KEY}" "ProcessKeeperPayloadTarget"
      ${EndIf}
      WriteRegStr HKLM "${PRODUCT_KEY}" "DisplayVersion" "$PreviousVersion"
    ${EndIf}
  ${EndIf}
  ${If} $StageCreated = 1
    Delete "$SetupStage\ProcessKeeper.exe"
    Delete "$SetupStage\Uninstall.exe"
    Delete "$SetupStage\install.ini"
    SetOutPath "$INSTDIR"
    RMDir "$SetupStage"
  ${EndIf}
  DetailPrint "Setup backup | $SetupStage"
  MessageBox MB_OK|MB_ICONSTOP "$(InstallFailed)"
  Abort
stageCollision:
  MessageBox MB_OK|MB_ICONSTOP "$(UnsafeDirectory)"
  Abort
SectionEnd

Function un.onInit
  SetRegView 32
  SetShellVarContext all
  Call un.InitSetupGuard
  Call un.VerifyPlainDirectory
  Call un.VerifyOwnership
  Call un.RequireStopped
FunctionEnd

Section "Uninstall"
  Call un.VerifyPlainDirectory
  Call un.VerifyOwnership
  Call un.RequireStopped
  StrCpy $MovedApp 0
  StrCpy $MovedUninstaller 0
  StrCpy $MovedMarker 0
  System::Call 'kernel32::GetCurrentProcessId()i.r0'
  System::Call 'kernel32::GetTickCount()i.r1'
  StrCpy $SetupStage "$INSTDIR\.ProcessKeeper.uninstall-$0-$1"
  System::Call 'kernel32::CreateDirectoryW(w "$SetupStage",p0)i.r0'
  StrCmp $0 0 stageCollision
  ; All three originals remain recoverable until the owned registration is removed.
  ClearErrors
  Rename "$INSTDIR\ProcessKeeper.exe" "$SetupStage\previous-app.exe"
  IfErrors rollback 0
  StrCpy $MovedApp 1
  ClearErrors
  Rename "$INSTDIR\Uninstall.exe" "$SetupStage\previous-uninstaller.exe"
  IfErrors rollback 0
  StrCpy $MovedUninstaller 1
  ClearErrors
  Rename "$INSTDIR\install.ini" "$SetupStage\previous-install.ini"
  IfErrors rollback 0
  StrCpy $MovedMarker 1
  ClearErrors
  DeleteRegKey HKLM "${PRODUCT_KEY}"
  IfErrors rollback 0
  ; No recursive deletion: a sharing/AV failure retains the exact staged files.
  ClearErrors
  Delete "$SetupStage\previous-app.exe"
  IfErrors failed 0
  ClearErrors
  Delete "$SetupStage\previous-uninstaller.exe"
  IfErrors failed 0
  ClearErrors
  Delete "$SetupStage\previous-install.ini"
  IfErrors failed 0
  RMDir "$SetupStage"
  StrCpy $ShortcutPath "$SMPROGRAMS\Process Keeper\Process Keeper.lnk"
  Call un.ShortcutOwnership
  ${If} $ShortcutOwned = 1
    ClearErrors
    Delete "$ShortcutPath"
    IfErrors failed 0
  ${EndIf}
  RMDir "$SMPROGRAMS\Process Keeper"
  StrCpy $ShortcutPath "$DESKTOP\Process Keeper.lnk"
  Call un.ShortcutOwnership
  ${If} $ShortcutOwned = 1
    ClearErrors
    Delete "$ShortcutPath"
    IfErrors failed 0
  ${EndIf}
  SetShellVarContext current
  StrCpy $ShortcutPath "$DESKTOP\Process Keeper.lnk"
  Call un.ShortcutOwnership
  ${If} $ShortcutOwned = 1
    ClearErrors
    Delete "$ShortcutPath"
    IfErrors failed 0
  ${EndIf}
  SetOutPath "$TEMP"
  RMDir "$INSTDIR"
  DetailPrint "$(PreservedData)"
  Return
rollback:
  ${If} $MovedApp = 1
    Rename "$SetupStage\previous-app.exe" "$INSTDIR\ProcessKeeper.exe"
  ${EndIf}
  ${If} $MovedUninstaller = 1
    Rename "$SetupStage\previous-uninstaller.exe" "$INSTDIR\Uninstall.exe"
  ${EndIf}
  ${If} $MovedMarker = 1
    Rename "$SetupStage\previous-install.ini" "$INSTDIR\install.ini"
  ${EndIf}
  RMDir "$SetupStage"
  DetailPrint "Uninstall backup | $SetupStage"
  MessageBox MB_OK|MB_ICONSTOP "$(UninstallRollback)"
  Abort
failed:
  DetailPrint "Uninstall backup | $SetupStage"
  MessageBox MB_OK|MB_ICONSTOP "$(UninstallFailed)"
  Abort
stageCollision:
  MessageBox MB_OK|MB_ICONSTOP "$(UnsafeDirectory)"
  Abort
SectionEnd
