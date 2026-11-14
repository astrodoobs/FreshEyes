using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using StopTheClip.Structures;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using CSCameraManager = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using CameraControlMode = FFXIVClientStructs.FFXIV.Client.Game.CameraControlMode;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using CharacterBase = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase;
using RenderSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;
using PartialSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.PartialSkeleton;

namespace StopTheClip
{
    //----
    // True first person
    //
    // Pose buffers: every frame Skeleton::FinishAnimation copies each partial
    // skeleton's animated LOCAL pose from havok pose slot 0 into slot 1 (and 2
    // into 3) and marks slot 1's model pose dirty. Slot 1 is what gets drawn.
    //
    // 1. After Skeleton::UpdateAnimations we read the local player's head, neck
    //    and eye positions from slot 0 (eyes live in the face partial skeleton,
    //    which is attached to the head bone at render time, so we apply that
    //    attachment ourselves).
    // 2. The head is hidden by shrinking the head bones in slot 1's local pose.
    //    Slot 1 is rewritten from slot 0 every frame, so nothing needs undoing.
    // 3. Camera::Update: clamp the look direction to a human head's range,
    //    pivot the eyes around the neck, follow the head's own rotation
    //    (emotes, look-at), and place the camera there.
    //----
    public unsafe partial class Plugin
    {
        private const string HeadBoneName = "j_kao";
        private const string NeckBoneName = "j_kubi";
        private const float Deg2Rad = MathF.PI / 180.0f;

        private sealed class BoneInfo
        {
            public int BoneCount;
            public int HeadIndex = -1;       // body only
            public int NeckIndex = -1;       // body only
            public bool[] InHeadTree = Array.Empty<bool>();
            public int[] EyeIndices = Array.Empty<int>();
            public bool IsHeadPart;          // face / hair / hat skeleton
            public Quaternion HeadRefRotation = Quaternion.Identity; // reference pose, model space
        }

        private readonly Dictionary<nint, BoneInfo> boneInfoCache = new();

        //----
        // Per-frame head data, produced by the animation hook
        //----
        private readonly object headLock = new();
        // Eye/neck relative to the character (model space, scaled). The camera
        // is placed from these and the character's draw transform at the time
        // of placement, so it can't trail a step behind when you move.
        private Vector3 eyeLocal, neckLocal;
        private Vector3 headPointShift;
        private float dbgDrawVsSkel;
        private Vector3 eyeWorldPos, neckWorldPos, rootWorldPos, bodyForward = Vector3.UnitZ;

        //----
        // Smoothed head motion: eye/neck offsets relative to the character's
        // position (so the camera never trails behind when moving) and the
        // head's rotation
        //----
        private Vector3 smoothEyeOffset, smoothNeckOffset;
        private Quaternion smoothHeadDelta = Quaternion.Identity;
        private bool smoothValid = false;
        private const float MaxSmoothingTime = 1.0f;  // seconds at 100% (curve: 50% = 0.25 s)
        private const float MaxEyeLag = 0.03f;        // smoothed eye never drifts further than this from the real eye
        private Quaternion headDeltaWorld = Quaternion.Identity;
        private long headFrame = -100;

        private long frameCounter = 0;
        private int mainThreadId = -1;
        private bool loggedBones = false;

        // Written on the framework thread, read from the hooks
        private volatile bool firstPersonActive = false;
        private nint localGameObject = 0;

        //----
        // Learned relation between the camera's angle fields and the actual
        // look direction (+1 or -1), used to push DirH/DirV back inside limits
        //----
        //----
        // Measured rate between the camera's own angles (DirH/DirV) and the
        // resulting look direction, learned only from frames the player moved
        // the camera and we didn't touch it
        //----
        private sealed class AngleRate
        {
            private float sumXY, sumXX;
            public float Rate => sumXX > 1e-9f ? sumXY / sumXX : 0.0f;
            public bool Confident => sumXX > 0.01f && MathF.Abs(Rate) > 0.25f && MathF.Abs(Rate) < 4.0f;

            public void Add(float dAngle, float dLook)
            {
                if (MathF.Abs(dAngle) < 0.002f || MathF.Abs(dAngle) > 0.5f)
                    return;
                // Decay so the estimate follows changes, but keeps plenty of history
                sumXY = sumXY * 0.98f + dAngle * dLook;
                sumXX = sumXX * 0.98f + dAngle * dAngle;
            }
        }
        private readonly AngleRate yawRate = new(), pitchRate = new();
        private float prevDirH = float.NaN, prevDirV = float.NaN, prevLookYaw, prevLookPitch;
        private bool wroteAnglesLastFrame = false;

        //----
        // In first person the game turns the body toward where the camera
        // points. While that's happening the yaw limit stands aside (past the
        // head's range, the body turns) instead of fighting it.
        //----
        private float prevBodyYaw = float.NaN;
        private float bodyTurnHold = 0.0f;
        private const float BodyTurnHoldTime = 0.3f;
        private const float CorrectionGain = 0.5f;

        //----
        // Target focus
        //----
        private float frameDelta = 1.0f / 60.0f;
        private float outYaw, outPitch;
        private bool outValid = false;
        private float focusRelease = 0.0f;
        private bool focusBroken = false;      // current target left the head's range
        private float recenterTime = 0.0f;     // time left easing the view back to straight ahead
        private const float RecenterTime = 0.7f;

        //----
        // Head-follow weight: fades out while turning to / tracking a target
        // and (optionally) in combat, where animations whip the head around
        //----
        private bool inCombat = false;
        private float headFollowWeight = 1.0f;
        private const float MaxHeadFollowAngle = 25.0f * MathF.PI / 180.0f;
        private const float MaxTargetHeadFollowAngle = 10.0f * MathF.PI / 180.0f;
        private const float TargetHeadSmoothing = 0.35f;       // extra smoothing (s) while tracking
        private Quaternion trackHeadDelta = Quaternion.Identity;
        private float trackBlend = 0.0f;                        // 0 = free look, 1 = tracking a target

        //----
        // While walking/running, real eyes counter-rotate to keep the gaze
        // steady, so most of the head's stride sway is ignored
        //----
        private Vector3 prevRootPos;
        private bool havePrevRoot = false;
        private float moveSpeed = 0.0f;   // smoothed, m/s
        private float moveBlend = 0.0f;   // 0 = still, 1 = moving
        private const float MaxViewPitch = 88.0f * MathF.PI / 180.0f;
        private bool focusingThisFrame = false;

        //----
        // The game doesn't rebuild the scene camera's view matrix every frame,
        // so on some frames it still holds what we wrote last frame. Keep the
        // last matrix the game really built and always derive our view from
        // it in one step, so nothing accumulates between frames.
        //----
        private Matrix4x4 lastWrittenView, cleanView;
        private Vector3 lastWrittenPos;
        // Group pose camera offsets carried on top of the eye position
        private const float MaxGposeOffset = 20.0f;
        private Vector3 pivotBase, gposeOffset, externalOffset;
        private nint pivotObject = 0;
        private bool havePivot = false;
        private Vector3 cleanPos, cleanLook;
        private bool haveWrittenView = false, haveCleanView = false;
        private long dbgViewFresh, dbgViewStale;
        private long dbgStaleFrames;
        private string dbgEyeMethod = "-";
        private float dbgEyeToHead;
        private bool focusTurning = false;   // still swinging toward the target (> FocusSettledAngle away)
        private const float FocusSettledAngle = 3.0f * MathF.PI / 180.0f;
        private const float FocusReleaseTime = 0.5f;
        private nint lastTarget = 0;
        private float focusTimeLeft = 0.0f;
        private const float FocusMaxTime = 1.5f;
        private string dbgFocus = "-";
        private float dbgFocusAim;

        private bool TryGetSkeletonBounds(CSGameObject* target, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            CharacterBase* charBase = (CharacterBase*)target->DrawObject;
            if (charBase == null || charBase->Skeleton == null)
                return false;
            RenderSkeleton* skel = charBase->Skeleton;
            int count = 0;
            for (int p = 0; p < skel->PartialSkeletonCount && p < 8; p++)
            {
                hkaPose* pose = skel->PartialSkeletons[p].GetHavokPose(0);
                if (pose == null)
                    continue;
                int n = Math.Min(pose->ModelPose.Length, 512);
                for (int i = 0; i < n; i++)
                {
                    Vector3 w = ModelToWorld(skel, ToVector3(pose->ModelPose.Data[i].Translation));
                    min = Vector3.Min(min, w);
                    max = Vector3.Max(max, w);
                    count++;
                }
                // Partial skeletons other than the body are attached at render
                // time (not in world space here), so the body is enough
                break;
            }
            return count >= 2 && max.Y - min.Y > 0.05f && max.Y - min.Y < 50.0f;
        }

