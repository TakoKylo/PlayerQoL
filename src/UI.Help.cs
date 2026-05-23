// KeybindRunner.UI.Settings.cs
using System;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UITK = UnityEngine.UIElements;
using UnityEngine.UIElements;

namespace PoncePuck.Keybinds
{
    public sealed partial class KeybindRunner
    {
        // SETTINGS refs
        private UITK.Slider _settingsMusicVolSlider;
        private UITK.Label _settingsMusicVolVal;

#pragma warning disable CS0649, CS0169 // Fields assigned via out parameters and used in callbacks
        private UITK.Slider _panelAlphaSlider;
        private UITK.Toggle _warmupMusicToggle;

        // Panel color controls
        private UITK.TextField _panelColorHexField;
        private UITK.Slider _panelBrightnessSlider;
        private UITK.Slider _panelHueSlider;
        private UITK.Slider _panelSaturationSlider;
        private UITK.VisualElement _panelColorSwatch;
        private float _panelH, _panelS, _panelV;
#pragma warning restore CS0649, CS0169



        // ---------- helpers ----------
        private static string ColorToHex(Color c)
        {
            int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
            int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
            int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
        private static bool TryParseHexColor(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 6) return false;
            if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            {
                int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
                c = new Color(r / 255f, g / 255f, b / 255f, 1f);
                return true;
            }
            return false;
        }
        private static string SanitizeHex(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "#FFFFFF";
            s = s.Trim().ToUpperInvariant();
            if (!s.StartsWith("#")) s = "#" + s;
            return (s.Length == 7) ? s : "#FFFFFF";
        }

        // ---- Slider styling to look like base-game (single rail + one thumb) ----
        private UITK.VisualElement MakeHexColorRow(string label, string startHex, out UITK.VisualElement colorSwatch, out UITK.TextField hexField)
        {
            var row = new UITK.VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems    = Align.Center,
                    height        = 50,
                    marginBottom  = 8,
                    backgroundColor = new StyleColor(new Color32(61,61,61,255)),
                    paddingLeft   = 12,
                    paddingRight  = 12
                }
            };

            var lab = new UITK.Label(label); MakeReadable(lab);
            lab.style.minWidth = 300; lab.style.maxWidth = 300; row.Add(lab);

            // Color swatch
            var localSwatch = new UITK.VisualElement();
            localSwatch.style.width = 32; localSwatch.style.height = 32;
            localSwatch.style.marginLeft = 8; localSwatch.style.marginRight = 8;
            
            if (TryParseHexColor(startHex, out var startColor))
                localSwatch.style.backgroundColor = new StyleColor(startColor);
            else
                localSwatch.style.backgroundColor = new StyleColor(Color.white);
                
            localSwatch.style.borderTopLeftRadius = 6;
            localSwatch.style.borderTopRightRadius = 6;
            localSwatch.style.borderBottomLeftRadius = 6;
            localSwatch.style.borderBottomRightRadius = 6;
            row.Add(localSwatch);
            colorSwatch = localSwatch; // Set the out parameter

            // Hex text field
            var localHexField = new UITK.TextField { value = SanitizeHex(startHex) };
            MakeReadable(localHexField);
            var input = localHexField.childCount > 0 ? localHexField.ElementAt(0) : null;
            if (input != null) { input.style.whiteSpace = WhiteSpace.NoWrap; input.style.overflow = Overflow.Hidden; }
            localHexField.style.minWidth = 110; localHexField.style.maxWidth = 110;
            localHexField.style.unityTextAlign = TextAnchor.MiddleLeft;
            row.Add(localHexField);
            hexField = localHexField; // Set the out parameter

            // Update swatch when hex changes
            localHexField.RegisterValueChangedCallback(ev =>
            {
                if (TryParseHexColor(ev.newValue, out var newColor))
                {
                    localSwatch.style.backgroundColor = new StyleColor(newColor);
                }
            });

