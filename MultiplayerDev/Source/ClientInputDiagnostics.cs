using System;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis;
using Nivalis.Dialogue;
using Nivalis.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using PlayerInputManager = Nivalis.PlayerInputManager;

namespace NivalisTogether
{
    /// <summary>Read-only diagnostics. Call once per loaded-client Update; never sample menus or the worker.</summary>
    public static class ClientInputDiagnostics
    {
        private static double nextReport, previousSample, longestGap;
        private static int samples, focusedSamples, keysSeen;
        private static bool sampled;
        public static void Reset()
        {
            nextReport=previousSample=longestGap=0; samples=focusedSamples=keysSeen=0; sampled=false;
        }
        /// <summary>Samples only W/S/I/Escape. Logs immediately, then once per ten seconds. Does not release locks.</summary>
        public static void Tick(double now, Action<string> log, bool force=false)
        {
            if(log==null || !double.IsFinite(now))return;
            samples++;
            if(sampled)longestGap=Math.Max(longestGap,Math.Max(0,now-previousSample));
            previousSample=now;sampled=true;
            try {
                if(Application.isFocused)focusedSamples++;
                var keyboard=Keyboard.current;
                if(keyboard!=null) {
                    if(keyboard.wKey.isPressed || keyboard.wKey.wasPressedThisFrame)keysSeen|=1;
                    if(keyboard.sKey.isPressed || keyboard.sKey.wasPressedThisFrame)keysSeen|=2;
                    if(keyboard.iKey.isPressed || keyboard.iKey.wasPressedThisFrame)keysSeen|=4;
                    if(keyboard.escapeKey.isPressed || keyboard.escapeKey.wasPressedThisFrame)keysSeen|=8;
                }
            } catch { /* The bounded report below records unavailable native state. */ }
            if(!force && now<nextReport)return;
            nextReport=now+10;
            var text=new StringBuilder("CLIENT_INPUT samples=").Append(samples).Append(" focusedSamples=").Append(focusedSamples)
                .Append(" sampledKeys[W,S,I,Esc]=").Append((keysSeen&1)!=0).Append(',').Append((keysSeen&2)!=0).Append(',')
                .Append((keysSeen&4)!=0).Append(',').Append((keysSeen&8)!=0).Append(" maxSampleGap=").Append(Number(longestGap));
            Add(text,"application",()=>"focused="+Application.isFocused+",timeScale="+Number(Time.timeScale)+",keyboard="+(Keyboard.current!=null));
            Add(text,"keyboards",KeyboardDevices);
            Add(text,"inputUser",InputUserDevices);
            Add(text,"photoMode",()=>{
                var value=Nivalis.PhotoMode.PhotoModeManager.InstanceNotNull()?Nivalis.PhotoMode.PhotoModeManager.Instance:null;
                return value==null?"missing":"opened="+value.opened+",capturing="+value._Capturing_k__BackingField;
            });
            Add(text,"cursor",()=>{
                var value=CursorModeManager._instance;if(value==null)return "missing";
                return "ui="+value._uiLock+",application="+value._applicationLock+",blocksMovement="+value.disableMovementWhileInPointerMode+
                    ",moveLock="+Lock(value._playerMoveLock)+",cameraLock="+Lock(value._playerCameraLock);
            });
            Add(text,"movement",()=>{
                var character=PlayerManager.InstanceNotNull()?PlayerManager.Instance._localPlayer?._character:null;
                if(character==null)return "missingCharacter";
                var value=character._controller;if(value==null)return "missingController";
                return "initialized="+character._isInitialized+",characterEnabled="+character.enabled+",controllerEnabled="+value.enabled+
                    ",nativeControllerEnabled="+(value._characterController!=null&&value._characterController.enabled)+
                    ",state="+value._state+",speedScale="+Number(value._moveSpeedScale)+",defaultSpeed="+Number(value.defaultMoveSpeed)+
                    ",canMove="+Owners(value._canMove)+",cameraLook="+Owners(value._cameraLookEnabled)+
                    ",movementLock="+Lock(value.movementLock)+",spotLock="+Lock(character.spotMovementLock);
            });
            Add(text,"input",()=>{
                var value=PlayerInputManager.InstanceNotNull()?PlayerInputManager.Instance:null;if(value==null)return "missing";
                var map=value.currentMap;
                var gameplay=value.GameplayControls;
                return "map="+(map==null?"missing":Cut(map.m_Name,80)+",enabledActions="+map.m_EnabledActionsCount)+
                    ",gameplayMap="+(gameplay==null?"missing":Cut(gameplay.m_Name,80)+",enabledActions="+gameplay.m_EnabledActionsCount)+",rebinding="+value.isRebind+
                    ",allMenus="+Owners(value.PreventAllMenuShortcuts)+",gameplayMenus="+Owners(value.PreventGameplayMenuShortcuts)+
                    ",saving="+Owners(value.PreventSaving);
            });
            Add(text,"panels",()=>{
                var manager=UIManager.InstanceNotNull()?UIManager.Instance:null;if(manager==null)return "missing";
                var panels=manager._openPanels;var result=new StringBuilder("count=").Append(panels?.Count??0).Append('[');
                for(int i=0;i<Math.Min(panels?.Count??0,12);i++) {
                    if(i>0)result.Append(';');var panel=panels[i];
                    if(panel==null){result.Append("null");continue;}
                    result.Append(Identity(panel)).Append(",visible=").Append(panel._isVisible).Append(",mouse=").Append(panel.requiresMouse)
                        .Append(",pause=").Append(panel.pauseTimeWhenOpen).Append(",completePause=").Append(panel.pauseTimeCompletely);
                }
                return result.Append(']').ToString();
            });
            Add(text,"dialogue",()=>{
                var value=DialogueUI._instance;if(value==null)return "missing";
                return "open="+value._isOpen+",visible="+value._isVisible+",phone="+value._isInCallDialogue+
                    ",continueButton="+(value.fullScreenButton==null?"missing":value.fullScreenButton.interactable+",active="+value.fullScreenButton.gameObject.activeInHierarchy)+
                    ",menuLock="+Lock(value._preventMenuShortcutsLock)+",saveLock="+Lock(value._preventSavingLock);
            });
            Add(text,"loading",()=>{
                var value=GameSceneManager.InstanceNotNull()?GameSceneManager.Instance:null;
                var loading=LoadingScreenUI._instance;
                return "screenShowing="+LoadingScreenUI.showing+",screenAlpha="+(loading?.grp==null?"missing":Number(loading.grp.alpha))+
                    ",screenRoutine="+(loading?.routine!=null)+",game="+(value==null?"missing":"levelLoading="+value.levelLoading+
                    ",external="+value._IsLoadingExternal_k__BackingField+",blocked="+value.LevelLoadingIsBlocked+
                    ",unloading="+GameSceneManager._IsUnloadingGameplay_k__BackingField);
            });
            Add(text,"dayTransition",()=>{
                var value=DayTransitionController.InstanceNotNull()?DayTransitionController.Instance:null;if(value==null)return "missing";
                return "pause="+Lock(value._pauseLock)+",move="+Lock(value._movementLock)+",camera="+Lock(value._cameraLock)+
                    ",interaction="+Lock(value._interactionLock);
            });
            Add(text,"sleep",()=>{
                var value=SleepManager.InstanceNotNull()?SleepManager.Instance:null;if(value==null)return "missing";
                return "pause="+Lock(value._timePauseLock)+",move="+Lock(value._movementLock)+",look="+Lock(value._playerLookLock)+
                    ",camera="+Lock(value._cameraLock)+",interaction="+Lock(value._playerInteractionLock)+",menus="+Lock(value._preventOpeningMenuLock);
            });
            Add(text,"clock",()=>Owners(TimeOfDayManager._instance?._isPaused));
            samples=focusedSamples=keysSeen=0;longestGap=0;
            try{log(text.ToString());}catch{}
        }
        private static void Add(StringBuilder text,string name,Func<string> read)
        {
            text.Append(" | ").Append(name).Append('=');
            try{text.Append(read());}catch(Exception ex){text.Append("unavailable:").Append(ex.GetType().Name);}
        }
        private static string Device(InputDevice value)
        {
            if(value==null)return "missing";
            return "id="+value.deviceId+",enabled="+value.enabled+",native="+value.native+
                ",type="+Cut(value.GetIl2CppType()?.FullName,100)+",name="+Cut(value.displayName,64)+",path="+Cut(value.path,96);
        }
        private static string KeyboardDevices()
        {
            var current=Keyboard.current;
            var manager=InputSystem.s_Manager;
            if(manager==null)return "current{"+Device(current)+"},manager=missing";
            // Read the backing reference array instead of the generated ReadOnlyArray value-return API.
            var devices=manager.m_Devices;int count=manager.m_DevicesCount;
            if(count<0 || count>(devices?.Length??0))return "invalidDeviceBounds";
            int scanned=Math.Min(count,64),keyboards=0,shown=0;
            var result=new StringBuilder("current{").Append(Device(current)).Append("},devices=").Append(count).Append(",list=[");
            for(int i=0;i<scanned;i++) {
                var keyboard=devices[i]?.TryCast<Keyboard>();if(keyboard==null)continue;
                keyboards++;
                if(shown>=8)continue;
                if(shown++>0)result.Append(';');
                result.Append('{').Append(Device(keyboard)).Append(",current=").Append(current!=null&&keyboard.Pointer==current.Pointer).Append('}');
            }
            result.Append("],keyboardCount=").Append(keyboards).Append(",scanned=").Append(scanned)
                .Append(",truncated=").Append(scanned<count||keyboards>shown).Append(",allDevices=[");
            for(int i=0;i<Math.Min(count,8);i++) {
                if(i>0)result.Append(';');result.Append('{').Append(Device(devices[i])).Append('}');
            }
            return result.Append("],allDevicesTruncated=").Append(count>8).ToString();
        }
        private static string InputUserDevices()
        {
            var manager=PlayerInputManager.InstanceNotNull()?PlayerInputManager.Instance:null;if(manager==null)return "missing";
            // InputUser is a blittable uint ID. The raw path uses primitive/reference fields.
            // The independent pairedDevices path returns a normally boxed (non-Nullable)
            // ReadOnlyArray; its native Count getter explicitly unboxes that object correctly.
            var user=manager.user;
            if(!user.valid)return "id="+user.m_Id+",valid=False";
            int index=user.index;
            var state=UnityEngine.InputSystem.Users.InputUser.s_GlobalState;
            var users=state.allUserData;
            if(index<0 || index>=state.allUserCount || index>=(users?.Length??0))return "invalidUserBounds";
            var data=users[index];if(data==null)return "missingUserData";
            var devices=state.allPairedDevices;int start=data.deviceStartIndex,count=data.deviceCount;
            if(start<0 || count<0 || start>state.allPairedDeviceCount || count>state.allPairedDeviceCount-start ||
                start>(devices?.Length??0) || count>(devices?.Length??0)-start)return "invalidPairBounds";
            var current=Keyboard.current;
            string publicCount;
            try { publicCount=user.pairedDevices.Count.ToString(CultureInfo.InvariantCulture); }
            catch(Exception ex) { publicCount="unavailable:"+ex.GetType().Name; }
            var result=new StringBuilder("id=").Append(user.m_Id).Append(",valid=True,index=").Append(index)
                .Append(",pairedCount=").Append(count).Append(",publicPairedCount=").Append(publicCount)
                .Append(",pairingStateVersion=").Append(state.pairingStateVersion)
                .Append(",allPairedDeviceCount=").Append(state.allPairedDeviceCount).Append(",devices=[");
            for(int i=0;i<Math.Min(count,8);i++) {
                if(i>0)result.Append(';');var device=devices[start+i];
                result.Append('{').Append(Device(device)).Append(",currentKeyboard=")
                    .Append(device!=null&&current!=null&&device.Pointer==current.Pointer).Append('}');
            }
            return result.Append("],truncated=").Append(count>8).Append(",scheme=notSampledNullableApi").ToString();
        }
        private static string Lock(OverrideableBool.OverrideLock value) => ReferenceEquals(value,null)?"none":
            "released="+value._isReleased+",owner="+Identity(value._owner);
        private static string Owners(OverrideableBool value)
        {
            if(ReferenceEquals(value,null))return "missing";
            var owners=value._overrideOwners;int count=owners?.Count??0;
            var text=new StringBuilder("value=").Append(value.Value).Append(",default=").Append(value._defaultValue).Append(",owners=").Append(count).Append('[');
            if(count>0) {
                int take=Math.Min(count,8);var copy=new Il2CppReferenceArray<Il2CppSystem.Object>(take);owners.CopyTo(copy,0,take);
                for(int i=0;i<take;i++){if(i>0)text.Append(';');text.Append(Identity(copy[i]));}
            }
            return text.Append(']').ToString();
        }
        private static string Identity(Il2CppSystem.Object value)
        {
            if(ReferenceEquals(value,null))return "null";
            string type=Cut(value.GetIl2CppType()?.FullName??"unknown",120);
            var dummy=value.TryCast<LockDummy>();
            if(dummy!=null)type+="{"+Cut(dummy._information,160)+"}";
            var unity=value.TryCast<UnityEngine.Object>();
            return type+(unity==null?"":"("+Cut(unity.name,64)+")");
        }
        private static string Cut(string value,int max) {value=(value??"").Replace('\r',' ').Replace('\n',' ');return value.Length<=max?value:value.Substring(0,max);}
        private static string Number(double value) => value.ToString("F3",CultureInfo.InvariantCulture);
    }
}