        private string dbgFocusPath = "-";

        //----
        // Diagnostics (shown in the config window)
        //----
        private bool showDebug = false;
        private long dbgUpdateAnimCalls, dbgProcessed, dbgCameraUpdateCalls, dbgCameraPlaced;
        private string dbgStatus = "-";
        private string dbgError = "";
        private string dbgEyeBones = "(none)";
        private string dbgConnect = "-";
        private string dbgView = "-";
        private int dbgHeadIndex = -1, dbgNeckIndex = -1, dbgPartials;
        private float dbgRelYaw, dbgPitch, dbgHeadAngle;
        private Vector3 dbgHeadWorld, dbgEyeWorld, dbgNeckWorld, dbgSkelPos, dbgCamBefore, dbgCamAfter;
        private CameraControlMode dbgControlMode;

        //----
        // Hooks
        //----
        private delegate void UpdateAnimationsDg(RenderSkeleton* first, float deltaTime);
        private Hook<UpdateAnimationsDg>? UpdateAnimationsHook = null;

        private delegate void CameraUpdateDg(GameCamera* camera);
        private Hook<CameraUpdateDg>? CameraUpdateHook = null;

        private void InitializeFirstPerson()
        {
            //----
            // Settings upgrade: eye offsets set before the eye position was
            // fixed would now push the camera away from the eyes
            //----
            if (Config.Version < 1)
            {
                Config.EyeForwardOffset = 0.0f;
                Config.EyeHeightOffset = 0.0f;
                Config.Version = 1;
                Config.Save();
            }

            UpdateAnimationsHook = Interop.HookFromAddress<UpdateAnimationsDg>((nint)RenderSkeleton.MemberFunctionPointers.UpdateAnimations, UpdateAnimationsFn);
            ClientState!.TerritoryChanged += OnTerritoryChanged;
        }

        private void DisposeFirstPerson()
        {
            ClientState!.TerritoryChanged -= OnTerritoryChanged;
        }

        private void OnTerritoryChanged(uint territory)
        {
            lock (boneInfoCache)
                boneInfoCache.Clear();
        }

        [HandleStatus("UpdateAnimations")]
        public void UpdateAnimationsStatus(bool status, bool dispose)
        {
            if (dispose)
                UpdateAnimationsHook?.Dispose();
            else if (status)
                UpdateAnimationsHook?.Enable();
            else
                UpdateAnimationsHook?.Disable();
        }

        [HandleStatus("CameraUpdate")]
        public void CameraUpdateStatus(bool status, bool dispose)
        {
            if (dispose)
                CameraUpdateHook?.Dispose();
            else if (status)
                CameraUpdateHook?.Enable();
            else
                CameraUpdateHook?.Disable();
        }

        private void TryCreateCameraUpdateHook()
        {
            //----
            // CameraBase vtable +0x18 is Update; taken from the live camera so no
            // signature is needed
            //----
            CSCameraManager* cameraManager = CSCameraManager.Instance();
            if (cameraManager == null || cameraManager->Camera == null)
                return;

            nint updateAddress = ((nint*)cameraManager->Camera->VirtualTable)[3];
            CameraUpdateHook = Interop.HookFromAddress<CameraUpdateDg>(updateAddress, CameraUpdateFn);
            if (isEnabled)
                CameraUpdateHook.Enable();
        }

        //----
        // Framework thread: decide whether first person is active this frame
        //----
        private void UpdateFirstPersonState()
        {
            mainThreadId = Environment.CurrentManagedThreadId;
            frameCounter++;

            nint gameObject = 0;
            bool active = false;

            if (isEnabled && !suspended && Config.FirstPersonFix && !inCutscene)
            {
                CSCameraManager* cameraManager = CSCameraManager.Instance();
                if (cameraManager != null && cameraManager->ActiveCameraIndex == 0 && cameraManager->Camera != null)
                {
                    GameCamera* camera = cameraManager->Camera;
                    dbgZoom = camera->Distance;
                    dbgZoomMin = camera->MinDistance;
                    if (inGpose)
                    {
                        //----
                        // Group pose has no first person of its own: zooming
                        // all the way in stands in for it. Look out of the
                        // character the group pose camera is centred on.
                        //----
                        bool zoomedIn = camera->Distance <= camera->MinDistance + (gposeZoomedIn ? 0.06f : 0.02f);
                        gposeZoomedIn = zoomedIn;
                        if (Config.GposeFirstPerson && (zoomedIn || IsFirstPersonMode(camera->ControlMode)))
                        {
                            gameObject = GetGposeCharacter();
                            active = gameObject != 0;
                        }
                    }
                    else if (IsFirstPersonMode(camera->ControlMode))
                    {
                        var player = ObjectTable!.LocalPlayer;
                        if (player != null)
                        {
                            gameObject = player.Address;
                            active = true;
                        }
                    }
                }
            }

            localGameObject = gameObject;
            firstPersonActive = active;
            RecordPropState();
        }

        private bool gposeZoomedIn = false;
        private float dbgZoom, dbgZoomMin;

        //----
        // The character the group pose camera is centred on, or your own
        // posed copy when that isn't a character
        //----
        private nint GetGposeCharacter()
        {
            CSGameObject* target = targetSystem->GPoseTarget;
            if (target != null && target->IsCharacter() && target->DrawObject != null)
                return (nint)target;
            nint self = ObjectTable!.GetObjectAddress(GposeFirstIndex);
            if (self != 0 && ((CSGameObject*)self)->DrawObject != null)
                return self;
            return 0;
        }

        private static bool IsFirstPersonMode(CameraControlMode mode) =>
            mode == CameraControlMode.FirstPerson ||
            mode == CameraControlMode.LockonFirstPerson ||
            mode == CameraControlMode.FirstPersonUnk;

        private RenderSkeleton* GetLocalSkeleton()
        {
            nint gameObject = localGameObject;
            if (gameObject == 0)
                return null;
            CharacterBase* charBase = (CharacterBase*)((CSGameObject*)gameObject)->DrawObject;
            if (charBase == null)
                return null;
            return charBase->Skeleton;
        }

        //----
        // Skeleton::UpdateAnimations
        //----
        private void UpdateAnimationsFn(RenderSkeleton* first, float deltaTime)
        {
            UpdateAnimationsHook!.Original(first, deltaTime);
            dbgUpdateAnimCalls++;

            if (!firstPersonActive)
            {
                dbgStatus = "inactive (not first person / disabled)";
                return;
            }

            RenderSkeleton* skeleton = GetLocalSkeleton();
            if (skeleton == null)
            {
                dbgStatus = "no local skeleton";
                return;
            }

            try
            {
                ProcessLocalSkeleton(skeleton);
            }
            catch (Exception e)
            {
                Log!.Error(e, "Fresh Set of Eyes: first person update failed");
                dbgError = e.Message;
                firstPersonActive = false;
            }
        }

        private nint headObject = 0;   // which character the head reading belongs to
        private long dbgDirectReads = 0;

