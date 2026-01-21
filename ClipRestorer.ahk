#Requires AutoHotkey v1.1.35+
;==============================================================
; ClipRestorer — Clipboard auto-restore helper with hotkey callbacks
;
; GitHub: https://github.com/SevenKeyboard/clip-restorer
; Author: SevenKeyboard Ltd. (2025)
; License: MIT License
;==============================================================
class VersionManager_ClipRestorer
{
    static _ := VersionManager_ClipRestorer._init()
    _init()    {
        global
        CLIPRESTORER_VERSION := "1.0.0"
    }
}
class ClipRestorer
{
    static _isPending                   := false
        ,_objbmRestore                  := objBindMethod(ClipRestorer, "restore")
        ,_objbmDummyClipboardCallback   := objBindMethod(ClipRestorer, "_dummyClipboardCallback")
        ,_objbmClipboardChanged         := objBindMethod(ClipRestorer, "_clipboardChanged")
        ,_objbmHotIfCondCtrlV           := objBindMethod(ClipRestorer, "_hotIfCondCtrlV")
        ,_objbmCtrlVPressed             := objBindMethod(ClipRestorer, "_ctrlVPressed")
        ,_objbmAppsKeyPressed           := objBindMethod(ClipRestorer, "_appsKeyPressed")
        ,_objbmOnApplicationQuit        := objBindMethod(ClipRestorer, "_onApplicationQuit")
    ;----------------------------
    IsPending    {
        get  {
            return (!!this._isPending)
        }
    }
    restore(timeout := 1.5)    {
        critical % format("{2}", prevIC:=A_IsCritical, "On")
        if (this.hasKey("_timeout"))    {
            timeout := this._timeout
            this.delete("_timeout")
        }
        result := false
        this.scheduleRestore(false)
        if (this.waitAvailability(0.5))    {
            Clipboard := ""
            if (this.hasBackup())    {
                this.loadBackup()
                clipWait % timeout, true
                result := !ErrorLevel
            }
        }
        this.deleteBackup()
        critical % prevIC
        return result
    }
    scheduleRestore(onoff := true, period := 1200, timeout := 1.5)    {
        critical % format("{2}", prevIC:=A_IsCritical, "On")
        this._isPending := onoff
        this._timeout := timeout
        this._setPasteKeyCallback(onoff)
        this._setClipboardChangeCallback(onoff)
        onExit(this._objbmOnApplicationQuit, onoff?1:0)
        objbmRestore := this._objbmRestore
        setTimer % objbmRestore, % (onoff?-abs(period):"Delete")
        critical % prevIC
    }
    _onApplicationQuit(exitReason, exitCode)    {
        switch (exitReason)
        {
            case "Logoff","Shutdown":
            default:
                try  {
                    if (this.hasBackup())
                        this.loadBackup()
                }  finally  {
                    this.deleteBackup()
                }
        }
    }
    ;----------------------------
    createBackup()    {
        global
        clipRestorerClipboardBackup_EA52BF2B := ClipboardAll
    }
    loadBackup()    {
        global
        Clipboard := clipRestorerClipboardBackup_EA52BF2B
    }
    deleteBackup()    {
        global
        clipRestorerClipboardBackup_EA52BF2B := ""
    }
    hasBackup()    {
        global
        return (isSet(clipRestorerClipboardBackup_EA52BF2B) && clipRestorerClipboardBackup_EA52BF2B !== "")
    }
    ;----------------------------
    blockClipboardChangedCallbacks(onoff := true)    {
        onClipboardChange(this._objbmDummyClipboardCallback, onoff?-1:0)
    }
    _dummyClipboardCallback(_*)    {
        return true
    }
    ;----------------------------
    ;  #If ClipRestorer._isPasteKeyCallbackEnabled
    ;  ^v::ClipRestorer._ctrlVPressed()
    ;  AppsKey::ClipRestorer._appsKeyPressed()
    ;  #If   
    _setPasteKeyCallback(onoff)    {
        this._isPasteKeyCallbackEnabled := onoff
        hotkey If, % this._objbmHotIfCondCtrlV
        objbm := this._objbmCtrlVPressed
        hotkey % "^v", % objbm, % "UseErrorLevel" (onoff?" On":" Off")
        objbm := this._objbmAppsKeyPressed
        hotkey % "*AppsKey", % objbm, % "UseErrorLevel" (onoff?" On":" Off")
        hotkey If
    }
    _hotIfCondCtrlV()    {
        return this._isPasteKeyCallbackEnabled
    }
    _ctrlVPressed()    { ;  While waiting for the timer to finish, if you press Ctrl + V.
        suspend Permit
        this.scheduleRestore(false)
        if (this.restore())
            sendInput % "{LControl down}{vk56sc02F}{LControl up}"
    }
    _appsKeyPressed()    {
        suspend Permit
        this.scheduleRestore(false)
        if (this.restore())
            sendInput % "{Blind}{AppsKey}"
    }
    ;----------------------------
    _setClipboardChangeCallback(onoff)    {
        onClipboardChange(this._objbmClipboardChanged, onoff?-1:0)
    }
    _clipboardChanged(dataType)    { ;  While waiting for the timer, if there is a change in the clipboard.
        this.scheduleRestore(false)
        this.deleteBackup()
        return true
    }
    ;----------------------------
    waitAvailability(timeout := 0)    {
        endtick := timeout ? this._TickCount + timeout * 1000 : 0
        loop    {
            if (this._open())    {
                this._close()
                return true
            }  else if (endtick && endtick < this._TickCount)    {
                return false
            }
            sleep 50
        }        
        ;  while (dllCall("User32.dll\GetOpenClipboardWindow", "Ptr"))
        ;      sleep 50
    }
    _open(hWndNewOwner := 0)    {
        return dllCall("User32.dll\OpenClipboard", "Ptr",hWndNewOwner)
    }
    _close()    {
        return dllCall("User32.dll\CloseClipboard")
    }
    _TickCount    {
        get  {
            return A_Is64bitOS
                ? dllCall("Kernel32.dll\GetTickCount64", "Int64") & 0x7FFFFFFFFFFFFFFF
                : dllCall("Kernel32.dll\GetTickCount", "UInt")
        }
    }
}