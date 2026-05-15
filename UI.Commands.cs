// Commands & Prefills UI (rows, table, apply)
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UITK = UnityEngine.UIElements;

namespace PoncePuck.Keybinds
{
    public sealed partial class KeybindRunner
    {
        private class CmdRow { public UITK.TextField text; public List<string> chords = new List<string>(); public UITK.VisualElement chips; public UITK.VisualElement row; }
        private class PrefRow { public UITK.TextField text; public List<string> chords = new List<string>(); public UITK.VisualElement chips; public UITK.VisualElement row; }

        private readonly List<CmdRow> _cmdRows = new List<CmdRow>();
        private readonly List<PrefRow> _prefRows = new List<PrefRow>();

        private UITK.VisualElement MakeCommandsTable()
        {
            var wrap = new UITK.VisualElement();
            var head = new UITK.Label("COMMANDS & PREFILLS"); MakeReadable(head);
            head.style.unityFontStyleAndWeight = FontStyle.Normal;
            head.style.fontSize = 30;
            head.style.marginBottom = 10;
            wrap.Add(head);

            var secCmdLbl = new UITK.Label("EXTRA COMMANDS"); MakeReadable(secCmdLbl);
            secCmdLbl.style.fontSize = 24; secCmdLbl.style.opacity = 0.85f; secCmdLbl.style.marginBottom = 4;
            wrap.Add(secCmdLbl);

            _cmdRowsRoot = new UITK.VisualElement(); wrap.Add(_cmdRowsRoot);

            var addCmd = new UITK.Button(() => AddCommandRow("/", new List<string>())) { text = "ADD NEW COMMAND ROW" };
            addCmd.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            addCmd.style.height = 50;
            addCmd.style.marginBottom = 22;
            addCmd.style.paddingLeft = 8; addCmd.style.paddingRight = 8;
            addCmd.style.backgroundColor = BtnBrightGray;
            AddButtonFlash(addCmd);
            wrap.Add(addCmd);

            var secPrefLbl = new UITK.Label("PREFILLS"); MakeReadable(secPrefLbl);
            secPrefLbl.style.unityFontStyleAndWeight = FontStyle.Normal;
            secPrefLbl.style.fontSize = 24;
            secPrefLbl.style.marginTop = 4;
            wrap.Add(secPrefLbl);

            _prefRowsRoot = new UITK.VisualElement(); wrap.Add(_prefRowsRoot);

            var addPref = new UITK.Button(() => AddPrefillRow("/", new List<string>())) { text = "ADD NEW PRE-FILL ROW" };
            addPref.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            addPref.style.height = 50;
            addPref.style.marginBottom = 22;
            addPref.style.paddingLeft = 8; addPref.style.paddingRight = 8;
            addPref.style.backgroundColor = BtnBrightGray;
            AddButtonFlash(addPref);
            wrap.Add(addPref);

            return wrap;
        }

        private void RefreshPanelCommandFields()
        {
            _cmdRows.Clear();
            _prefRows.Clear();
            _cmdRowsRoot?.Clear();
            _prefRowsRoot?.Clear();

            for (int i = 0; i < _cmd.extraCommands.Count; i++)
            {
                if (ParseCommandEntryMulti(_cmd.extraCommands[i], out var cc, out var specs))
                    AddCommandRow(cc, specs);
            }

            for (int i = 0; i < _cmd.prefills.Count; i++)
            {
                if (ParsePrefillEntryMulti(_cmd.prefills[i], out var txt, out var specs))
                    AddPrefillRow(txt, specs);
            }
        }

        private void AddPrefillRow(string text, List<string> chordSpecs)
        {
            var model = new PrefRow { chords = new List<string>(chordSpecs ?? new List<string>()) };

            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 10;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12; row.style.paddingRight = 10;
            row.style.paddingTop = 8; row.style.paddingBottom = 8;

            var left = new UITK.TextField { value = text };
            StyleTextField(left);
            row.Add(left);

            var remove = new UITK.Button(() => { row.RemoveFromHierarchy(); _prefRows.Remove(model); });
            StyleRowButton(remove, BTN_W_RM, "REMOVE");
            remove.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            row.Add(remove);

            var chipsRoot = new UITK.VisualElement();
            chipsRoot.style.flexDirection = UITK.FlexDirection.Row;
            chipsRoot.style.justifyContent = UITK.Justify.FlexEnd;
            chipsRoot.style.alignItems = UITK.Align.Center;
            chipsRoot.style.flexGrow = 1;
            chipsRoot.style.flexShrink = 1;
            chipsRoot.style.minWidth = 0;
            chipsRoot.style.marginLeft = 4;
            chipsRoot.style.marginTop = 4;
            chipsRoot.style.marginRight = GAP;
            row.Add(chipsRoot);
            model.chips = chipsRoot;

            var right = new UITK.VisualElement();
            right.style.flexDirection = UITK.FlexDirection.Row;
            right.style.alignItems = UITK.Align.Center;
            right.style.flexShrink = 0;
            row.Add(right);

            var bind = new UITK.Button(() =>
            {
                StartChordCapture("Press keys for prefill", spec =>
                {
                    if (!SpecValid(spec)) return;
                    if (!ListContainsSpecEquivalent(model.chords, spec))
                    {
                        model.chords.Add(spec);
                        RefreshChips();
                    }
                });
            });
            StyleRowButton(bind, BTN_W, "BIND");
            bind.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            right.Add(bind);

            void RefreshChips()
            {
                chipsRoot.Clear();
                for (int i = 0; i < model.chords.Count; i++)
                {
                    var idx = i;
                    chipsRoot.Add(MakeChip(model.chords[i], () =>
                    {
                        if (idx >= 0 && idx < model.chords.Count)
                        {
                            model.chords.RemoveAt(idx);
                            RefreshChips();
                        }
                    }));
                }
            }
            RefreshChips();

            model.text = left; model.row = row;
            _prefRows.Add(model);
            _prefRowsRoot.Add(row);
        }