        private void ProcessLocalSkeleton(RenderSkeleton* skeleton, bool replaceCamera = true)
        {
            if (skeleton->PartialSkeletonCount == 0 || skeleton->PartialSkeletons == null)
                return;

            //----
            // Partial 0 is the body skeleton, which holds the head and neck bones
            //----
            PartialSkeleton* body = &skeleton->PartialSkeletons[0];
            hkaPose* bodyPose = body->GetHavokPose(0);
            if (bodyPose == null || bodyPose->Skeleton == null)
                return;

            BoneInfo bodyInfo = GetBoneInfo(bodyPose->Skeleton, true);
            dbgHeadIndex = bodyInfo.HeadIndex;
            dbgNeckIndex = bodyInfo.NeckIndex;
            dbgPartials = skeleton->PartialSkeletonCount;
            if (bodyInfo.HeadIndex < 0)
            {
                dbgStatus = "no j_kao bone in body skeleton";
                return; // not a humanoid skeleton
            }

            var bodyModel = bodyPose->GetSyncedPoseModelSpace();
            if (bodyInfo.HeadIndex >= bodyModel->Length)
                return;
            hkQsTransformf headT = bodyModel->Data[bodyInfo.HeadIndex];
            Vector3 headModel = ToVector3(headT.Translation);
            Vector3 neckModel = bodyInfo.NeckIndex >= 0 && bodyInfo.NeckIndex < bodyModel->Length
                ? ToVector3(bodyModel->Data[bodyInfo.NeckIndex].Translation)
                : headModel;

            //----
            // Eye position: average of the face skeleton's eye bones, mapped
            // onto the animated body:  eye * inverse(partialRoot) * bodyParentBone
            //----
            Vector3 eyeModel = headModel;
            Vector3 eyeSum = Vector3.Zero;
            int eyeCount = 0;
            for (int p = 1; p < skeleton->PartialSkeletonCount; p++)
            {
                PartialSkeleton* partial = &skeleton->PartialSkeletons[p];
                hkaPose* pose = partial->GetHavokPose(0);
                if (pose == null || pose->Skeleton == null)
                    continue;
                BoneInfo info = GetBoneInfo(pose->Skeleton, false);
                if (info.EyeIndices.Length == 0)
                    continue;

                int parentBone = partial->ConnectedParentBoneIndex;
                int rootBone = partial->ConnectedBoneIndex;
                var model = pose->GetSyncedPoseModelSpace();
                if (parentBone < 0 || parentBone >= bodyModel->Length || rootBone < 0 || rootBone >= model->Length)
                    continue;

                if (!Matrix4x4.Invert(ToMatrix(model->Data[rootBone]), out Matrix4x4 rootInverse))
                    continue;
                Matrix4x4 partialToBody = rootInverse * ToMatrix(bodyModel->Data[parentBone]);

                foreach (int idx in info.EyeIndices)
                {
                    if (idx < model->Length)
                    {
                        eyeSum += Vector3.Transform(ToVector3(model->Data[idx].Translation), partialToBody);
                        eyeCount++;
                    }
                }
                dbgConnect = $"p{p} root={rootBone} parent={parentBone}";
                break;
            }
            if (eyeCount > 0)
                eyeModel = eyeSum / eyeCount;

            //----
            // Safety net: if the eyes still come out on top of the head bone
            // (unusual face skeleton), use a typical offset: ~9 cm up and ~7 cm
            // forward of the head bone, turned with the head
            //----
            float eyeToHead = Vector3.Distance(eyeModel, headModel);
            if (eyeToHead < 0.03f)
            {
                Quaternion headRotModel = Quaternion.Normalize(ToQuaternion(headT.Rotation));
                Quaternion deltaModel = Quaternion.Normalize(headRotModel * Quaternion.Inverse(bodyInfo.HeadRefRotation));
                eyeModel = headModel + Vector3.Transform(new Vector3(0, 0.09f, 0.07f), deltaModel);
                dbgEyeMethod = "typical offset (eye bones not usable)";
            }
            else
            {
                dbgEyeMethod = $"eye bones ({eyeCount})";
            }
            dbgEyeToHead = Vector3.Distance(eyeModel, headModel);

            //----
            // Head rotation relative to its reference pose (animation + look-at),
            // expressed in world space
            //----
            Quaternion skelRot = skeleton->Transform.Rotation;
            Quaternion headRot = Quaternion.Normalize(ToQuaternion(headT.Rotation));
            Quaternion headDeltaModel = Quaternion.Normalize(headRot * Quaternion.Inverse(bodyInfo.HeadRefRotation));
            Quaternion headDelta = Quaternion.Normalize(skelRot * headDeltaModel * Quaternion.Inverse(skelRot));

            Vector3 forward = Vector3.Transform(Vector3.UnitZ, skelRot);
            forward.Y = 0;
            forward = forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : Vector3.UnitZ;

            CollectHeadPoints(skeleton, bodyInfo, bodyModel);

            Vector3 eyeWorld = ModelToWorld(skeleton, eyeModel);
            Vector3 neckWorld = ModelToWorld(skeleton, neckModel);
            lock (headLock)
            {
                eyeWorldPos = eyeWorld;
                neckWorldPos = neckWorld;
                rootWorldPos = skeleton->Transform.Position;
                Vector3 skelScale = skeleton->Transform.Scale;
                eyeLocal = eyeModel * skelScale;
                neckLocal = neckModel * skelScale;
                bodyForward = forward;
                headDeltaWorld = headDelta;
                headFrame = frameCounter;
                headObject = localGameObject;
            }

            dbgProcessed++;
            dbgStatus = eyeCount > 0 ? $"ok, {eyeCount} eye bones" : "ok, using head bone (no eye bones)";
            dbgEyeWorld = eyeWorld;
            dbgNeckWorld = neckWorld;
            dbgHeadWorld = ModelToWorld(skeleton, headModel);
            dbgSkelPos = skeleton->Transform.Position;
            dbgHeadAngle = 2.0f * MathF.Acos(Math.Clamp(MathF.Abs(headDeltaModel.W), 0, 1)) / Deg2Rad;

            //----
            // If the camera already updated this frame, re-place it now with the
            // fresh head data (only safe on the main thread)
            //----
            if (replaceCamera && Environment.CurrentManagedThreadId == mainThreadId)
            {
                CSCameraManager* cameraManager = CSCameraManager.Instance();
                if (cameraManager != null && cameraManager->Camera != null)
                    PlaceFirstPersonCamera(cameraManager->Camera, false);
            }
        }

        //----
        // Bone lookup, cached per havok skeleton
        //----
        private BoneInfo GetBoneInfo(hkaSkeleton* hkSkeleton, bool isBody)
        {
            lock (boneInfoCache)
                return GetBoneInfoLocked(hkSkeleton, isBody);
        }

        private BoneInfo GetBoneInfoLocked(hkaSkeleton* hkSkeleton, bool isBody)
        {
            int boneCount = hkSkeleton->Bones.Length;
            if (boneInfoCache.TryGetValue((nint)hkSkeleton, out BoneInfo? cached) && cached.BoneCount == boneCount)
                return cached;

            var info = new BoneInfo { BoneCount = boneCount, InHeadTree = new bool[boneCount] };
            var eyes = new List<int>();
            var names = new string[boneCount];

            for (int i = 0; i < boneCount; i++)
            {
                string name = hkSkeleton->Bones[i].Name.String ?? string.Empty;
                names[i] = name;
                if (isBody && name == HeadBoneName)
                    info.HeadIndex = i;
                if (isBody && name == NeckBoneName)
                    info.NeckIndex = i;
                //----
                // The actual eyeballs only. The other j_f_eye* bones (eyeprm,
                // eyeprmroll, eyepuru) are expression parameter bones that sit
                // near the face root and would drag the average down to the jaw.
                //----
                if (name.Equals("j_f_eye_l", StringComparison.OrdinalIgnoreCase) || name.Equals("j_f_eye_r", StringComparison.OrdinalIgnoreCase))
                    eyes.Add(i);
                if (!isBody && (name.StartsWith("j_f_") || name.StartsWith("j_kami") || name.StartsWith("j_ex_met")))
                    info.IsHeadPart = true;
            }
            info.EyeIndices = eyes.ToArray();

            var parents = hkSkeleton->ParentIndices;
            if (info.HeadIndex >= 0)
            {
                for (int i = 0; i < boneCount; i++)
                {
                    int cur = i;
                    int guard = 0;
                    while (cur >= 0 && cur < parents.Length && guard++ < 256)
                    {
                        if (cur == info.HeadIndex)
                        {
                            info.InHeadTree[i] = true;
                            break;
                        }
                        cur = parents[cur];
                    }
                }

                //----
                // Reference (neutral) model-space rotation of the head bone
                //----
                Quaternion refRot = Quaternion.Identity;
                var chain = new List<int>();
                for (int cur = info.HeadIndex, guard = 0; cur >= 0 && cur < parents.Length && guard < 256; cur = parents[cur], guard++)
                    chain.Add(cur);
                for (int c = chain.Count - 1; c >= 0; c--)
                {
                    int idx = chain[c];
                    if (idx < hkSkeleton->ReferencePose.Length)
                        refRot = Quaternion.Normalize(refRot * ToQuaternion(hkSkeleton->ReferencePose[idx].Rotation));
                }
                info.HeadRefRotation = refRot;
            }

            if (!loggedBones && eyes.Count > 0)
            {
                loggedBones = true;
                var eyeNames = new List<string>();
                foreach (int e in eyes) eyeNames.Add(names[e]);
                dbgEyeBones = string.Join(", ", eyeNames);
                Log!.Info($"Fresh Set of Eyes: eye bones = {dbgEyeBones}");
            }

            boneInfoCache[(nint)hkSkeleton] = info;
            return info;
        }

