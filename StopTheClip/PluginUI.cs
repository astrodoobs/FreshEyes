using Dalamud.Bindings.ImGui;
using System;
using System.Numerics;

namespace StopTheClip
{
    public static class PluginUI
    {
        public static bool isVisible = false;

        public static void Draw(ref bool isEnabled, Configuration config, Action drawDebug)
        {
            if (!isVisible)
                return;

            ImGui.SetNextWindowSize(new Vector2(380, 440), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSizeConstraints(new Vector2(330, 260), new Vector2(9999));

            if (ImGui.Begin("Fresh Set of Eyes", ref isVisible))
            {
                if (ImGui.BeginTabBar("##stcTabs"))
                {
                    if (ImGui.BeginTabItem("General"))
                    {
                        DrawGeneral(ref isEnabled, config);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("First Person"))
                    {
                        DrawFirstPerson(isEnabled, config);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("Head & Targeting"))
                    {
                        DrawHead(isEnabled, config);
                        ImGui.EndTabItem();
                    }
                    if (ImGui.BeginTabItem("Debug"))
                    {
                        drawDebug();
                        ImGui.EndTabItem();
                    }
                    ImGui.EndTabBar();
                }
            }
            ImGui.End();
        }

        //----
        // Tabs
        //----
        private static void DrawGeneral(ref bool isEnabled, Configuration config)
        {
            ImGui.Spacing();
            ImGui.Checkbox("Enable Fresh Set of Eyes", ref isEnabled);
            HelpMarker("Lets the camera get close without models disappearing,\nand lowers the near clip so close-up shots aren't cut off.");

            ImGui.Spacing();
            Section("First person");
            ImGui.BeginDisabled(!isEnabled);
            Checkbox("True first person", config.FirstPersonFix, v => config.FirstPersonFix = v, config,
                "When the game camera is in first person, look out of your\ncharacter's eyes: the view follows every animation and\nyou can see your own body.");
            ImGui.BeginDisabled(!config.FirstPersonFix);
            Checkbox("Also in group pose", config.GposeFirstPerson, v => config.GposeFirstPerson = v, config,
                "In group pose, zooming all the way in looks out of the\neyes of the character the camera is centred on.\nUntick to keep the normal close-up camera there.");
            ImGui.EndDisabled();
            ImGui.EndDisabled();

            ImGui.Spacing();
            ImGui.TextDisabled("Zoom all the way in to enter first person.");
            if (!isEnabled)
                ImGui.TextDisabled("Fresh Set of Eyes is disabled.");
        }

        private static void DrawFirstPerson(bool isEnabled, Configuration config)
        {
            ImGui.Spacing();
            if (!FeatureNotice(isEnabled, config))
                ImGui.BeginDisabled();

            Section("View");
            Checkbox("Hide my face", config.HideHead, v => config.HideHead = v, config,
                "Stops your own face, lashes and hair blocking the view.\nThe amount hidden adjusts automatically to your\ncharacter and where you're looking.");
            Checkbox("Move eyes with head tilt", config.NeckPivot, v => config.NeckPivot = v, config,
                "Your eyes swing around your neck as you look around,\nso looking down brings them forward over your body\ninstead of just tilting the camera.");

            ImGui.Spacing();
            if (ImGui.CollapsingHeader("Fine tuning"))
            {
                ImGui.TextDisabled("The camera is placed at your character's eyes\nautomatically. These are normally left at 0.");
                SliderCm("Forward / back", config.EyeForwardOffset, v => config.EyeForwardOffset = v, -20, 30, config,
                    "Moves the camera forward (+) or back (-) from your eyes.");
                SliderCm("Up / down", config.EyeHeightOffset, v => config.EyeHeightOffset = v, -20, 20, config,
                    "Moves the camera up (+) or down (-) from your eyes.");
            }

            ImGui.Spacing();
            if (ImGui.Button("Reset to defaults##fp"))
            {
                var d = new Configuration();
                config.HideHead = d.HideHead;
                config.EyeForwardOffset = d.EyeForwardOffset;
                config.EyeHeightOffset = d.EyeHeightOffset;
                config.NeckPivot = d.NeckPivot;
                config.Save();
            }

            if (!(isEnabled && config.FirstPersonFix))
                ImGui.EndDisabled();
        }

        private static void DrawHead(bool isEnabled, Configuration config)
        {
            ImGui.Spacing();
            if (!FeatureNotice(isEnabled, config))
                ImGui.BeginDisabled();

            Section("Head movement");
            SliderPercent("Follow head motion", config.HeadFollowStrength, v => config.HeadFollowStrength = v, config,
                "How much the view turns when your character's head\nmoves on its own (emotes, idle animations).\n0% = only the mouse turns the view.");

            SliderPercent("Head motion while moving", config.MovingHeadFollow, v => config.MovingHeadFollow = v, config,
                "How much head motion comes through while walking or\nrunning. Real eyes stay steady as you move, so a low\nvalue stops the view swaying with every stride.");

            ImGui.BeginDisabled(!config.FocusOnTarget);
            SliderPercent("Head motion on target", config.TargetHeadFollow, v => config.TargetHeadFollow = v, config,
                "How much head motion comes through while you're\nlooking at a target. Lower keeps the target steadier\nin view; it's also smoothed and limited to 10 degrees.");
            ImGui.EndDisabled();

            Checkbox("Ignore head motion in combat", config.IgnoreHeadInCombat, v => config.IgnoreHeadInCombat = v, config,
                "Combat animations swing the head hard; this keeps the\nview steady while you're fighting.");

            SliderPercent("Smooth head motion", config.HeadSmoothing, v => config.HeadSmoothing = v, config,
                "Softens head bob and quick head movements from\nanimations. 0% = raw, higher = smoother but a little\nslower to follow.");

            Checkbox("Limit to natural head range", config.LimitHeadTurn, v => config.LimitHeadTurn = v, config,
                "Stops the view turning further than a real head could\nrelative to your body.");
            ImGui.BeginDisabled(!config.LimitHeadTurn);
            SliderDeg("Turn left / right", config.HeadYawLimit, v => config.HeadYawLimit = v, 30, 180, config, null);
            SliderDeg("Look up", config.HeadPitchUpLimit, v => config.HeadPitchUpLimit = v, 20, 89, config, null);
            SliderDeg("Look down", config.HeadPitchDownLimit, v => config.HeadPitchDownLimit = v, 20, 89, config, null);
            ImGui.EndDisabled();

            Section("Targeting");
            Checkbox("Look at what I select", config.FocusOnTarget, v => config.FocusOnTarget = v, config,
                "When you select something, your view turns to face it,\nthe way your character's head does.");
            ImGui.BeginDisabled(!config.FocusOnTarget);
            Checkbox("Keep looking while selected", config.KeepTrackingTarget, v => config.KeepTrackingTarget = v, config,
                "Keep the view on your target for as long as it's\nselected, instead of only turning to it once. If it\nmoves out of your head's range (you walk past it), it\nstays selected but you look ahead again.");
            Slider("Turn speed", config.FocusSpeed, v => config.FocusSpeed = v, 1, 20, "%.0f", config,
                "How quickly the view turns to a new target.");
            ImGui.EndDisabled();

            ImGui.Spacing();
            if (ImGui.Button("Reset to defaults##head"))
            {
                var d = new Configuration();
                config.HeadFollowStrength = d.HeadFollowStrength;
                config.HeadSmoothing = d.HeadSmoothing;
                config.IgnoreHeadInCombat = d.IgnoreHeadInCombat;
                config.TargetHeadFollow = d.TargetHeadFollow;
                config.MovingHeadFollow = d.MovingHeadFollow;
                config.LimitHeadTurn = d.LimitHeadTurn;
                config.HeadYawLimit = d.HeadYawLimit;
                config.HeadPitchUpLimit = d.HeadPitchUpLimit;
                config.HeadPitchDownLimit = d.HeadPitchDownLimit;
                config.FocusOnTarget = d.FocusOnTarget;
                config.KeepTrackingTarget = d.KeepTrackingTarget;
                config.FocusSpeed = d.FocusSpeed;
                config.Save();
            }

            if (!(isEnabled && config.FirstPersonFix))
                ImGui.EndDisabled();
        }

        //----
        // Shown at the top of the first person tabs when they have no effect.
        // Returns true when the settings are live.
        //----
        private static bool FeatureNotice(bool isEnabled, Configuration config)
        {
            if (!isEnabled)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.3f, 1.0f), "Fresh Set of Eyes is disabled (General tab).");
                ImGui.Spacing();
                return false;
            }
            if (!config.FirstPersonFix)
            {
                ImGui.TextColored(new Vector4(1.0f, 0.75f, 0.3f, 1.0f), "True first person is off (General tab).");
                ImGui.Spacing();
                return false;
            }
            return true;
        }

        //----
        // Widgets
        //----
        private static void Section(string title)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1.0f, 1.0f), title);
            ImGui.Separator();
        }

        private static void HelpMarker(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(text);
        }

        private static void Checkbox(string label, bool value, Action<bool> set, Configuration config, string? help)
        {
            if (ImGui.Checkbox(label, ref value))
            {
                set(value);
                config.Save();
            }
            HelpMarker(help);
        }

        private static void Slider(string label, float value, Action<float> set, float min, float max, string format, Configuration config, string? help)
        {
            ImGui.TextUnformatted(label);
            HelpMarker(help);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.SliderFloat($"##{label}", ref value, min, max, format))
                set(value);
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
        }

        // Stored in metres, shown in centimetres
        private static void SliderCm(string label, float metres, Action<float> set, float minCm, float maxCm, Configuration config, string? help)
            => Slider(label, metres * 100.0f, v => set(v / 100.0f), minCm, maxCm, "%.0f cm", config, help);

        private static void SliderDeg(string label, float degrees, Action<float> set, float min, float max, Configuration config, string? help)
            => Slider(label, degrees, set, min, max, "%.0f°", config, help);

        // Stored as 0..1, shown as a percentage
        private static void SliderPercent(string label, float value, Action<float> set, Configuration config, string? help)
            => Slider(label, value * 100.0f, v => set(v / 100.0f), 0, 100, "%.0f%%", config, help);
    }
}
