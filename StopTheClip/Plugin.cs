using System;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using CSCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using CameraControlMode = FFXIVClientStructs.FFXIV.Client.Game.CameraControlMode;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;

using StopTheClip.Structures;


namespace StopTheClip
{
    public unsafe partial class Plugin : IDalamudPlugin
    {
        [PluginService] public static IDalamudPluginInterface? iPluginInterface { get; private set; } = null;
        [PluginService] public static IFramework? iFramework { get; private set; } = null;
        [PluginService] public static ICommandManager? CommandManager { get; private set; } = null;
        [PluginService] public static IClientState? ClientState { get; private set; } = null;
        [PluginService] public static IObjectTable? ObjectTable { get; private set; } = null;
        [PluginService] public static IPartyList? PartyList { get; private set; } = null;
        [PluginService] public static IChatGui? ChatGui { get; private set; } = null;
        [PluginService] public static IGameGui? GameGui { get; private set; } = null;
        [PluginService] public static ICondition? Condition { get; private set; } = null;
        [PluginService] public static IPluginLog? Log { get; private set; } = null;
        [PluginService] public static IGameInteropProvider Interop { get; private set; } = null!;

        public static Configuration Config { get; private set; } = new Configuration();

        public string Name => "Fresh Set of Eyes";
        private const string CommandName = "/fresheyes";
        private const string LegacyCommandName = "/stoptheclip";

        private TargetSystem* targetSystem = TargetSystem.Instance();
        private const float NearClipOverride = 0.05f;
        private float origNearClip = 0.1f;
        private HookManager hookManager = new HookManager();
        private readonly WindowSystem windowSystem = new("FreshSetOfEyes");
        private PluginUI? mainWindow;

        private bool isEnabled = false;
        private bool inCutscene = false;
        private bool inGpose = false;
        private bool inPvP = false;
        private bool suspended = false;   // enabled, but standing down for combat / PvP

        public string? SuspendReason => !isEnabled || !suspended ? null
            : inPvP && !Config.AllowInPvP ? "Paused in PvP" : "Paused in combat";
        