        //----
        // Camera::Update
        //----
        private void CameraUpdateFn(GameCamera* camera)
        {
            CameraUpdateHook!.Original(camera);
            dbgCameraUpdateCalls++;
            dbgControlMode = camera->ControlMode;

            ApplyFaceClip();

            if (!firstPersonActive)
            {
                prevDirH = float.NaN;
                prevBodyYaw = float.NaN;
                smoothValid = false;
                havePrevRoot = false;
                outValid = false;
                haveWrittenView = false;
                haveCleanView = false;
                havePivot = false;
                gposeOffset = Vector3.Zero;
                externalOffset = Vector3.Zero;
                focusRelease = 0.0f;
                return;
            }

            CSCameraManager* cameraManager = CSCameraManager.Instance();
            if (cameraManager == null || cameraManager->Camera != camera)
                return;

            //----
            // When animations aren't being updated (motion frozen in group
            // pose) the skeleton hook doesn't run, so read the pose here
            //----
            if (frameCounter - headFrame >= 2 || headObject != localGameObject)
            {
                try
                {
                    RenderSkeleton* skeleton = GetLocalSkeleton();
                    if (skeleton != null)
                    {
                        ProcessLocalSkeleton(skeleton, false);
                        dbgDirectReads++;
                    }
                }
                catch (Exception e)
                {
                    dbgError = e.Message;
                }
            }

            PlaceFirstPersonCamera(camera, true);
        }

        //----
        // The camera sits at the eyes, so the only parts of the character it
        // can see closer than a few centimetres are the face itself (nose,
        // lips, teeth, chin). A larger near clip cuts those away while the
        // body, hands and weapon stay visible.
        //----
        private bool faceClipApplied = false;
        private float cameraEyeGap = 0.0f;   // distance between our camera and the real eyes, last frame
        private const float MinFaceClip = 0.10f;  // below this your own eyelids, lashes and brows show
        private const float MaxFaceClip = 0.30f;
        private const float FaceClipMargin = 0.03f; // skin beyond the face bones
        private const float HairClipMargin = 0.09f; // hair strands/fringe reach well past the few hair bones

        //----
        // Automatic face clip: world positions of the head's bones (body head
        // tree + face/hair skeletons), so the clip can be set just past the
        // furthest part of your own face in front of the camera
        //----
        private Vector3[] headPoints = new Vector3[256];
        private float[] headPointMargin = new float[256];
        private int headPointCount = 0;
        private float autoFaceClip = MinFaceClip;

        private void CollectHeadPoints(RenderSkeleton* skeleton, BoneInfo bodyInfo, FFXIVClientStructs.Havok.Common.Base.Container.Array.hkArray<hkQsTransformf>* bodyModel)
        {
            int n = 0;
            for (int i = 0; i < bodyModel->Length && i < bodyInfo.InHeadTree.Length && n < headPoints.Length; i++)
            {
                if (bodyInfo.InHeadTree[i])
                {
                    headPointMargin[n] = FaceClipMargin;
                    headPoints[n++] = ModelToWorld(skeleton, ToVector3(bodyModel->Data[i].Translation));
                }
            }

            for (int p = 1; p < skeleton->PartialSkeletonCount && n < headPoints.Length; p++)
            {
                PartialSkeleton* partial = &skeleton->PartialSkeletons[p];
                hkaPose* pose = partial->GetHavokPose(0);
                if (pose == null || pose->Skeleton == null)
                    continue;
                int parentBone = partial->ConnectedParentBoneIndex;
                bool attachedToHead = parentBone >= 0 && parentBone < bodyInfo.InHeadTree.Length && bodyInfo.InHeadTree[parentBone];
                if (!attachedToHead && !GetBoneInfo(pose->Skeleton, false).IsHeadPart)
                    continue;
                int rootBone = partial->ConnectedBoneIndex < 0 ? 0 : partial->ConnectedBoneIndex;
                var model = pose->GetSyncedPoseModelSpace();
                if (parentBone < 0 || parentBone >= bodyModel->Length || rootBone >= model->Length)
                    continue;
                if (!Matrix4x4.Invert(ToMatrix(model->Data[rootBone]), out Matrix4x4 rootInverse))
                    continue;
                Matrix4x4 partialToBody = rootInverse * ToMatrix(bodyModel->Data[parentBone]);
                // The face skeleton has the eye bones; anything else attached to
                // the head (hair, hat, ears) is sparse and gets a bigger margin
                float margin = GetBoneInfo(pose->Skeleton, false).EyeIndices.Length > 0 ? FaceClipMargin : HairClipMargin;
                for (int i = 0; i < model->Length && n < headPoints.Length; i++)
                {
                    headPointMargin[n] = margin;
                    headPoints[n++] = ModelToWorld(skeleton, Vector3.Transform(ToVector3(model->Data[i].Translation), partialToBody));
                }
            }
            headPointCount = n;
        }

        //----
        // How far the face reaches in front of the camera along the view
        // direction (+ margin). Grows instantly, shrinks gently.
        //----
        private void UpdateAutoFaceClip(Vector3 camPos, Vector3 viewDir)
        {
            float reach = 0.0f;
            for (int i = 0; i < headPointCount; i++)
            {
                Vector3 d = headPoints[i] + headPointShift - camPos;
                if (d.LengthSquared() > 0.5f * 0.5f)
                    continue; // not part of our head (stale/garbage)
                reach = MathF.Max(reach, Vector3.Dot(d, viewDir) + headPointMargin[i]);
            }
            float want = Math.Clamp(reach, MinFaceClip, MaxFaceClip);
            if (want > autoFaceClip)
                autoFaceClip = want;
            else
                autoFaceClip += (want - autoFaceClip) * (1.0f - MathF.Exp(-frameDelta * 3.0f));
        }
        private void ApplyFaceClip()
        {
            bool want = firstPersonActive && Config.HideHead;
            if (!want && !faceClipApplied)
                return;

            RenderCamera* renderCamera = GetGameRenderCamera();
            if (renderCamera == null)
                return;

            if (want)
            {
                //----
                // The setting is measured from the real eyes; if the camera has
                // been offset away from them (eye position sliders, neck pivot,
                // smoothing), add that gap so the face is still cut away
                //----
                renderCamera->NearPlane = Math.Clamp(autoFaceClip, NearClipOverride, MaxFaceClip);
                faceClipApplied = true;
            }
            else
            {
                renderCamera->NearPlane = suspended ? origNearClip : NearClipOverride;
                faceClipApplied = false;
            }
        }

        //----
        // Called late in the frame (from the game task loop) so the camera
        // matches where the character ends up being drawn this frame
        //----
        private void LatePlaceCamera()
        {
            if (!firstPersonActive)
                return;
            CSCameraManager* cameraManager = CSCameraManager.Instance();
            if (cameraManager == null || cameraManager->ActiveCameraIndex != 0 || cameraManager->Camera == null)
                return;
            try
            {
                PlaceFirstPersonCamera(cameraManager->Camera, false);
            }
            catch (Exception e)
            {
                dbgError = e.Message;
            }
        }