        private void AddCommandRow(string cmd, List<string> chordSpecs)
        {
            var model = new CmdRow { chords = new List<string>(chordSpecs ?? new List<string>()) };

            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 10;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12; row.style.paddingRight = 10;
            row.style.paddingTop = 8; row.style.paddingBottom = 8;

            var left = new UITK.TextField { value = cmd };
            StyleTextField(left);
            row.Add(left);

            var remove = new UITK.Button(() => { row.RemoveFromHierarchy(); _cmdRows.Remove(model); });
            StyleRowButton(remove, BTN_W_RM, "REMOVE");
            remove.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            row.Add(remove);

            var chipsRoot = new UITK.VisualElement();
            chipsRoot.style.flexDirection = UITK.FlexDirection.Row;
            chipsRoot.style.justifyContent = UITK.Justify.FlexEnd;
            chipsRoot.style.alignItems = UITK.Align.Center;
            chipsRoot.style.flexGrow = 1;
            chipsRoot.style.flexShrink = 1;
            chipsRoot.style.minWidth = 0;
            chipsRoot.style.marginLeft = 4;
            chipsRoot.style.marginTop = 4;
            chipsRoot.style.marginRight = 8;
            row.Add(chipsRoot);
            model.chips = chipsRoot;

            var right = new UITK.VisualElement();
            right.style.flexDirection = UITK.FlexDirection.Row;
            right.style.alignItems = UITK.Align.Center;
            right.style.flexShrink = 0;
            row.Add(right);

            var bind = new UITK.Button(() =>
            {
                StartChordCapture("Press keys for command", spec =>
                {
                    if (!SpecValid(spec)) return;
                    if (!ListContainsSpecEquivalent(model.chords, spec))
                    {
                        model.chords.Add(spec);
                        RefreshChips();
                    }
                });
            });
            StyleRowButton(bind, BTN_W, "BIND");
            bind.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            right.Add(bind);

            void RefreshChips()
            {
                chipsRoot.Clear();
                for (int i = 0; i < model.chords.Count; i++)
                {
                    var idx = i;
                    chipsRoot.Add(MakeChip(model.chords[i], () =>
                    {
                        if (idx >= 0 && idx < model.chords.Count)
                        {
                            model.chords.RemoveAt(idx);
                            RefreshChips();
                        }
                    }));
                }
            }
            RefreshChips();

            model.text = left; model.row = row;
            _cmdRows.Add(model);
            _cmdRowsRoot.Add(row);
        }

        private static string JoinComma(List<string> list) => (list == null || list.Count == 0) ? "" : string.Join(", ", list);

        partial void RefreshPanelFields();