        public Plugin()
        {
            CommandManager!.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open the Fresh Set of Eyes settings. Add 'enable' or 'disable' to switch it on or off."
            });
            CommandManager!.AddHandler(LegacyCommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Same as /fresheyes.",
                ShowInHelp = false
            });

            ClientState!.Login += OnLogin;
            ClientState!.Logout += OnLogout;
            iFramework!.Update += Update;
            // Keep the settings window available in group pose (Dalamud hides plugin windows there by default)
            iPluginInterface!.UiBuilder.DisableGposeUiHide = true;
            iPluginInterface!.UiBuilder.Draw += DrawUI;
            iPluginInterface!.UiBuilder.OpenConfigUi += ToggleUI;
            iPluginInterface!.UiBuilder.OpenMainUi += ToggleUI;

            Initialize();
            mainWindow = new PluginUI(this, DrawFirstPersonDebug);
            windowSystem.AddWindow(mainWindow);
            Start();
        }

        public void Dispose()
        {
            Stop();
            DisposeFirstPerson();
            hookManager.DisposeFunctionHandles();
            windowSystem.RemoveAllWindows();

            ClientState!.Login -= OnLogin;
            ClientState!.Logout -= OnLogout;
            iFramework!.Update -= Update;
            iPluginInterface!.UiBuilder.Draw -= DrawUI;
            iPluginInterface!.UiBuilder.OpenConfigUi -= ToggleUI;
            iPluginInterface!.UiBuilder.OpenMainUi -= ToggleUI;

            CommandManager!.RemoveHandler(CommandName);
            CommandManager!.RemoveHandler(LegacyCommandName);
        }

        public bool IsEnabled => isEnabled;
        public void ToggleUI() => mainWindow?.Toggle();
        private void OnCommand(string command, string argument)
        {
            if (string.IsNullOrEmpty(argument))
            {
                ToggleUI();
                return;
            }
            var regex = Regex.Match(argument, "^(\\w+) ?(.*)");
            var subcommand = regex.Success && regex.Groups.Count > 1 ? regex.Groups[1].Value : string.Empty;

            switch (subcommand.ToLower())
            {
                case "enable":
                    {
                        Start();
                        break;
                    }
                case "disable":
                    {
                        Stop();
                        break;
                    }
            }

        }

        private void DrawUI()
        {
            windowSystem.Draw();
            DrawFirstPersonMarkers();
        }

        private void OnLogin()
        {
            if (isEnabled && !suspended)
                ClipManagerSetNearClip();

        }
        private void OnLogout(int type, int code)
        {
            //----
            // Sets the lengths of the TargetSystem to 0 as they keep their size
            // even though the data is reset
            //----
            targetSystem->TargetableObjectsOnScreen.Length = 0;
            targetSystem->ObjectFilterArray1.Length = 0;
            targetSystem->ObjectFilterArray2.Length = 0;
            targetSystem->ObjectFilterArray3.Length = 0;

            if (isEnabled && !suspended)
                ClipManagerResetNearClip();
        }

        public static void PrintEcho(string message) => ChatGui!.Print($"[Fresh Set of Eyes] {message}");
        public static void PrintError(string message) => ChatGui!.PrintError($"[Fresh Set of Eyes] {message}");

        private void Update(IFramework framework)
        {
            frameDelta = (float)framework.UpdateDelta.TotalSeconds;
            if (CameraUpdateHook == null)
                TryCreateCameraUpdateHook();

            //----
            // Group pose raises the cutscene flags too, but it's the player's
            // own camera, so it is not treated as a cutscene
            //----
            inGpose = FFXIVClientStructs.FFXIV.Client.Game.GameMain.IsInGPose();
            if (isEnabled)
                inCutscene = !inGpose && (Condition![ConditionFlag.OccupiedInCutSceneEvent] || Condition![ConditionFlag.WatchingCutscene] || Condition![ConditionFlag.WatchingCutscene78]);

            inCombat = Condition![ConditionFlag.InCombat];
            inPvP = ClientState!.IsPvP;

            //----
            // The plugin stands down in combat and in PvP unless the player has
            // opted in (General tab, Experimental): the game's own near clip
            // comes back and nothing is un-hidden or moved
            //----
            bool shouldSuspend = isEnabled && ((inCombat && !Config.AllowInCombat) || (inPvP && !Config.AllowInPvP));
            if (shouldSuspend != suspended)
            {
                suspended = shouldSuspend;
                if (isEnabled)
                {
                    if (suspended)
                        ClipManagerResetNearClip();
                    else
                        ClipManagerSetNearClip();
                }
            }
            UpdateFirstPersonState();
        }

        private static RenderCamera* GetGameRenderCamera()
        {
            //----
            // Only touch the main game camera (index 0), not cutscene/lobby cameras
            //----
            CSCameraManager* cameraManager = CSCameraManager.Instance();
            if (cameraManager == null || cameraManager->ActiveCameraIndex != 0 || cameraManager->Camera == null)
                return null;
            return cameraManager->Camera->CameraBase.SceneCamera.RenderCamera;
        }

        private void ClipManagerSetNearClip()
        {
            //----
            // Set the near clip, remembering the game's value so it can be restored
            //----
            RenderCamera* renderCamera = GetGameRenderCamera();
            if (renderCamera == null)
                return;
            if (renderCamera->NearPlane != NearClipOverride)
                origNearClip = renderCamera->NearPlane;
            renderCamera->NearPlane = NearClipOverride;
        }

        private void ClipManagerResetNearClip()
        {
            //----
            // Restore the game's near clip
            //----
            RenderCamera* renderCamera = GetGameRenderCamera();
            if (renderCamera == null)
                return;
            renderCamera->NearPlane = origNearClip;
        }

        private void Initialize()
        {
            //----
            // Carry settings over from when the plugin was called StopTheClip
            //----
            try
            {
                var configFile = iPluginInterface!.ConfigFile;
                var legacyFile = new System.IO.FileInfo(System.IO.Path.Combine(configFile.DirectoryName!, "StopTheClip.json"));
                if (!configFile.Exists && legacyFile.Exists)
                    legacyFile.CopyTo(configFile.FullName);
            }
            catch (Exception e)
            {
                Log!.Warning(e, "Fresh Set of Eyes: could not carry over StopTheClip settings");
            }
            Config = iPluginInterface!.GetPluginConfig() as Configuration ?? new Configuration();
            Config.Initialize(iPluginInterface!);

            //----
            // TaskManager::ExecuteAllTasks (formerly the RunGameTasks signature),
            // resolved by FFXIVClientStructs so it tracks game patches
            //----
            RunGameTasksHook = Interop.HookFromAddress<RunGameTasksDg>((nint)TaskManager.MemberFunctionPointers.ExecuteAllTasks, RunGameTasksFn);
            InitializeFirstPerson();

            hookManager.SetFunctionHandles(this);
        }

        public void Start()
        {
            hookManager.EnableFunctionHandles();
            if (!suspended)
                ClipManagerSetNearClip();
            isEnabled = true;
        }

        public void Stop()
        {
            isEnabled = false;
            if (!suspended)
                ClipManagerResetNearClip();
            suspended = false;
            hookManager.DisableFunctionHandles();
        }

        public void Toggle()
        {
            if (isEnabled)
                Stop();
            else
                Start();
        }

        //----
        // RunGameTasks
        //----
        private delegate void RunGameTasksDg(TaskManager* taskManager, float* frameTiming);
        private Hook<RunGameTasksDg>? RunGameTasksHook = null;

        [HandleStatus("RunGameTasks")]
        public void RunGameTasksStatus(bool status, bool dispose)
        {
            if (dispose)
                RunGameTasksHook?.Dispose();
            else
            {
                if (status)
                    RunGameTasksHook?.Enable();
                else
                    RunGameTasksHook?.Disable();
            }
        }

        private void RunGameTasksFn(TaskManager* taskManager, float* frameTiming)
        {
            if (!isEnabled || suspended)
            {
                RunGameTasksHook!.Original(taskManager, frameTiming);
                return;
            }

            //----
            // Mirrors the game's loop, but fixes up model culling before task 18
            //----
            taskManager->UserData = frameTiming;
            for (int i = 0; i < taskManager->TaskCount; i++)
            {
                //----
                // The game marks characters the camera is close to as hidden.
                // Undo that before rendering (task 18, as the plugin always
                // has) and also at the start and end of the task loop, so the
                // game's targeting code doesn't see them as hidden either.
                //----
                if (i == 0)
                    CheckVisibilityAt(0);
                if (i == 18)
                {
                    CheckVisibilityAt(1);
                    LatePlaceCamera();
                }

                GameTask* task = (GameTask*)((byte*)taskManager->TaskList + (i * 0x78));
                task->vf1(frameTiming);
            }
            CheckVisibilityAt(2);
            LatePlaceCamera();
        }

        // How many hidden-by-camera characters were made visible at each point
        // in the frame: [0] start of tasks, [1] before task 18, [2] end of tasks
        private readonly long[] visibilityFlips = new long[3];
        private int visibilitySite = 1;
        private void CheckVisibilityAt(int site)
        {
            visibilitySite = site;
            CheckVisibility();
        }


        private void CheckVisibilityInner(Character* character, bool requireTargetable = true)
        {
            if (character == null)
                return;

            if ((ObjectKind)character->GameObject.ObjectKind == ObjectKind.Pc ||
                (ObjectKind)character->GameObject.ObjectKind == ObjectKind.BattleNpc ||
                (ObjectKind)character->GameObject.ObjectKind == ObjectKind.EventNpc ||
                (ObjectKind)character->GameObject.ObjectKind == ObjectKind.Mount ||
                (ObjectKind)character->GameObject.ObjectKind == ObjectKind.Companion ||
                (ObjectKind)character->GameObject.ObjectKind == ObjectKind.Retainer)
            {
                Structures.Model* model = (Structures.Model*)character->GameObject.DrawObject;
                if (model == null)
                    return;

                if (model->CullType == ModelCullTypes.InsideCamera && (!requireTargetable || ((byte)character->GameObject.TargetableStatus & 2) == 2))
                {
                    model->CullType = ModelCullTypes.Visible;
                    visibilityFlips[visibilitySite]++;
                }
                
                DrawDataContainer* drawData = &character->DrawData;

                //----
                // Emote props (books, cups, instruments) live in the third
                // weapon slot. They are shown even when the player has weapon
                // display switched off. The game unloads the prop when the
                // emote ends, so a loaded prop is always one in use.
                //----
                if (drawData != null)
                {
                    ref var prop = ref drawData->Weapon(DrawDataContainer.WeaponSlot.System);
                    Structures.Model* propModel = (Structures.Model*)prop.DrawData.DrawObject;
                    if (propModel != null)
                    {
                        if (drawData->IsWeaponHidden && propModel->CullType == ModelCullTypes.InsideCamera)
                            propModel->CullType = ModelCullTypes.Visible;

                        //----
                        // In first person the game also marks the prop itself as
                        // put away (lowest bit of its state) shortly after the
                        // camera goes in, which hides it whatever its draw flags
                        //----
                        if ((nint)character == localGameObject && firstPersonActive && (prop.State & 1) != 0)
                        {
                            prop.State &= 0xFE;
                            propStateClears++;
                        }
                    }
                }

                if (drawData != null && !drawData->IsWeaponHidden)
                {
                    Structures.Model* mhWeap = (Structures.Model*)drawData->Weapon(DrawDataContainer.WeaponSlot.MainHand).DrawData.DrawObject;
                    if (mhWeap != null)
                        mhWeap->CullType = ModelCullTypes.Visible;

                    Structures.Model* ohWeap = (Structures.Model*)drawData->Weapon(DrawDataContainer.WeaponSlot.OffHand).DrawData.DrawObject;
                    if (ohWeap != null)
                        ohWeap->CullType = ModelCullTypes.Visible;

                    Structures.Model* fWeap = (Structures.Model*)drawData->Weapon(DrawDataContainer.WeaponSlot.System).DrawData.DrawObject;
                    if (fWeap != null)
                        fWeap->CullType = ModelCullTypes.Visible;
                }

                //----
                // The mount comes from the game's own mount container. It used
                // to be read from a fixed offset inside the draw object, which
                // a game patch moved: that offset now holds a skeleton, and
                // writing the flag there corrupted it and crashed the game
                // when the character was unloaded.
                //----
                Character* mountCharacter = character->Mount.MountObject;
                if (mountCharacter != null)
                {
                    Structures.Model* mount = (Structures.Model*)mountCharacter->GameObject.DrawObject;
                    if (mount != null)
                        mount->CullType = ModelCullTypes.Visible;
                }
                
                OrnamentContainer* oCont = &character->OrnamentData;
                if (oCont != null)
                {
                    GameObject* bonedOrnament = (GameObject*)oCont->OrnamentObject;
                    if (bonedOrnament != null)
                    {
                        Structures.Model* ornament = (Structures.Model*)bonedOrnament->DrawObject;
                        if (ornament != null)
                            ornament->CullType = ModelCullTypes.Visible;
                    }
                }
            }
        }
        private const int GposeFirstIndex = 201;
        private const int GposeEndIndex = 249;

        private void CheckVisibility()
        {
            IPlayerCharacter? player = Plugin.ObjectTable!.LocalPlayer;
            if (player == null)
                return;

            if (inCutscene)
                return;

            //----
            // Group pose: the posed characters are copies in their own block
            // of the object table, and aren't flagged as targetable
            //----
            if (inGpose)
            {
                for (int i = GposeFirstIndex; i < GposeEndIndex; i++)
                {
                    nint address = Plugin.ObjectTable!.GetObjectAddress(i);
                    if (address != 0)
                        CheckVisibilityInner((Character*)address, false);
                }
                return;
            }

            //----
            // Check the player
            //----
            Character* character = (Character*)player.Address;
            if (character != null && character != targetSystem->TargetableObjectsOnScreen[0])
                CheckVisibilityInner(character);

            //----
            // Check anyone in your party (used for multi seater mounts)
            //----
            for (int i = 0; i < PartyList!.Length; i++)
            {
                Dalamud.Game.ClientState.Objects.Types.IGameObject? partyMember = PartyList[i]?.GameObject;
                if (partyMember != null)
                {
                    Character* partyCharacter = (Character*)partyMember.Address;
                    if (partyCharacter != null)
                        CheckVisibilityInner(partyCharacter);
                }
            }

            //----
            // Check anyone in sight
            //----
            for (int i = 0; i < targetSystem->ObjectFilterArray1.Length; i++)
                CheckVisibilityInner((Character*)targetSystem->ObjectFilterArray1[i]);
        }
    }
}