        private void PlaceFirstPersonCamera(GameCamera* camera, bool fromCameraUpdate)
        {
            Vector3 eye, neck, root, bodyFwd;
            Quaternion headDelta;
            lock (headLock)
            {
                //----
                // If the head reading is a little old (a hitch, mount, zone
                // change), keep using it relative to where the character is now
                // instead of handing back to the game's camera
                //----
                long age = frameCounter - headFrame;
                if (age > 30 || headObject != localGameObject)
                    return;
                eye = eyeLocal;
                neck = neckLocal;
                root = rootWorldPos;
                bodyFwd = bodyForward;
                headDelta = headDeltaWorld;
            }
            if (frameCounter - headFrame > 2)
                dbgStaleFrames++;

            //----
            // Where the character is being drawn right now
            //----
            if (localGameObject == 0)
                return;
            CharacterBase* drawObject = (CharacterBase*)((CSGameObject*)localGameObject)->DrawObject;
            if (drawObject == null)
                return;
            Vector3 drawPos = drawObject->Position;
            Quaternion drawRot = drawObject->Rotation;
            dbgDrawVsSkel = Vector3.Distance(drawPos, root);
            headPointShift = drawPos - root;
            eye = drawPos + Vector3.Transform(eye, drawRot);
            neck = drawPos + Vector3.Transform(neck, drawRot);
            root = drawPos;
            Vector3 liveEye = eye;

            ref var scene = ref camera->CameraBase.SceneCamera;
            Vector3 gamePos = scene.Position;
            Vector3 gameLookAt = scene.LookAtVector;
            Matrix4x4 gameView = scene.ViewMatrix;

            //----
            // The game's look direction this frame (only meaningful straight
            // after Camera::Update; on the re-place pass reuse what we stored)
            //----
            Vector3 look = gameLookAt - gamePos;
            float lookDist = look.Length();
            if (lookDist < 1e-4f)
                return;
            look /= lookDist;
            //----
            // Smooth the head motion once per frame (on the Camera::Update pass)
            //----
            if (fromCameraUpdate)
            {
                Vector3 eyeOffset = eye - root, neckOffset = neck - root;
                float amount = Math.Clamp(Config.HeadSmoothing, 0, 1);
                float tau = amount * amount * MaxSmoothingTime;
                if (!smoothValid || tau <= 0.0f || (eyeOffset - smoothEyeOffset).LengthSquared() > 1.0f)
                {
                    smoothEyeOffset = eyeOffset;
                    smoothNeckOffset = neckOffset;
                    smoothHeadDelta = headDelta;
                    smoothValid = true;
                }
                else
                {
                    float k = 1.0f - MathF.Exp(-frameDelta / tau);
                    smoothEyeOffset = LimitLag(Vector3.Lerp(smoothEyeOffset, eyeOffset, k), eyeOffset);
                    smoothNeckOffset = LimitLag(Vector3.Lerp(smoothNeckOffset, neckOffset, k), neckOffset);
                    smoothHeadDelta = Quaternion.Normalize(Quaternion.Slerp(smoothHeadDelta, headDelta, k));
                }
            }
            if (smoothValid)
            {
                eye = root + smoothEyeOffset;
                neck = root + smoothNeckOffset;
                headDelta = smoothHeadDelta;
            }

            if (!fromCameraUpdate)
            {
                // Already placed this frame: only refresh the position part
                //----
                // Group pose: if something moved the camera since we placed it
                // (Brio adds its position offset after the game's update), that
                // is a deliberate offset, so carry it instead of undoing it
                //----
                Vector3 matrixPos = gamePos;
                if (inGpose && haveWrittenView)
                {
                    Vector3 moved = gamePos - lastWrittenPos;
                    if (moved.LengthSquared() > 1e-10f && moved.LengthSquared() < MaxGposeOffset * MaxGposeOffset)
                        externalOffset += moved;
                    // Our matrix is still in place: it belongs to where we put the camera
                    if (SameMatrix(gameView, lastWrittenView))
                        matrixPos = lastWrittenPos;
                }
                Vector3 wanted = ComputeEye(eye, neck, bodyFwd, look) + gposeOffset + externalOffset;
                Vector3 shift = wanted - gamePos;
                Vector3 matrixShift = wanted - matrixPos;
                if (shift.LengthSquared() < 1e-10f && matrixShift.LengthSquared() < 1e-10f)
                    return;
                scene.Position = wanted;
                scene.LookAtVector = gameLookAt + shift;
                scene.ViewMatrix = Matrix4x4.CreateTranslation(-matrixShift) * gameView;
                lastWrittenView = scene.ViewMatrix;
                lastWrittenPos = wanted;
                dbgCamAfter = wanted;
                cameraEyeGap = Math.Min(Vector3.Distance(wanted, liveEye), 0.5f);
                UpdateAutoFaceClip(wanted, look);
                ApplyFaceClip();
                return;
            }

            //----
            // Was the game's matrix rebuilt since our last write?
            //----
            bool fresh = !haveWrittenView || !SameMatrix(gameView, lastWrittenView);
            if (fresh || !haveCleanView)
            {
                cleanView = gameView;
                cleanPos = gamePos;
                cleanLook = look;
                haveCleanView = true;
                dbgViewFresh++;
            }
            else
            {
                dbgViewStale++;
            }

            float lookYaw = MathF.Atan2(look.X, look.Z);
            float lookPitch = MathF.Asin(Math.Clamp(look.Y, -1, 1));
            float bodyYaw = MathF.Atan2(bodyFwd.X, bodyFwd.Z);
            float relYaw = WrapAngle(lookYaw - bodyYaw);

            //----
            // Learn which way DirH / DirV move the view
            //----
            bool bodyTurned = !float.IsNaN(prevBodyYaw) && MathF.Abs(WrapAngle(bodyYaw - prevBodyYaw)) > 0.0015f;
            prevBodyYaw = bodyYaw;
            if (bodyTurned)
                bodyTurnHold = BodyTurnHoldTime;
            else
                bodyTurnHold = MathF.Max(0.0f, bodyTurnHold - frameDelta);
            bool bodyTurning = bodyTurnHold > 0.0f;

            if (!float.IsNaN(prevDirH) && !wroteAnglesLastFrame)
            {
                if (!bodyTurned)
                    yawRate.Add(WrapAngle(camera->DirH - prevDirH), WrapAngle(lookYaw - prevLookYaw));
                pitchRate.Add(camera->DirV - prevDirV, lookPitch - prevLookPitch);
            }

            //----
            // Clamp to the range a head can turn relative to the body, and push
            // the camera's own angles back inside so the mouse doesn't wind up
            //----
            prevDirH = camera->DirH;
            prevDirV = camera->DirV;
            prevLookYaw = lookYaw;
            prevLookPitch = lookPitch;

            //----
            // Turn toward a newly selected target (what the character's head
            // does), easing the camera's own angles so the mouse continues
            // from there
            //----
            //----
            // The view direction is our own state while turning to a target, so
            // it glides smoothly regardless of how quickly the game's camera
            // angles catch up. Afterwards it eases back to the mouse's direction.
            //----
            if (!outValid)
            {
                outYaw = lookYaw;
                outPitch = lookPitch;
                outValid = true;
            }

            focusingThisFrame = UpdateTargetFocus(eye, bodyYaw, out float targetRelYaw, out float targetPitch);
            if (focusingThisFrame)
            {
                float k = 1.0f - MathF.Exp(-frameDelta * Math.Max(Config.FocusSpeed, 0.1f));
                outYaw = WrapAngle(outYaw + WrapAngle(bodyYaw + targetRelYaw - outYaw) * k);
                outPitch += (targetPitch - outPitch) * k;
                focusRelease = FocusReleaseTime;
                float yawErr = WrapAngle(bodyYaw + targetRelYaw - outYaw), pitchErr = targetPitch - outPitch;
                focusTurning = MathF.Sqrt(yawErr * yawErr + pitchErr * pitchErr) > FocusSettledAngle;
            }
            else if (focusRelease > 0.0f)
            {
                focusTurning = false;
                focusRelease -= frameDelta;
                float k = 1.0f - MathF.Exp(-frameDelta * 12.0f);
                outYaw = WrapAngle(outYaw + WrapAngle(lookYaw - outYaw) * k);
                outPitch += (lookPitch - outPitch) * k;
            }
            else
            {
                focusTurning = false;
                outYaw = lookYaw;
                outPitch = lookPitch;
            }

            float wantRelYaw = WrapAngle(outYaw - bodyYaw), wantPitch = outPitch;

            //----
            // Clamp to the range a head can turn relative to the body
            //----
            float relYawC = wantRelYaw, pitchC = wantPitch;
            if (Config.LimitHeadTurn)
            {
                float yawLim = Config.HeadYawLimit * Deg2Rad;
                if (!bodyTurning)
                    relYawC = Math.Clamp(wantRelYaw, -yawLim, yawLim);
                pitchC = Math.Clamp(wantPitch, -Config.HeadPitchDownLimit * Deg2Rad, Config.HeadPitchUpLimit * Deg2Rad);
            }
            // Keep our own state inside the limits too
            outYaw = WrapAngle(bodyYaw + relYawC);
            outPitch = pitchC;

            //----
            // Push the camera's own angles toward the clamped / focused view so
            // the mouse doesn't wind up past the limit. Only once the rate is
            // measured, and damped (a fraction per frame) so an imperfect
            // estimate settles instead of overshooting. The picture itself is
            // clamped every frame, so the damping isn't visible.
            //----
            bool wrote = false;
            float yawFix = WrapAngle(relYawC - relYaw);
            if (MathF.Abs(yawFix) > 0.0005f && !bodyTurning)
            {
                camera->DirH += CorrectionGain * yawFix / SteerRate(yawRate);
                wrote = true;
            }
            float pitchFix = pitchC - lookPitch;
            if (MathF.Abs(pitchFix) > 0.0005f)
            {
                camera->DirV += CorrectionGain * pitchFix / SteerRate(pitchRate);
                wrote = true;
            }
            wroteAnglesLastFrame = wrote;

            float yawC = bodyYaw + relYawC;
            Vector3 lookC = new(MathF.Sin(yawC) * MathF.Cos(pitchC), MathF.Sin(pitchC), MathF.Cos(yawC) * MathF.Cos(pitchC));

            //----
            // Follow the head's own rotation (emotes, look-at) on top of the
            // player's look
            //----
            //----
            // Fade head motion out only while actually swinging toward a target
            // (and in combat if asked); once on target it comes back, even while
            // the target is being tracked
            //----
            bool suppress = (focusingThisFrame && focusTurning) || (Config.IgnoreHeadInCombat && inCombat);
            float weightTarget = suppress ? 0.0f : 1.0f;
            headFollowWeight += (weightTarget - headFollowWeight) * (1.0f - MathF.Exp(-frameDelta * 4.0f));

            //----
            // While tracking a target the head motion is lighter, slower and
            // can't move the view far off the target, so it reads as the head
            // swaying rather than the view being dragged around
            //----
            bool tracking = focusingThisFrame && !focusTurning;
            trackBlend += ((tracking ? 1.0f : 0.0f) - trackBlend) * (1.0f - MathF.Exp(-frameDelta * 4.0f));
            trackHeadDelta = Quaternion.Normalize(Quaternion.Slerp(trackHeadDelta, headDelta, 1.0f - MathF.Exp(-frameDelta / TargetHeadSmoothing)));
            Quaternion followSource = Quaternion.Normalize(Quaternion.Slerp(headDelta, trackHeadDelta, trackBlend));
            float strengthScale = 1.0f + (Math.Clamp(Config.TargetHeadFollow, 0, 1) - 1.0f) * trackBlend;

            if (havePrevRoot && frameDelta > 1e-4f)
            {
                Vector2 step = new(root.X - prevRootPos.X, root.Z - prevRootPos.Z);
                float speed = step.Length() / frameDelta;
                if (speed > 30.0f) speed = 0.0f; // teleport / zone change
                moveSpeed += (speed - moveSpeed) * (1.0f - MathF.Exp(-frameDelta * 8.0f));
            }
            prevRootPos = root;
            havePrevRoot = true;
            float moveTarget = Math.Clamp((moveSpeed - 0.3f) / 1.2f, 0.0f, 1.0f); // fully "moving" by ~1.5 m/s
            moveBlend += (moveTarget - moveBlend) * (1.0f - MathF.Exp(-frameDelta * 5.0f));
            strengthScale *= 1.0f + (Math.Clamp(Config.MovingHeadFollow, 0, 1) - 1.0f) * moveBlend;
            float maxFollow = MaxHeadFollowAngle + (MaxTargetHeadFollowAngle - MaxHeadFollowAngle) * trackBlend;

            Vector3 viewDir = lookC;
            float strength = Math.Clamp(Config.HeadFollowStrength, 0, 1) * headFollowWeight * strengthScale;
            if (strength > 0.001f)
            {
                //----
                // Only the change of direction is used (roll is discarded by the
                // yaw/pitch view update), and it's capped so big animation head
                // swings can't throw the view around
                //----
                Quaternion follow = Quaternion.Slerp(Quaternion.Identity, followSource, strength);
                Vector3 followed = Vector3.Normalize(Vector3.Transform(lookC, follow));
                float angle = MathF.Acos(Math.Clamp(Vector3.Dot(lookC, followed), -1, 1));
                if (angle > maxFollow)
                    followed = Vector3.Normalize(Vector3.Lerp(lookC, followed, maxFollow / angle));
                viewDir = followed;
            }

            //----
            // Never let the view reach straight up/down, where yaw flips
            //----
            float viewPitch = MathF.Asin(Math.Clamp(viewDir.Y, -1, 1));
            if (MathF.Abs(viewPitch) > MaxViewPitch)
            {
                float vy = MathF.Atan2(viewDir.X, viewDir.Z);
                float vp = MathF.CopySign(MaxViewPitch, viewPitch);
                viewDir = new Vector3(MathF.Sin(vy) * MathF.Cos(vp), MathF.Sin(vp), MathF.Cos(vy) * MathF.Cos(vp));
            }

            //----
            // Group pose: moving the camera's pivot (the game's own camera pan,
            // or Brio's position offset) moves the view away from the eyes by
            // the same amount, measured from where the pivot was on entering
            // first person
            //----
            externalOffset = Vector3.Zero;
            if (inGpose)
            {
                Vector3 pivot = gameLookAt - root;
                if (!havePivot || pivotObject != localGameObject)
                {
                    pivotBase = pivot;
                    pivotObject = localGameObject;
                    havePivot = true;
                }
                gposeOffset = pivot - pivotBase;
                if (gposeOffset.LengthSquared() < 1e-8f || gposeOffset.LengthSquared() > MaxGposeOffset * MaxGposeOffset)
                    gposeOffset = Vector3.Zero;
            }
            else
            {
                havePivot = false;
                gposeOffset = Vector3.Zero;
            }

            Vector3 eyePos = ComputeEye(eye, neck, bodyFwd, lookC) + gposeOffset;

            //----
            // Apply the change to the game's own view matrix as a delta, so it
            // works whatever convention the game uses (its matrix isn't a plain
            // LookAt). With V mapping world -> view (row vectors) and the game's
            // camera at gamePos:
            //   V' = T(-eye) * R(change)^-1 * T(gamePos) * V
            // The change is split into yaw then pitch so it never adds roll.
            //----
            Quaternion change = YawPitchChange(cleanLook, viewDir);
            Matrix4x4 newView = Matrix4x4.CreateTranslation(-eyePos)
                              * Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(change))
                              * Matrix4x4.CreateTranslation(cleanPos)
                              * cleanView;
            scene.ViewMatrix = newView;
            lastWrittenView = newView;
            haveWrittenView = true;
            scene.Position = eyePos;
            scene.LookAtVector = eyePos + viewDir * lookDist;
            lastWrittenPos = eyePos;