        private bool TryApplyFromPanel()
        {
            try
            {
                _cmd.extraCommands = new List<string>();
                foreach (var row in _cmdRows.ToList())
                {
                    var cmd = row.text?.value?.Trim();
                    if (string.IsNullOrEmpty(cmd)) continue;
                    if (!cmd.StartsWith("/")) cmd = "/" + cmd.TrimStart('/');
                    
                    // Strip any :key suffix if user typed it in the text field
                    // Keybinds should ONLY come from the BIND button, not typed text
                    int colonIdx = cmd.LastIndexOf(':');
                    if (colonIdx > 0 && colonIdx < cmd.Length - 1)
                    {
                        // Check if everything after : looks like keybinds (single letters, Ctrl+X, etc)
                        var afterColon = cmd.Substring(colonIdx + 1).Trim();
                        bool looksLikeKeybinds = afterColon.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .All(k => k.Trim().Length <= 10 && !k.Contains(" "));
                        if (looksLikeKeybinds)
                        {
                            cmd = cmd.Substring(0, colonIdx).Trim();
                        }
                    }
                    
                    var csv = JoinComma(row.chords);
                    if (!string.IsNullOrEmpty(csv)) _cmd.extraCommands.Add(cmd + ":" + csv);
                }

                _cmd.prefills = new List<string>();
                foreach (var row in _prefRows.ToList())
                {
                    var text = (row.text?.value ?? "");
                    if (!text.EndsWith(" ")) text += " ";
                    var csv = JoinComma(row.chords);
                    if (!string.IsNullOrEmpty(csv)) _cmd.prefills.Add(text + "|" + csv);
                }

                // Save position settings from rows.
                //
                // Value capture is defensive on purpose: numeric rows (slider + field
                // pair) read BOTH widgets and keep whichever parses as a float. A
                // historical bug let Minimap Vertical save as empty because the slider
                // sometimes reported 0 / NaN while the field showed the typed value
                // — the empty-string check below then dropped the entry entirely.
                _cmd.positionSettings.Clear();
                Debug.Log($"[PPKB] Saving position settings - row count: {_posSettingRows.Count}");
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (var row in _posSettingRows.ToList())
                {
                    var pos = row.position;
                    var dropdownDisplay = row.settingDropdown?.value ?? "FOV";
                    var st = StringToSettingType(dropdownDisplay);

                    string val = "";
                    if (row.valueDropdown != null)
                    {
                        // Enum-style controls (Handedness): dropdown carries the value verbatim.
                        val = row.valueDropdown.value ?? "";
                    }
                    else
                    {
                        // Numeric controls: read slider value, but fall back to the text
                        // field if the slider reports a value that diverges from what
                        // the user sees in the field (this happens when the field's
                        // typed value parsed but the slider's SetValueWithoutNotify
                        // didn't take, or vice-versa). Use the field whenever it parses
                        // and differs from the slider's stringified value.
                        float? sliderVal = null;
                        string sliderStr = null;
                        if (row.valueSlider != null)
                        {
                            sliderVal = row.valueSlider.value;
                            sliderStr = sliderVal.Value.ToString(inv);
                        }
                        string fieldStr = row.valueField?.value;
                        bool fieldParses = !string.IsNullOrEmpty(fieldStr)
                            && float.TryParse(fieldStr, System.Globalization.NumberStyles.Float, inv, out float fieldF);

                        if (sliderVal.HasValue && (!fieldParses || sliderStr == fieldStr))
                        {
                            val = sliderStr;
                        }
                        else if (fieldParses)
                        {
                            val = fieldStr;
                        }
                        else if (sliderVal.HasValue)
                        {
                            val = sliderStr;
                        }
                        else if (!string.IsNullOrEmpty(fieldStr))
                        {
                            val = fieldStr;
                        }
                    }

                    Debug.Log($"[PPKB] Processing row: pos={pos}, dropdown=\"{dropdownDisplay}\", type={st}, " +
                              $"slider={(row.valueSlider != null ? row.valueSlider.value.ToString(inv) : "null")}, " +
                              $"field=\"{row.valueField?.value ?? "null"}\", picked=\"{val}\"");

                    // Allow "0" explicitly — `string.IsNullOrEmpty("0")` is already false,
                    // but the comment is here because Minimap Vertical's default value
                    // IS "0" (top of screen) and we want it to round-trip.
                    if (pos != HockeyPosition.None && !string.IsNullOrEmpty(val))
                    {
                        _cmd.positionSettings.Add(new PositionSettingEntry
                        {
                            position = pos,
                            settingType = st,
                            value = val
                        });
                        Debug.Log($"[PPKB] Added position setting: {pos} {st} = {val}");
                    }
                    else
                    {
                        Debug.LogWarning($"[PPKB] DROPPED row: pos={pos}, type={st}, val=\"{val}\" " +
                                         $"(slider={row.valueSlider?.value.ToString(inv) ?? "null"}, " +
                                         $"field=\"{row.valueField?.value ?? "null"}\", " +
                                         $"dropdown=\"{row.valueDropdown?.value ?? "null"}\")");
                    }
                }
                Debug.Log($"[PPKB] Total position settings to save: {_cmd.positionSettings.Count}");

                return true;
            }
            catch (Exception e) { Debug.LogException(e); SafeEcho("Error applying UI changes: " + e.Message); return false; }
        }

        private void ResetToDefaultsUI()
        {
            _cmd = new CommandKeybindConfig
            {
                extraCommands = new List<string> { "/vs:J", "/vw:K", "/s:G" },
                prefills = new List<string> { "/w |U" },

                // Audio settings
                enableSounds = false,
                musicvol = 0.5f,
                warmupmusic = true,

                // Visual settings
                enableChatRichText = true,
                disableArenaVisuals = false,

                // Customization settings
                enableDonor = false,
                enableColor = false,
                enableRichText = true,
                colorHex = "#FFFFFF",
                enablePanelCustomization = true,
                panelAlpha = 1f,
                _panelHueSlider = 0f,
                _panelSaturationSlider = 0f,
                _panelBrightnessSlider = 0.20f,

                // Gameplay settings
                enableRecentPlayers = true,
                enableLiveLogging = true,

                mentionSoundEnabled = true,
                mentionSoundVolume = 0.5f,
                selectedMentionSound = "MentionPingDefault.mp3",
                // Admin settings
                enableAdminSettings = false
            };
        }
    }
}