            return row;
        }

        private static void StyleSliderLikeBase(UITK.Slider s)
        {
            s.style.height = 26;
            s.style.marginLeft = 6;
            s.style.marginRight = 6;

            // Cover legacy, current, and base-slider USS class names — Unity
            // renames these between versions and only one will match per build.
            var tracker = s.Q<VisualElement>(className: "unity-base-slider__tracker")
                       ?? s.Q<VisualElement>(className: "unity-slider__tracker")
                       ?? s.Q<VisualElement>(className: "unity-tracker");
            var dragger = s.Q<VisualElement>(className: "unity-base-slider__dragger")
                       ?? s.Q<VisualElement>(className: "unity-slider__dragger")
                       ?? s.Q<VisualElement>(className: "unity-dragger");
            if (tracker != null)
            {
                tracker.style.height = 4;
                tracker.style.marginTop = 11; // vertically center the 4px rail in 26px
                tracker.style.backgroundColor = new StyleColor(new Color(1f, 1f, 1f, 0.35f));
                tracker.style.borderTopLeftRadius = 2;
                tracker.style.borderTopRightRadius = 2;
                tracker.style.borderBottomLeftRadius = 2;
                tracker.style.borderBottomRightRadius = 2;
            }
            if (dragger != null)
            {
                dragger.style.width = 18;
                dragger.style.height = 18;
                dragger.style.marginTop = 4; // center on the rail
                dragger.style.backgroundColor = new StyleColor(Color.white);
                dragger.style.borderTopLeftRadius = 9;
                dragger.style.borderTopRightRadius = 9;
                dragger.style.borderBottomLeftRadius = 9;
                dragger.style.borderBottomRightRadius = 9;
            }
        }

        // ─────────────── TRL-style row builders ────────────────────────────
        // PPI used to render every settings row as a 50px dark "card" with
        // big padding. TRL uses a flatter look: a flex row with the label
        // on the left and the control on the right, no card background, and
        // matching dark-fill / gray-border styling on the controls.
        // Make*Row signatures are unchanged so callers don't need updates.

        // Compact row matching TRL's CreateConfigurationRow: label flush-left,
        // control flush-right, no background.
        private static UITK.VisualElement MakeConfigRow()
        {
            return new UITK.VisualElement
            {
                style =
                {
                    flexDirection  = FlexDirection.Row,
                    alignItems     = Align.Center,
                    justifyContent = Justify.SpaceBetween,
                    marginTop      = 4,
                    marginBottom   = 4,
                }
            };
        }

        // Settings-tab section title — bold, 20fs white, tight margins.
        // Matches TRL's PlayerQoLSection.Header pattern.
        private void AddSectionHeader(UITK.VisualElement parent, string text, bool topGap = true)
        {
            var h = new UITK.Label("<b>" + text + "</b>"); MakeReadable(h);
            h.style.fontSize = 20;
            h.style.unityFontStyleAndWeight = FontStyle.Normal;
            h.style.color = Color.white;
            h.style.marginTop = topGap ? 12 : 4;
            h.style.marginBottom = 8;
            parent.Add(h);
        }

        // Small explanatory text under a section header (TRL style).
        private void AddSectionNote(UITK.VisualElement parent, string text)
        {
            var n = new UITK.Label(text); MakeReadable(n);
            n.style.fontSize = 13;
            n.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
            n.style.whiteSpace = UITK.WhiteSpace.Normal;
            n.style.marginBottom = 8;
            parent.Add(n);
        }

        // 1-px medium-gray divider between sections.
        private void AddSectionSeparator(UITK.VisualElement parent)
        {
            var sep = new UITK.VisualElement();
            sep.style.height = 1;
            sep.style.backgroundColor = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
            sep.style.marginTop = 12;
            sep.style.marginBottom = 12;
            parent.Add(sep);
        }

        // Dark-gray pill button with white text and white-on-black hover —
        // mirrors TRL's StyleConfigButton so dialog buttons (CLEAR ALL etc.)
        // match the toggles visually.
        internal static void StyleConfigButton(UITK.Button button)
        {
            if (button == null) return;
            button.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.25f));
            button.style.color = Color.white;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.fontSize = 16;
            button.style.paddingTop = 6;
            button.style.paddingBottom = 6;
            button.style.paddingLeft = 14;
            button.style.paddingRight = 14;
            button.style.borderTopWidth = 0;
            button.style.borderBottomWidth = 0;
            button.style.borderLeftWidth = 0;
            button.style.borderRightWidth = 0;
            button.RegisterCallback<UITK.MouseEnterEvent>(_ =>
            {
                button.style.backgroundColor = Color.white;
                button.style.color = Color.black;
            });
            button.RegisterCallback<UITK.MouseLeaveEvent>(_ =>
            {
                button.style.backgroundColor = new StyleColor(new Color(0.25f, 0.25f, 0.25f));
                button.style.color = Color.white;
            });
        }

        private UITK.VisualElement MakeToggleRow(string label, bool start, System.Action<bool> onChange)
        {
            var row = new UITK.VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems    = Align.Center,
                    height        = 50,
                    marginBottom  = 8,
                    backgroundColor = new StyleColor(new Color32(61,61,61,255)),
                    paddingLeft   = 12,
                    paddingRight  = 12
                }
            };

            var lab = new UITK.Label(label); MakeReadable(lab);
            lab.style.minWidth = 300; lab.style.maxWidth = 300; row.Add(lab);

            // Spacer to push toggle to the right (like other tabs)
            var spacer = new UITK.VisualElement();
            spacer.style.flexGrow = 1;
            row.Add(spacer);

            var t = new UITK.Toggle { value = start };
            StyleConfigCheckbox(t);
            row.Add(t);
            t.RegisterValueChangedCallback(ev => onChange?.Invoke(ev.newValue));
            return row;
        }

        // Recolor the checkbox frame to the TRL palette: dark fill + medium-gray
        // border. The default Unity USS renders a light box that disappears
        // against this panel's dark rows; this matches the look TRL uses so
        // mod-injected toggles (e.g. the per-server "MODS REQUIRED" popup
        // checkbox) blend with PPI's settings page.
        internal static void StyleConfigCheckbox(UITK.Toggle toggle)
        {
            if (toggle == null) return;
            toggle.RegisterCallback<UITK.AttachToPanelEvent>(_ =>
            {
                var input = toggle.Q(className: "unity-toggle__input");
                if (input == null) return;
                input.style.backgroundColor   = new StyleColor(new Color(0.15f, 0.15f, 0.15f));
                input.style.borderTopColor    = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
                input.style.borderBottomColor = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
                input.style.borderLeftColor   = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
                input.style.borderRightColor  = new StyleColor(new Color(0.4f, 0.4f, 0.4f));
            });
        }

        private UITK.VisualElement MakeDropdownRow(string label, string currentValue, System.Collections.Generic.List<string> options, System.Action<string> onChange)
        {
            var row = new UITK.VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems    = Align.Center,
                    height        = 50,
                    marginBottom  = 8,
                    backgroundColor = new StyleColor(new Color32(61,61,61,255)),
                    paddingLeft   = 12,
                    paddingRight  = 12
                }
            };

            var lab = new UITK.Label(label); MakeReadable(lab);
            lab.style.minWidth = 300; lab.style.maxWidth = 300; row.Add(lab);

            // Spacer
            var spacer = new UITK.VisualElement();
            spacer.style.flexGrow = 1;
            row.Add(spacer);

            // Dropdown
            var dropdown = new UITK.DropdownField();
            dropdown.choices = options;
            dropdown.value = options.Contains(currentValue) ? currentValue : (options.Count > 0 ? options[0] : "");
            dropdown.style.minWidth = 300;
            
            // Prevent text wrapping and truncate with ellipsis
            var textElement = dropdown.Q<UITK.TextElement>();
            if (textElement != null)
            {
                textElement.style.overflow = Overflow.Hidden;
                textElement.style.textOverflow = TextOverflow.Ellipsis;
                textElement.style.whiteSpace = WhiteSpace.NoWrap;
            }
            
            // Add dropdown indicator
            var arrow = new UITK.Label(" ▼");
            arrow.style.position = Position.Absolute;
            arrow.style.right = 8;
            arrow.style.unityTextAlign = TextAnchor.MiddleRight;
            dropdown.Add(arrow);
            
            dropdown.RegisterValueChangedCallback(ev => onChange?.Invoke(ev.newValue));
            row.Add(dropdown);
            
            return row;
        }

        private UITK.VisualElement MakeSliderRow(string label, float start, out UITK.Slider slider, out UITK.Label valLabel)
        {
            var row = new UITK.VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems    = Align.Center,
                    height        = 50,
                    marginBottom  = 8,
                    backgroundColor = new StyleColor(new Color32(61,61,61,255)),
                    paddingLeft   = 12,
                    paddingRight  = 12
                }
            };

            var lab = new UITK.Label(label); MakeReadable(lab);
            lab.style.minWidth = 300; lab.style.maxWidth = 300; row.Add(lab);

            // Editable value field (between label and slider)
            var valueField = new UITK.TextField();
            valueField.value = start.ToString("0.00");
            MakeReadable(valueField);
            valueField.style.minWidth = 65;
            valueField.style.maxWidth = 65;
            valueField.style.maxHeight = 30;
            valueField.style.unityTextAlign = TextAnchor.MiddleRight;
            valueField.style.marginLeft = 8;
            valueField.style.marginRight = 8;
            row.Add(valueField);

            var localSlider = new UITK.Slider(0f, 1f) { value = Mathf.Clamp01(start), lowValue = 0f, highValue = 1f };
            localSlider.style.flexGrow = 1; localSlider.style.flexBasis = 0; localSlider.style.marginLeft = 6; localSlider.style.marginRight = 6;
            row.Add(localSlider);

            StyleSliderLikeBase(localSlider);
            slider = localSlider; // Set the out parameter

            // Create a dummy label for compatibility (some code expects valLabel)
            var dummyLabel = new UITK.Label();
            valLabel = dummyLabel;

            // Two-way binding between slider and text field
            localSlider.RegisterValueChangedCallback(ev => { 
                float clampedValue = Mathf.Clamp01(ev.newValue);
                valueField.SetValueWithoutNotify(clampedValue.ToString("0.00")); 
            });

            valueField.RegisterValueChangedCallback(ev => {
                if (float.TryParse(ev.newValue, out float newValue))
                {
                    float clampedValue = Mathf.Clamp01(newValue);
                    localSlider.SetValueWithoutNotify(clampedValue);
                }
            });

            return row;
        }

        private UITK.VisualElement MakeTextFieldRow(string label, string startValue, Action<string> onChange)
        {
            var row = new UITK.VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems    = Align.Center,
                    height        = 50,
                    marginBottom  = 8,
                    backgroundColor = new StyleColor(new Color32(61,61,61,255)),
                    paddingLeft   = 12,
                    paddingRight  = 12
                }
            };

            var lab = new UITK.Label(label); MakeReadable(lab);
            lab.style.minWidth = 300; lab.style.maxWidth = 300; row.Add(lab);

            var textField = new UITK.TextField();
            textField.value = startValue;
            MakeReadable(textField);
            textField.style.flexGrow = 1;
            textField.style.maxHeight = 30;
            textField.style.marginLeft = 8;
            row.Add(textField);

            textField.RegisterValueChangedCallback(ev => {
                onChange?.Invoke(ev.newValue);
            });

            return row;
        }

        private void ShowSettingsTab()
        {
            if (_settingsSectionVE == null) _settingsSectionVE = new UITK.VisualElement();
            // Don't set display here - ShowTab() handles visibility
            _settingsSectionVE.Clear();

            // Header
            var h = new UITK.Label("QOL SETTINGS"); MakeReadable(h);
            h.style.unityFontStyleAndWeight = FontStyle.Normal; h.style.fontSize = 30; h.style.marginBottom = 8;
            _settingsSectionVE.Add(h);

            var mV = new UITK.Label("AUDIO"); MakeReadable(mV);
            mV.style.unityFontStyleAndWeight = FontStyle.Normal;
            mV.style.fontSize = 24; mV.style.marginBottom = 8;
            _settingsSectionVE.Add(mV);

            // === AUDIO SECTION ===
            // Load current values from sounds mod config
            var soundsConfig = LoadSoundsConfig();

            // 1) stoppage music volume - read from and write to sounds config
            {
                UITK.Slider s; UITK.Label val;
                _settingsSectionVE.Add(MakeSliderRow("MUSIC VOLUME", soundsConfig.MusicVolume, out s, out val));
                _settingsMusicVolSlider = s; _settingsMusicVolVal = val;
                s.RegisterValueChangedCallback(ev => 
                { 
                    float volume = Mathf.Clamp01(ev.newValue);
                    _cmd.musicvol = volume;
                    
                    // Update sounds mod config
                    var config = LoadSoundsConfig();
                    config.MusicVolume = volume;
                    SaveSoundsConfig(config);
                });
            }
                        // Arena Audio Volume slider (styled like other rows)
            var arenaAudioRow = MakeSliderRow("SCENERY VOLUME", _cmd.arenaAudioVolume, out var arenaAudioSlider, out var arenaAudioVal);
            arenaAudioSlider.lowValue = 0f;
            arenaAudioSlider.highValue = 1f;
            arenaAudioSlider.RegisterValueChangedCallback(evt =>
            {
                _cmd.arenaAudioVolume = evt.newValue;
                arenaAudioVal.text = $"{(int)(evt.newValue * 100)}%";
                ApplyArenaPartialDisable();
            });
            arenaAudioVal.text = $"{(int)(_cmd.arenaAudioVolume * 100)}%";
            _settingsSectionVE.Add(arenaAudioRow);

            {  
            UITK.Slider s; UITK.Label val;
            _settingsSectionVE.Add(MakeSliderRow("MENTION VOLUME", _cmd.mentionSoundVolume, out s, out val));
            s.RegisterValueChangedCallback(ev => 
            { 
                float volume = Mathf.Clamp01(ev.newValue);
                _cmd.mentionSoundVolume = volume;
                SaveConfigsAndRefresh();
            });
            }
                        // 2) warmup music toggle - read from and write to sounds config
            var warmupToggleRow = MakeToggleRow("WARMUP MUSIC", soundsConfig.WarmupMusic, on => 
            { 
                _cmd.warmupmusic = on;
                
                // Update sounds mod config
                var config = LoadSoundsConfig();
                config.WarmupMusic = on;
                SaveSoundsConfig(config);
            });
            _settingsSectionVE.Add(warmupToggleRow);
            _warmupMusicToggle = warmupToggleRow.Q<UITK.Toggle>();

                        // Enable mention sound toggle
            var mentionSoundToggleRow = MakeToggleRow("MENTION SOUND", _cmd.mentionSoundEnabled, on => 
            { 
                _cmd.mentionSoundEnabled = on;
                SaveConfigsAndRefresh();
            });
            _settingsSectionVE.Add(mentionSoundToggleRow);



            // Mention sound dropdown
            var mentionSoundRow = MakeDropdownRow("MENTION TYPE", _cmd.selectedMentionSound, 
                PoncePuck.Keybinds.PingSoundRuntime.GetAvailableSounds(), 
                selected => 
                {
                    if (_cmd.enableDebugLogging)
                    {
                        Debug.Log($"[PPKB] Dropdown selected: {selected}");
                    }
                    _cmd.selectedMentionSound = selected;
                    SaveConfigsAndRefresh();
                    PoncePuck.Keybinds.PingSoundRuntime.ReloadSound();
                });
            _settingsSectionVE.Add(mentionSoundRow);

            // ARENA VISUALS section
            var arenaLoaderTitle = new UITK.Label("VISUALS"); MakeReadable(arenaLoaderTitle);
            arenaLoaderTitle.style.unityFontStyleAndWeight = FontStyle.Normal;
            arenaLoaderTitle.style.fontSize = 24;
            arenaLoaderTitle.style.marginBottom = 8;
            arenaLoaderTitle.style.marginTop = 8;
            _settingsSectionVE.Add(arenaLoaderTitle);

            // Disable arena visuals toggle (MASTER)
            var arenaVisualsToggleRow = MakeToggleRow("DESTROY FULL ARENA", _cmd.disableArenaVisuals, on =>
            {
                _cmd.disableArenaVisuals = on;
                ApplyArenaDisableState(on);
            });
            _settingsSectionVE.Add(arenaVisualsToggleRow);

            // Granular arena controls (indented sub-options)
            var arenaPropsToggleRow = MakeToggleRow("DISABLE ASSETS", _cmd.disableArenaProps, on =>
            {
                _cmd.disableArenaProps = on;
                ApplyArenaPartialDisable();
            });
            _settingsSectionVE.Add(arenaPropsToggleRow);

            var arenaLightsToggleRow = MakeToggleRow("DISABLE LIGHTS", _cmd.disableArenaLights, on =>
            {
                _cmd.disableArenaLights = on;
                ApplyArenaPartialDisable();
            });
            _settingsSectionVE.Add(arenaLightsToggleRow);

            var arenaSkyboxToggleRow = MakeToggleRow("DISABLE SKYBOX", _cmd.disableArenaSkybox, on =>
            {
                _cmd.disableArenaSkybox = on;
                ApplyArenaPartialDisable();
            });
            _settingsSectionVE.Add(arenaSkyboxToggleRow);

            var arenaParticlesToggleRow = MakeToggleRow("DISABLE PARTICLES", _cmd.disableArenaParticles, on =>
            {
                _cmd.disableArenaParticles = on;
                ApplyArenaPartialDisable();
            });
            _settingsSectionVE.Add(arenaParticlesToggleRow);


                        var vIs = new UITK.Label("CHAT"); MakeReadable(vIs);
            vIs.style.unityFontStyleAndWeight = FontStyle.Normal;
            vIs.style.fontSize = 24; vIs.style.marginBottom = 8;
            _settingsSectionVE.Add(vIs);


            // Chat rich text toggle
            var chatRichTextToggleRow = MakeToggleRow("CHAT RICH TEXT", _cmd.enableChatRichText, on =>
            {
                _cmd.enableChatRichText = on;
            });
            _settingsSectionVE.Add(chatRichTextToggleRow);

            // Live Logging toggle
            var liveLoggingToggleRow = MakeToggleRow("LIVE LOGGING", _cmd.enableLiveLogging, on =>
            {
                _cmd.enableLiveLogging = on;
                Debug.Log($"[Settings] Live logging {(on ? "enabled" : "disabled")}");
            });
            _settingsSectionVE.Add(liveLoggingToggleRow);

            var labelnoices = new UITK.Label("These may only take effect after rejoining a server."); MakeReadable(labelnoices);
            labelnoices.style.unityFontStyleAndWeight = FontStyle.Normal;
            labelnoices.style.fontSize = 16; labelnoices.style.marginBottom = 8;
            _settingsSectionVE.Add(labelnoices);
                
                /* Additional UI Color Settings
                {
                    var colorHeader = new UITK.Label("UI COLOR SETTINGS");
                    colorHeader.style.fontSize = 24;
                    colorHeader.style.unityFontStyleAndWeight = FontStyle.Normal;
                    colorHeader.style.marginTop = 16;
                    colorHeader.style.marginBottom = 8;
                    colorHeader.style.color = new UITK.StyleColor(Color.white);
                    _settingsSectionVE.Add(colorHeader);

                    // === PANEL CUSTOMIZATION SECTION ===
                    {
                        UITK.Slider s; UITK.Label _;
                        _settingsSectionVE.Add(MakeSliderRow("PANEL OPACITY", _cmd.panelAlpha, out s, out _));
                        _panelAlphaSlider = s;
                        s.RegisterValueChangedCallback(ev =>
                        {
                            _cmd.panelAlpha = Mathf.Clamp01(ev.newValue);
                            ApplyPanelAlpha();
                        });
                    }


                    // Panel color hex field
                    {
                        _settingsSectionVE.Add(MakeHexColorRow("PANEL COLOR", "#323232", out _panelColorSwatch, out _panelColorHexField));

                        _panelColorHexField.RegisterValueChangedCallback(ev =>
                        {
                            if (TryParseHexColor(ev.newValue, out var newColor) && _ppkbPanel != null)
                            {
                                newColor.a = Mathf.Clamp01(_cmd.panelAlpha); // Preserve current alpha
                                _ppkbPanel.style.backgroundColor = new UITK.StyleColor(newColor);

                                // Update swatch
                                _panelColorSwatch.style.backgroundColor = new UITK.StyleColor(newColor);

                                // Update sliders to match hex input (without triggering callbacks)
                                Color.RGBToHSV(newColor, out _panelH, out _panelS, out _panelV);
                                _panelHueSlider?.SetValueWithoutNotify(_panelH);
                                _panelSaturationSlider?.SetValueWithoutNotify(_panelS);
                                _panelBrightnessSlider?.SetValueWithoutNotify(_panelV);

                                // Save HSV values to config
                                _cmd._panelHueSlider = _panelH;
                                _cmd._panelSaturationSlider = _panelS;
                                _cmd._panelBrightnessSlider = _panelV;
                            }
                        });
                    }

                    // Panel background brightness
                    {
                        UITK.Label bgVal;
                        _settingsSectionVE.Add(MakeSliderRow("PANEL BRIGHTNESS", 0.2f, out _panelBrightnessSlider, out bgVal));

                        _panelBrightnessSlider.RegisterValueChangedCallback(ev =>
                        {
                            _panelV = Mathf.Clamp01(ev.newValue);
                            _cmd._panelBrightnessSlider = _panelV; // Save to config
                            UpdatePanelColorFromSliders();
                        });
                    }

                    // Panel hue
                    {
                        UITK.Label bgVal;
                        _settingsSectionVE.Add(MakeSliderRow("PANEL HUE", 0.0f, out _panelHueSlider, out bgVal));

                        _panelHueSlider.RegisterValueChangedCallback(ev =>
                        {
                            _panelH = Mathf.Clamp01(ev.newValue);
                            _cmd._panelHueSlider = _panelH; // Save to config
                            UpdatePanelColorFromSliders();
                        });
                    }

                    // Panel saturation
                    {
                        UITK.Label bgVal;
                        _settingsSectionVE.Add(MakeSliderRow("PANEL SATURATION", 0.0f, out _panelSaturationSlider, out bgVal));

                        _panelSaturationSlider.RegisterValueChangedCallback(ev =>
                        {
                            _panelS = Mathf.Clamp01(ev.newValue);
                            _cmd._panelSaturationSlider = _panelS; // Save to config
                            UpdatePanelColorFromSliders();
                        });
                    }
                    */

                    // Trusted Servers — management surface for the per-server
                    // "MODS REQUIRED" popup suppression. The opt-in toggle is
                    // injected into the vanilla popup itself
                    // (MissingModsPopupSuppression.cs); this lists every
                    // trusted endpoint with a per-row Untrust button plus a
                    // wipe-all action.
                    {
                        AddSectionSeparator(_settingsSectionVE);
                        AddSectionHeader(_settingsSectionVE, "Trusted Servers");
                        AddSectionNote(_settingsSectionVE,
                            "Servers you ticked \"Don't show this popup again\" on inside the " +
                            "MODS REQUIRED popup. Trust is per-server; the popup returns automatically " +
                            "if the server's required mod set changes.");

                        // Container the rebuild repaints into. Re-rendering
                        // the whole list on each mutation keeps the per-row
                        // closures simple — no need to track row VisualElements.
                        var listContainer = new UITK.VisualElement();
                        _settingsSectionVE.Add(listContainer);

                        System.Action rebuildList = null;
                        rebuildList = () =>
                        {
                            listContainer.Clear();
                            var keys = MissingModsPopupSuppression.SnapshotKeys();

                            if (keys.Count == 0)
                            {
                                var empty = new UITK.Label("No trusted servers yet.");
                                MakeReadable(empty);
                                empty.style.fontSize = 14;
                                empty.style.color = new StyleColor(new Color(0.65f, 0.65f, 0.65f));
                                empty.style.marginTop = 4;
                                empty.style.marginBottom = 4;
                                listContainer.Add(empty);
                                return;
                            }

                            foreach (var key in keys)
                            {
                                var row = MakeConfigRow();

                                int modCount = MissingModsPopupSuppression.CountModsFor(key);
                                string cachedName = MissingModsPopupSuppression.GetCachedServerName(key);
                                bool hasName = !string.IsNullOrEmpty(cachedName);

                                var stack = new UITK.VisualElement();
                                stack.style.flexDirection = FlexDirection.Column;
                                stack.style.flexGrow = 1;

                                // Primary line: friendly name if we've seen the server in
                                // the browser this session, otherwise raw ip:port.
                                var primary = new UITK.Label(hasName ? cachedName : key);
                                MakeReadable(primary);
                                primary.style.fontSize = 16;
                                primary.style.color = Color.white;
                                stack.Add(primary);

                                // Subtitle: when we have a friendly name, append the
                                // endpoint so the user can still identify which server
                                // shares that name. Either way, show the mod-count.
                                string subText = hasName
                                    ? key + " — " + modCount + " mod" + (modCount == 1 ? "" : "s") + " trusted"
                                    : modCount + " mod" + (modCount == 1 ? "" : "s") + " trusted";
                                var sub = new UITK.Label(subText); MakeReadable(sub);
                                sub.style.fontSize = 11;
                                sub.style.color = new StyleColor(new Color(0.65f, 0.65f, 0.65f));
                                sub.style.marginTop = 0;
                                stack.Add(sub);

                                row.Add(stack);

                                var capturedKey = key;
                                var untrustBtn = new UITK.Button(() =>
                                {
                                    MissingModsPopupSuppression.Remove(capturedKey);
                                    rebuildList();
                                })
                                { text = "Untrust" };
                                StyleConfigButton(untrustBtn);
                                untrustBtn.style.marginLeft = 8;
                                row.Add(untrustBtn);

                                listContainer.Add(row);
                            }

                            // Wipe-all action flushed right under the list.
                            var clearAllRow = MakeConfigRow();
                            clearAllRow.style.justifyContent = Justify.FlexEnd;
                            clearAllRow.style.marginTop = 8;
                            var clearBtn = new UITK.Button(() =>
                            {
                                MissingModsPopupSuppression.RemoveAll();
                                rebuildList();
                            })
                            { text = "Untrust all servers" };
                            StyleConfigButton(clearBtn);
                            clearAllRow.Add(clearBtn);
                            listContainer.Add(clearAllRow);
                        };
                        rebuildList();
                    }

                    // Admin Settings Section
                    {
                        var adminHeader = new UITK.Label("ADMIN");
                        adminHeader.style.fontSize = 24;
                        adminHeader.style.unityFontStyleAndWeight = FontStyle.Normal;
                        adminHeader.style.marginTop = 16;
                        adminHeader.style.marginBottom = 8;
                        adminHeader.style.color = new UITK.StyleColor(Color.white);
                        _settingsSectionVE.Add(adminHeader);

                        // === ADMIN MODE TOGGLE ===
                        var adminToggleRow = MakeToggleRow("ADMIN MODE", _cmd.enableAdminSettings, enabled =>
                        {
                            _cmd.enableAdminSettings = enabled;
                            SetAdminEnabled(enabled);
                        });
                        _settingsSectionVE.Add(adminToggleRow);

                        // Debug logging toggle
                        var debugTitle = new UITK.Label("DEBUG"); MakeReadable(debugTitle);
                        debugTitle.style.unityFontStyleAndWeight = FontStyle.Normal;
                        debugTitle.style.fontSize = 24;
                        debugTitle.style.marginBottom = 8;
                        debugTitle.style.marginTop = 16;
                        _settingsSectionVE.Add(debugTitle);

                        var debugToggleRow = MakeToggleRow("DEBUG LOGGING", _cmd.enableDebugLogging, on =>
                        {
                            _cmd.enableDebugLogging = on;
                            SaveConfigsAndRefresh();
                        });
                        _settingsSectionVE.Add(debugToggleRow);

                        // Initialize panel color values (only if panel color controls exist)
                        {
                            // Load values from config or use defaults
                            _panelH = _cmd._panelHueSlider;
                            _panelS = _cmd._panelSaturationSlider;
                            _panelV = _cmd._panelBrightnessSlider;

                            // Set slider values without triggering callbacks (if controls exist)
                            _panelHueSlider?.SetValueWithoutNotify(_panelH);
                            _panelSaturationSlider?.SetValueWithoutNotify(_panelS);
                            _panelBrightnessSlider?.SetValueWithoutNotify(_panelV);

                            // Calculate color from HSV values
                            Color panelColor = Color.HSVToRGB(_panelH, _panelS, _panelV);
                            panelColor.a = _cmd.panelAlpha;

                            // Update hex field (if control exists)
                            string hexColor = ColorToHex(panelColor);
                            _panelColorHexField?.SetValueWithoutNotify(hexColor);

                            // Update swatch (if control exists)
                            if (_panelColorSwatch != null)
                                _panelColorSwatch.style.backgroundColor = new UITK.StyleColor(panelColor);
                        }
                    }
                }
        private void ApplyPanelAlpha()
        {
            if (_ppkbPanel == null) return;
            
            // Preserve current HSV color and only update alpha
            var currentColor = Color.HSVToRGB(_panelH, _panelS, _panelV);
            var a = Mathf.Clamp01(_cmd.panelAlpha);
            currentColor.a = a;
            
            _ppkbPanel.style.backgroundColor = new StyleColor(currentColor);
            
            // Also update swatch and hex field to reflect alpha change (if controls exist)
            if (_panelColorSwatch != null)
                _panelColorSwatch.style.backgroundColor = new UITK.StyleColor(currentColor);
            string hexColor = ColorToHex(currentColor);
            _panelColorHexField?.SetValueWithoutNotify(hexColor);
        }

        private void SetAdminEnabled(bool enabled)
        {
            // Admin panel automatically works when toggle is on - it checks KeybindRunner.AdminModeEnabled
            Debug.Log($"[KeybindRunner] Admin mode {(enabled ? "enabled" : "disabled")} - right-click scoreboard players to use admin menu");
        }

        private void UpdatePanelColorFromSliders()
        {
            if (_ppkbPanel != null)
            {
                var bgColor = Color.HSVToRGB(_panelH, _panelS, _panelV);
                bgColor.a = Mathf.Clamp01(_cmd.panelAlpha); // Use current alpha setting
                _ppkbPanel.style.backgroundColor = new UITK.StyleColor(bgColor);
                
                // Update swatch and hex field (without triggering callbacks) - if controls exist
                if (_panelColorSwatch != null)
                    _panelColorSwatch.style.backgroundColor = new UITK.StyleColor(bgColor);
                string hexColor = ColorToHex(bgColor);
                _panelColorHexField?.SetValueWithoutNotify(hexColor);
            }
        }

    }
}