            float changeDeg = 2.0f * MathF.Acos(Math.Clamp(MathF.Abs(change.W), 0, 1)) / Deg2Rad;
            dbgView = $"delta rotation {changeDeg:F1} deg, game matrix fresh {dbgViewFresh} stale {dbgViewStale}";

            dbgRelYaw = relYaw / Deg2Rad;
            dbgPitch = lookPitch / Deg2Rad;
            dbgCamBefore = gamePos;
            dbgCamAfter = eyePos;
            cameraEyeGap = Math.Min(Vector3.Distance(eyePos, liveEye), 0.5f);
            UpdateAutoFaceClip(eyePos, viewDir);
            ApplyFaceClip(); // same frame, so fast head motion never shows a frame of face
            dbgCameraPlaced++;
        }

        //----
        // Target focus: returns the direction to the target (relative yaw to
        // the body, and pitch) while the camera should be turning toward it
        //----
        private bool UpdateTargetFocus(Vector3 eye, float bodyYaw, out float relYaw, out float pitch)
        {
            relYaw = 0;
            pitch = 0;

            CSGameObject* target = Config.FocusOnTarget && !inGpose ? targetSystem->Target : null;
            if (target == null || (nint)target == localGameObject)
            {
                lastTarget = 0;
                focusTimeLeft = 0;
                focusBroken = false;
                recenterTime = 0;
                dbgFocus = "no target";
                return false;
            }

            if ((nint)target != lastTarget)
            {
                lastTarget = (nint)target;
                focusTimeLeft = FocusMaxTime;
                focusBroken = false;
                recenterTime = 0;
            }

            //----
            // The target went out of the head's range earlier (e.g. you walked
            // past it): it stays selected, but the view looks ahead and doesn't
            // go back to it until you select something again
            //----
            if (focusBroken)
            {
                if (recenterTime > 0)
                {
                    recenterTime -= frameDelta;
                    dbgFocus = "target out of range - looking ahead";
                    return true; // relYaw = 0, pitch = 0: straight ahead
                }
                dbgFocus = "target out of range (released until reselected)";
                return false;
            }
            else if (!Config.KeepTrackingTarget)
            {
                focusTimeLeft -= frameDelta;
            }

            if (focusTimeLeft <= 0 && !Config.KeepTrackingTarget)
            {
                dbgFocus = "target held (focus done)";
                return false;
            }

            Vector3 point = GetFocusPoint(target, eye);
            Vector3 dir = point - eye;
            if (dir.LengthSquared() < 1e-4f)
                return false;
            dir = Vector3.Normalize(dir);

            relYaw = WrapAngle(MathF.Atan2(dir.X, dir.Z) - bodyYaw);
            pitch = MathF.Asin(Math.Clamp(dir.Y, -1, 1));

            //----
            // A head can't look behind itself: ignore targets outside the range
            //----
            float maxYaw = (Config.LimitHeadTurn ? Config.HeadYawLimit : 90.0f) * Deg2Rad + 0.09f;
            if (MathF.Abs(relYaw) > maxYaw)
            {
                focusBroken = true;
                recenterTime = RecenterTime;
                relYaw = 0;
                pitch = 0;
                dbgFocus = "target out of range - looking ahead";
                return true;
            }

            dbgFocus = $"focusing ({focusTimeLeft:F2}s) yaw {relYaw / Deg2Rad:F0} pitch {pitch / Deg2Rad:F0} aim {dbgFocusPath} {dbgFocusAim:F2} m (obj height {target->Height:F2})";
            return true;
        }

