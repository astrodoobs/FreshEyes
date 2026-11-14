using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace StopTheClip
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;

        //----
        // True first person: the camera is pinned to the character's eyes every
        // frame (so it follows emotes/animations) and the head is hidden
        //----
        public bool FirstPersonFix { get; set; } = true;
        public bool GposeFirstPerson { get; set; } = true;   // zoomed fully in during group pose counts as first person
        public bool HideHead { get; set; } = true;
        public float FaceClipDistance { get; set; } = 0.15f;  // near clip used in true first person
        public float EyeForwardOffset { get; set; } = 0.0f;
        public float EyeHeightOffset { get; set; } = 0.0f;

        //----
        // Head behaviour in first person
        //----
        public float HeadFollowStrength { get; set; } = 1.0f;   // 0 = ignore head animation/look-at rotation
        public bool IgnoreHeadInCombat { get; set; } = true;
        public float TargetHeadFollow { get; set; } = 0.35f;
        public float MovingHeadFollow { get; set; } = 0.15f;   // head motion strength while walking/running   // head motion strength while tracking a target   // fade out head motion while in combat
        public float HeadSmoothing { get; set; } = 0.5f;        // 0 = raw, 1 = heavily smoothed head motion
        public bool NeckPivot { get; set; } = true;             // eyes rotate around the neck when looking around
        public bool LimitHeadTurn { get; set; } = true;
        public float HeadYawLimit { get; set; } = 85.0f;        // degrees left/right of the body
        public float HeadPitchUpLimit { get; set; } = 65.0f;
        public float HeadPitchDownLimit { get; set; } = 75.0f;

        //----
        // Turn to face a newly selected target, like the character's head does
        //----
        public bool FocusOnTarget { get; set; } = true;
        public bool KeepTrackingTarget { get; set; } = false;
        public float FocusSpeed { get; set; } = 8.0f;

        // the below exist just to make saving less cumbersome
        [NonSerialized]
        private IDalamudPluginInterface? iPluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.iPluginInterface = pluginInterface;
        }

        public void Save()
        {
            this.iPluginInterface!.SavePluginConfig(this);
        }
    }
}