        private Vector3 GetFocusPoint(CSGameObject* target, Vector3 eye)
        {
            dbgFocusPath = "-";

            //----
            // Characters: their head bone. Everything else: near the top of the
            // object
            //----
            CharacterBase* charBase = (CharacterBase*)target->DrawObject;
            if (charBase != null && charBase->Skeleton != null && charBase->Skeleton->PartialSkeletonCount > 0)
            {
                RenderSkeleton* skel = charBase->Skeleton;
                hkaPose* pose = skel->PartialSkeletons[0].GetHavokPose(0);
                if (pose != null && pose->Skeleton != null)
                {
                    BoneInfo info = GetBoneInfo(pose->Skeleton, true);
                    if (info.HeadIndex >= 0 && info.HeadIndex < pose->ModelPose.Length)
                    {
                        //----
                        // Only trust it if it's plausibly a head: near the
                        // target and a sensible height above its feet
                        //----
                        Vector3 head = ModelToWorld(skel, ToVector3(pose->ModelPose.Data[info.HeadIndex].Translation));
                        Vector3 feet = target->Position;
                        float dx = head.X - feet.X, dz = head.Z - feet.Z, dy = head.Y - feet.Y;
                        if (dx * dx + dz * dz < 1.5f * 1.5f && dy > 0.2f && dy < 4.0f)
                        {
                            dbgFocusAim = dy;
                            dbgFocusPath = "head bone";
                            return head;
                        }
                        dbgFocusPath = $"head bone rejected (dy {dy:F2})";
                    }
                }
            }
            //----
            // Everything else: use the target's actual shape (the world-space
            // box around its skeleton's bones), since an object's reported
            // position isn't always at its base. Look level at your own eye
            // height if that falls within it, otherwise at its nearest part.
            //----
            if (TryGetSkeletonBounds(target, out Vector3 min, out Vector3 max))
            {
                float h = max.Y - min.Y;
                float lo = min.Y + 0.15f * h, hi = max.Y - 0.1f * h;
                float aimY = Math.Clamp(eye.Y, lo, MathF.Max(lo, hi));
                dbgFocusPath = $"model bounds {min.Y:F2}..{max.Y:F2}";
                dbgFocusAim = aimY - min.Y;
                return new Vector3((min.X + max.X) * 0.5f, aimY, (min.Z + max.Z) * 0.5f);
            }

            //----
            // No skeleton: look level at your eye height, over its position
            //----
            Vector3 basePos = target->Position;
            dbgFocusPath = "eye level";
            dbgFocusAim = eye.Y - basePos.Y;
            return new Vector3(basePos.X, eye.Y, basePos.Z);
        }

        //----
        // Eyes pivot around the neck: rotate the neck->eye offset by the look
        // rotation relative to the body, so looking down brings the eyes
        // forward and down like a real head
        //----
        private Vector3 ComputeEye(Vector3 eye, Vector3 neck, Vector3 bodyFwd, Vector3 look)
        {
            Vector3 result = eye;
            if (Config.NeckPivot)
            {
                Vector3 lookH = new(look.X, 0, look.Z);
                if (lookH.LengthSquared() > 1e-6f)
                {
                    lookH = Vector3.Normalize(lookH);
                    Quaternion yaw = FromTo(bodyFwd, lookH);
                    Quaternion pitch = FromTo(lookH, look);
                    Quaternion rot = Quaternion.Concatenate(yaw, pitch); // yaw first, then pitch
                    result = neck + Vector3.Transform(eye - neck, rot);
                }
            }
            return result + Vector3.Normalize(new Vector3(look.X, 0, look.Z) + new Vector3(1e-6f, 0, 0)) * Config.EyeForwardOffset
                          + new Vector3(0.0f, Config.EyeHeightOffset, 0.0f);
        }

        //----
        // Math helpers
        //----
        //----
        // Keep a smoothed position within MaxEyeLag of the real one so the
        // camera can't fall behind the face (which the face clip relies on)
        //----
        private static Vector3 LimitLag(Vector3 smoothed, Vector3 raw)
        {
            Vector3 lag = smoothed - raw;
            float len = lag.Length();
            return len > MaxEyeLag ? raw + lag * (MaxEyeLag / len) : smoothed;
        }

        //----
        // Rotation taking direction 'from' to 'to' as yaw (about world up)
        // followed by pitch, so no roll is introduced
        //----
        private static Quaternion YawPitchChange(Vector3 from, Vector3 to)
        {
            Vector3 fromH = new(from.X, 0, from.Z);
            Vector3 toH = new(to.X, 0, to.Z);
            if (fromH.LengthSquared() < 1e-8f || toH.LengthSquared() < 1e-8f)
                return FromTo(Vector3.Normalize(from), Vector3.Normalize(to));
            Quaternion yaw = FromTo(Vector3.Normalize(fromH), Vector3.Normalize(toH));
            Vector3 yawed = Vector3.Normalize(Vector3.Transform(from, yaw));
            Quaternion pitch = FromTo(yawed, Vector3.Normalize(to));
            return Quaternion.Concatenate(yaw, pitch); // yaw first, then pitch
        }

        private static bool SameMatrix(in Matrix4x4 a, in Matrix4x4 b)
        {
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    if (MathF.Abs(a[r, c] - b[r, c]) > 1e-5f)
                        return false;
            return true;
        }

        private static Quaternion FromTo(Vector3 a, Vector3 b)
        {
            Vector3 axis = Vector3.Cross(a, b);
            float sin = axis.Length();
            float cos = Vector3.Dot(a, b);
            if (sin < 1e-6f)
                return cos > 0 ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
            return Quaternion.CreateFromAxisAngle(axis / sin, MathF.Atan2(sin, cos));
        }

        private static float WrapAngle(float a)
        {
            while (a > MathF.PI) a -= 2 * MathF.PI;
            while (a < -MathF.PI) a += 2 * MathF.PI;
            return a;
        }

        private static Vector3 ToVector3(FFXIVClientStructs.Havok.Common.Base.Math.Vector.hkVector4f v) => new(v.X, v.Y, v.Z);
        private static Quaternion ToQuaternion(FFXIVClientStructs.Havok.Common.Base.Math.Quaternion.hkQuaternionf q) => new(q.X, q.Y, q.Z, q.W);

        private static Matrix4x4 ToMatrix(in hkQsTransformf t)
        {
            return Matrix4x4.CreateScale(t.Scale.X, t.Scale.Y, t.Scale.Z)
                 * Matrix4x4.CreateFromQuaternion(ToQuaternion(t.Rotation))
                 * Matrix4x4.CreateTranslation(t.Translation.X, t.Translation.Y, t.Translation.Z);
        }

        private static Vector3 ModelToWorld(RenderSkeleton* skeleton, Vector3 model)
        {
            Vector3 pos = skeleton->Transform.Position;
            Quaternion rot = skeleton->Transform.Rotation;
            Vector3 scale = skeleton->Transform.Scale;
            return pos + Vector3.Transform(model * scale, rot);
        }

        //----
        // Debug panel + world markers
        //----
        private void DrawFirstPersonDebug()
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Live diagnostics for troubleshooting first person.");
            ImGui.Checkbox("Show diagnostics and head markers", ref showDebug);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Also draws markers on your character in third person:\ngreen = head, yellow = neck, red = eyes.");
            if (!showDebug)
                return;

            var label = new Vector4(0.6f, 0.8f, 1.0f, 1.0f);
            void Row(string name, string value)
            {
                ImGui.TextColored(label, name);
                ImGui.SameLine(110);
                ImGui.TextUnformatted(value);
            }

            ImGui.Spacing();
            ImGui.Separator();
            Row("State", SuspendReason != null ? $"{SuspendReason} (General tab, Experimental)" : firstPersonActive ? (inGpose ? "True first person active (group pose)" : "True first person active")
                : inGpose ? $"Inactive in group pose (zoom {dbgZoom:F2}, fully in at {dbgZoomMin:F2})" : $"Inactive (camera mode: {dbgControlMode})");
            Row("Skeleton", dbgStatus);
            Row("Eyes", $"{dbgEyeToHead * 100:F0} cm from head bone, via {dbgEyeMethod}");
            if (dbgError.Length > 0)
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"Error: {dbgError}");

            RenderCamera* rc = GetGameRenderCamera();
            Row("Camera", $"view correction {dbgView}");
            Row("Face clip", faceClipApplied ? $"{(rc != null ? rc->NearPlane * 100 : 0):F0} cm (automatic, {headPointCount} head points; camera {cameraEyeGap * 100:F0} cm from eyes)" : "off");
            Row("Look", $"{dbgRelYaw:F0}° from body, pitch {dbgPitch:F0}°");
            Row("Head", $"rotation {dbgHeadAngle:F0}°, follow {headFollowWeight * 100:F0}%{(inCombat ? ", in combat" : "")}{(bodyTurnHold > 0 ? ", body turning" : "")}");
            Row("Moving", $"{moveSpeed:F1} m/s (steadying {moveBlend * 100:F0}%)");
            Row("Targeting", dbgFocus);
            Row("Limits", $"turn rate {RateText(yawRate)}, tilt rate {RateText(pitchRate)}");

            ImGui.Spacing();
            if (ImGui.CollapsingHeader("Technical details"))
            {
                CSCameraManager* cm = CSCameraManager.Instance();
                ImGui.TextUnformatted($"enabled={isEnabled} cutscene={inCutscene} camIndex={(cm != null ? cm->ActiveCameraIndex : -1)}");
                ImGui.TextUnformatted($"group pose={inGpose} zoom={dbgZoom:F2} (min {dbgZoomMin:F2}) character={localGameObject:X} direct reads={dbgDirectReads}");
                ImGui.TextUnformatted($"group pose offset: pivot {Fmt(gposeOffset)}  external {Fmt(externalOffset)}");
                ImGui.TextUnformatted($"hooks: anim={UpdateAnimationsHook?.IsEnabled} cam={CameraUpdateHook?.IsEnabled}");
                ImGui.TextUnformatted($"characters un-hidden: start {visibilityFlips[0]}, mid {visibilityFlips[1]}, end {visibilityFlips[2]}");
                ImGui.TextUnformatted(WeaponSlotText());
                if (propHistory.Count > 0)
                    ImGui.TextUnformatted("prop history:\n  " + string.Join("\n  ", propHistory));
                ImGui.TextUnformatted($"draw vs skeleton position: {dbgDrawVsSkel * 100:F1} cm");
                ImGui.TextUnformatted($"calls: anim={dbgUpdateAnimCalls} processed={dbgProcessed} camUpd={dbgCameraUpdateCalls} placed={dbgCameraPlaced} late head={dbgStaleFrames}");
                ImGui.TextUnformatted($"bones: j_kao={dbgHeadIndex} j_kubi={dbgNeckIndex} partials={dbgPartials} eyes via {dbgConnect}");
                ImGui.TextWrapped($"eye bones: {dbgEyeBones}");
                ImGui.TextUnformatted($"head  {Fmt(dbgHeadWorld)}");
                ImGui.TextUnformatted($"neck  {Fmt(dbgNeckWorld)}");
                ImGui.TextUnformatted($"eyes  {Fmt(dbgEyeWorld)}");
                ImGui.TextUnformatted($"game camera {Fmt(dbgCamBefore)}");
                ImGui.TextUnformatted($"our camera  {Fmt(dbgCamAfter)}");
            }

            ImGui.Spacing();
            if (ImGui.Button("Copy diagnostics"))
                ImGui.SetClipboardText(
                    $"active={firstPersonActive} mode={dbgControlMode} status={dbgStatus} err={dbgError}\n" +
                    WeaponSlotText() + "\nprop history:\n  " + string.Join("\n  ", propHistory) + "\n" +
                    $"gpose={inGpose} zoom={dbgZoom:F2}/{dbgZoomMin:F2} camIndex={(CSCameraManager.Instance() != null ? CSCameraManager.Instance()->ActiveCameraIndex : -1)} directReads={dbgDirectReads} pivotOff={Fmt(gposeOffset)} extOff={Fmt(externalOffset)}\n" +
                    $"view={dbgView} faceClip={faceClipApplied} look={dbgRelYaw:F1}/{dbgPitch:F1} headRot={dbgHeadAngle:F1} follow={headFollowWeight:F2} combat={inCombat} bodyTurning={bodyTurnHold > 0}\n" +
                    $"focus={dbgFocus}\nrates={yawRate.Rate:F2}{(yawRate.Confident ? "" : "?")}/{pitchRate.Rate:F2}{(pitchRate.Confident ? "" : "?")}\n" +
                    $"calls anim={dbgUpdateAnimCalls} proc={dbgProcessed} cam={dbgCameraUpdateCalls} placed={dbgCameraPlaced} kao={dbgHeadIndex} kubi={dbgNeckIndex} map={dbgConnect}\n" +
                    $"head={Fmt(dbgHeadWorld)} neck={Fmt(dbgNeckWorld)} eye={Fmt(dbgEyeWorld)} game={Fmt(dbgCamBefore)} ours={Fmt(dbgCamAfter)}");
        }

        //----
        // The camera's own angles map 1:1 onto the look direction (measured
        // repeatedly as 1.00), so steer with that from the start; the
        // measurement is only used to refine it once it's confident.
        //----
        private static float SteerRate(AngleRate rate) => rate.Confident ? rate.Rate : 1.0f;

        private static string RateText(AngleRate rate) => rate.Confident ? $"{rate.Rate:F2}" : "measuring…";

        //----
        // State of the three weapon slots (main hand, off hand, emote prop)
        //----
        // Recent changes to the emote prop slot, to see how the game hides it
        private long propStateClears = 0;
        private readonly List<string> propHistory = new();
        private string lastPropState = "";
        private readonly System.Diagnostics.Stopwatch propClock = System.Diagnostics.Stopwatch.StartNew();

        private void RecordPropState()
        {
            nint gameObject = ObjectTable!.LocalPlayer?.Address ?? 0;
            if (gameObject == 0)
                return;
            var drawData = &((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)gameObject)->DrawData;
            ref var slot = ref drawData->Weapon(FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.WeaponSlot.System);
            var model = (Structures.Model*)slot.DrawData.DrawObject;
            string state = $"id={slot.ModelId.Id} model={(model != null ? $"{(byte)model->CullType:X2}" : "none")} state={slot.State:X2} f1={slot.Flags1:X4} f2={slot.Flags2:X2} hideWeapons={drawData->IsWeaponHidden} fp={firstPersonActive}";
            if (state == lastPropState)
                return;
            lastPropState = state;
            propHistory.Add($"{propClock.Elapsed.TotalSeconds,7:F2}s {state}");
            if (propHistory.Count > 16)
                propHistory.RemoveAt(0);
        }

        private string WeaponSlotText()
        {
            nint gameObject = localGameObject;
            if (gameObject == 0)
                gameObject = ObjectTable!.LocalPlayer?.Address ?? 0;
            if (gameObject == 0)
                return "weapons: no character";
            var drawData = &((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)gameObject)->DrawData;
            string text = $"weapons hidden={drawData->IsWeaponHidden} prop state cleared {propStateClears} times";
            string[] names = { "main", "off", "prop" };
            for (int i = 0; i < 3; i++)
            {
                ref var slot = ref drawData->Weapon((FFXIVClientStructs.FFXIV.Client.Game.Character.DrawDataContainer.WeaponSlot)i);
                var model = (Structures.Model*)slot.DrawData.DrawObject;
                text += $"\n  {names[i]}: id={slot.ModelId.Id} model={(model != null ? $"flags {(byte)model->CullType:X2}" : "none")} state={slot.State:X2} f1={slot.Flags1:X4} f2={slot.Flags2:X2}";
            }
            return text;
        }

        private static string Fmt(Vector3 v) => $"{v.X,8:F3} {v.Y,8:F3} {v.Z,8:F3}";

        private void DrawFirstPersonMarkers()
        {
            //----
            // Green = head, yellow = neck pivot, red = eyes (useful in third person)
            //----
            if (!showDebug || dbgProcessed == 0)
                return;
            var drawList = ImGui.GetForegroundDrawList();
            if (GameGui!.WorldToScreen(dbgHeadWorld, out Vector2 headScreen))
                drawList.AddCircleFilled(headScreen, 6, 0xFF00FF00);
            if (GameGui!.WorldToScreen(dbgNeckWorld, out Vector2 neckScreen))
                drawList.AddCircleFilled(neckScreen, 5, 0xFF00FFFF);
            if (GameGui!.WorldToScreen(dbgEyeWorld, out Vector2 eyeScreen))
                drawList.AddCircleFilled(eyeScreen, 4, 0xFF0000FF);
        }
    }
}
