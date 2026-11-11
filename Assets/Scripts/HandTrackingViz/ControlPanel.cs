using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HandTrackingViz
{
    // ====================================================================
    // ControlPanel
    // --------------------------------------------------------------------
    // Head-locked, top-centre cluster, stacked top to bottom on one plane
    // with the status dots:
    //
    //        (gear)          <- always visible; opens / closes the panel
    //       o o o  o         <- StatusIndicator dots (not drawn here)
    //   [ IP       ... ]     <- panel, hidden until the gear is pressed
    //   [ 3D view  ... ]
    //      [keypad]          <- only while editing the 3D-view source
    //
    // The two switches are OFF at every launch:
    //
    //   IP       — shows the address a PC connects to for the pose stream,
    //              "<headset-ip>:<publisher-port>". Refreshed every couple of
    //              seconds while on, so a Wi-Fi change shows up by itself.
    //
    //   3D view  — turns passthrough off and hands over to a 3D-view receiver
    //              (ZED, C1, ... over WHEP) through RemoteView. The source's
    //              address is needed because the headset connects to the
    //              source, not the other way round; "Edit" opens a numeric
    //              keypad for it, and on first use the keypad opens by itself.
    //              The address is saved across launches. This build carries no
    //              receiver yet, which the panel states rather than pretending
    //              to wait for video.
    //
    // Closing the panel only hides it: a switch that is on stays on.
    //
    // Created at runtime by PoseFramePublisher (showControlPanel), next to the
    // status HUD, so it exists in both APKs without scene edits. Input is
    // fingertip poke or controller ray + trigger — see PanelPointer.
    // ====================================================================
    public sealed class ControlPanel : MonoBehaviour
    {
        private const float MetersPerUnit = 0.0001f;   // 1 layout unit = 0.1 mm
        private const float IpRefreshSeconds = 2f;
        private const int MaxEntryLength = 21;          // "255.255.255.255:65535"

        // ---- layout (units; canvas pivot bottom-left) ----------------------
        private const float Width = 1900f;
        private const float KeypadWidth = 800f, KeypadHeight = 1460f;
        private const float RowsBottom = 1480f, RowsHeight = 540f, RowHeight = 240f;
        private const float DotsY = 2140f;              // where StatusIndicator's row lands
        private const float GearSize = 200f, GearCenterY = 2370f;
        private const float Height = GearCenterY + GearSize * 0.5f;

        private static readonly Color PlateColor = new Color(0.07f, 0.08f, 0.10f, 0.82f);
        private static readonly Color KeyColor = new Color(0.22f, 0.23f, 0.27f, 0.95f);
        private static readonly Color ActionKeyColor = new Color(0.16f, 0.45f, 0.85f, 0.95f);
        private static readonly Color EntryColor = new Color(0.02f, 0.02f, 0.03f, 0.95f);
        private static readonly Color GearIdle = new Color(0.07f, 0.08f, 0.10f, 0.75f);
        private static readonly Color GearOpen = new Color(0.16f, 0.45f, 0.85f, 0.95f);
        private static readonly Color TextMain = Color.white;
        private static readonly Color TextDim = new Color(0.68f, 0.70f, 0.74f);
        private static readonly Color TextError = new Color(1f, 0.42f, 0.38f);

        private static readonly string HintText =
            $"Source IP: a.b.c.d  or  a.b.c.d:port\nport defaults to {RemoteView.DefaultPort}";

        private Camera hmdCamera;
        private Transform trackingOrigin;
        private int publisherPort;

        private RectTransform canvasRect;
        private readonly List<PanelButton> buttons = new List<PanelButton>();
        private PanelButton gearButton;
        private RectTransform gearIcon;
        private RectTransform rows;
        private Image rowsPlate;
        private RectTransform keypad;
        private Image keypadPlate;

        private PanelToggle ipToggle;
        private PanelToggle viewToggle;
        private TextMeshProUGUI ipValue;
        private TextMeshProUGUI viewValue;
        private TextMeshProUGUI entryText;
        private TextMeshProUGUI hintText;
        private string entry = "";

        private PanelPointer pointer;
        private PassthroughSwitch passthrough;
        private float nextIpRefresh;
        private float gearAngle;

        public bool IsOpen => rows != null && rows.gameObject.activeSelf;

        public static ControlPanel Attach(Camera hmdCamera, Transform trackingOrigin, int publisherPort)
        {
            var go = new GameObject("ControlPanel", typeof(RectTransform));
            go.transform.SetParent(hmdCamera.transform, false);
            var panel = go.AddComponent<ControlPanel>();
            panel.hmdCamera = hmdCamera;
            panel.trackingOrigin = trackingOrigin;
            panel.publisherPort = publisherPort;
            panel.Build();
            return panel;
        }

        // ------------------------------------------------------------- layout

        private void Build()
        {
            canvasRect = (RectTransform)transform;
            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = hmdCamera;
            canvasRect.pivot = Vector2.zero;
            canvasRect.sizeDelta = new Vector2(Width, Height);
            // Same plane as the status dots, horizontally centred, positioned so
            // the dots fall in the gap between the gear and the panel.
            canvasRect.localPosition = new Vector3(
                StatusIndicator.RowCenter.x - Width * 0.5f * MetersPerUnit,
                StatusIndicator.RowCenter.y - DotsY * MetersPerUnit,
                StatusIndicator.Distance);
            canvasRect.localRotation = Quaternion.identity;
            canvasRect.localScale = Vector3.one * MetersPerUnit;

            BuildGear();
            BuildRows();
            BuildKeypad();

            ipToggle.Changed += OnIpToggle;
            viewToggle.Changed += OnViewToggle;
            RemoteView.StatusChanged += RefreshViewValue;
            RemoteView.Changed += RefreshViewValue;

            pointer = new PanelPointer(trackingOrigin, transform);
            passthrough = new PassthroughSwitch(hmdCamera);

            rows.gameObject.SetActive(false);
            RefreshIpValue();
            RefreshViewValue();
            RefreshGear();
        }

        private void BuildGear()
        {
            var pos = new Vector2((Width - GearSize) * 0.5f, GearCenterY - GearSize * 0.5f);
            gearButton = new PanelButton("GearButton", canvasRect, pos, new Vector2(GearSize, GearSize),
                                         GearIdle, GearSize * 0.5f) { OnClick = ToggleOpen };
            buttons.Add(gearButton);

            const float icon = 150f;
            var image = PanelUi.Rect("GearIcon", gearButton.Rect,
                                     new Vector2((GearSize - icon) * 0.5f, (GearSize - icon) * 0.5f),
                                     new Vector2(icon, icon)).gameObject.AddComponent<Image>();
            image.sprite = PanelSprites.Gear;
            image.raycastTarget = false;
            gearIcon = image.rectTransform;
            gearIcon.pivot = new Vector2(0.5f, 0.5f);           // rotate about its centre
            gearIcon.anchoredPosition += new Vector2(icon, icon) * 0.5f;
        }

        private void BuildRows()
        {
            rows = PanelUi.Rect("Panel", canvasRect, new Vector2(0f, RowsBottom), new Vector2(Width, RowsHeight));
            rowsPlate = PanelUi.Box("Plate", rows, Vector2.zero, rows.sizeDelta, PlateColor, 70f);

            // IP on top, 3D view below.
            float ipY = 20f + RowHeight + 20f, viewY = 20f;

            ipToggle = AddToggle("IpToggle", rows, new Vector2(40f, ipY + 40f));
            PanelUi.Label("IpLabel", rows, new Vector2(370f, ipY), new Vector2(380f, RowHeight),
                          92f, TextAlignmentOptions.Left, TextMain, "IP", bold: true);
            ipValue = PanelUi.Label("IpValue", rows, new Vector2(770f, ipY), new Vector2(1090f, RowHeight),
                                    84f, TextAlignmentOptions.Left, TextDim, minFontSize: 48f);

            viewToggle = AddToggle("ViewToggle", rows, new Vector2(40f, viewY + 40f));
            PanelUi.Label("ViewLabel", rows, new Vector2(370f, viewY), new Vector2(380f, RowHeight),
                          92f, TextAlignmentOptions.Left, TextMain, "3D view", bold: true);
            viewValue = PanelUi.Label("ViewValue", rows, new Vector2(770f, viewY), new Vector2(850f, RowHeight),
                                      76f, TextAlignmentOptions.Left, TextDim, minFontSize: 44f);
            AddButton("EditButton", rows, new Vector2(1640f, viewY + 40f), new Vector2(220f, 160f),
                      ActionKeyColor, 50f, "Edit", 64f, ToggleKeypad);
        }

        private void BuildKeypad()
        {
            // Child of the panel (so closing the panel hides it too), hanging
            // below it: its bottom edge is the canvas bottom.
            keypad = PanelUi.Rect("Keypad", rows, new Vector2((Width - KeypadWidth) * 0.5f, -RowsBottom),
                                  new Vector2(KeypadWidth, KeypadHeight));
            keypadPlate = PanelUi.Box("KeypadPlate", keypad, Vector2.zero, keypad.sizeDelta, PlateColor, 70f);

            hintText = PanelUi.Label("Hint", keypad, new Vector2(40f, 1310f), new Vector2(720f, 130f),
                                     46f, TextAlignmentOptions.Left, TextDim, HintText, minFontSize: 30f);
            PanelUi.Box("Entry", keypad, new Vector2(20f, 1120f), new Vector2(760f, 170f), EntryColor, 36f);
            entryText = PanelUi.Label("EntryText", keypad, new Vector2(50f, 1120f), new Vector2(700f, 170f),
                                      84f, TextAlignmentOptions.Left, TextMain, minFontSize: 50f);

            string[,] keys =
            {
                { "1", "2", "3" },
                { "4", "5", "6" },
                { "7", "8", "9" },
                { ".", "0", ":" },
                { "DEL", "CLR", "OK" },
            };
            for (int r = 0; r < 5; r++)
            {
                for (int c = 0; c < 3; c++)
                {
                    string key = keys[r, c];
                    var pos = new Vector2(20f + c * 260f, 900f - r * 220f);
                    bool action = key.Length > 1;
                    AddButton("Key_" + key, keypad, pos, new Vector2(240f, 200f),
                              action ? ActionKeyColor : KeyColor, 44f, key, action ? 62f : 96f,
                              () => OnKey(key));
                }
            }

            keypad.gameObject.SetActive(false);
        }

        private PanelToggle AddToggle(string name, Transform parent, Vector2 position)
        {
            var toggle = new PanelToggle(name, parent, position, new Vector2(300f, 160f));
            buttons.Add(toggle.Button);
            return toggle;
        }

        private void AddButton(string name, Transform parent, Vector2 position, Vector2 size, Color color,
                               float radius, string caption, float fontSize, System.Action onClick)
        {
            var button = new PanelButton(name, parent, position, size, color, radius, caption, fontSize)
            {
                OnClick = onClick
            };
            buttons.Add(button);
        }

        // ------------------------------------------------------------ runtime

        private void Update()
        {
            pointer.Update(canvasRect, buttons, IsOverPanel);

            float dt = Time.unscaledDeltaTime;
            ipToggle.Tick(dt);
            viewToggle.Tick(dt);

            // Gear turns a sixth of a revolution (one tooth pitch is 45 deg)
            // as the panel opens, and back as it closes.
            float target = IsOpen ? 60f : 0f;
            if (!Mathf.Approximately(gearAngle, target))
            {
                gearAngle = Mathf.MoveTowards(gearAngle, target, 400f * dt);
                gearIcon.localRotation = Quaternion.Euler(0f, 0f, -gearAngle);
            }

            if (ipToggle.On && Time.unscaledTime >= nextIpRefresh)
                RefreshIpValue();
        }

        private bool IsOverPanel(Vector3 world)
        {
            return Contains(gearButton.Rect, world)
                || (rows.gameObject.activeSelf && Contains(rowsPlate.rectTransform, world))
                || (keypad.gameObject.activeInHierarchy && Contains(keypadPlate.rectTransform, world));
        }

        private static bool Contains(RectTransform rt, Vector3 world)
        {
            Vector3 local = rt.InverseTransformPoint(world);
            return rt.rect.Contains(new Vector2(local.x, local.y));
        }

        private void ToggleOpen()
        {
            bool open = !IsOpen;
            rows.gameObject.SetActive(open);
            if (!open) CloseKeypad();
            RefreshGear();
        }

        private void RefreshGear()
        {
            gearButton.SetColor(IsOpen ? GearOpen : GearIdle);
        }

        private void OnIpToggle(bool on)
        {
            nextIpRefresh = 0f;
            RefreshIpValue();
        }

        private void OnViewToggle(bool on)
        {
            if (on)
            {
                passthrough.SetPassthrough(false);
                RemoteView.SetActive(true);
                if (!RemoteView.EndpointWasSetByUser)
                    OpenKeypad();
            }
            else
            {
                CloseKeypad();
                passthrough.SetPassthrough(true);
                RemoteView.SetActive(false);
            }
            RefreshViewValue();
        }

        private void RefreshIpValue()
        {
            nextIpRefresh = Time.unscaledTime + IpRefreshSeconds;
            if (!ipToggle.On)
            {
                ipValue.text = "-";
                ipValue.color = TextDim;
                return;
            }

            string ip = NetworkInfo.GetLocalIPv4();
            ipValue.text = ip != null ? $"{ip}:{publisherPort}" : "no network";
            ipValue.color = ip != null ? TextMain : TextError;
        }

        private void RefreshViewValue()
        {
            if (viewValue == null) return;
            string endpoint = RemoteView.Endpoint.ToString();
            if (!viewToggle.On)
            {
                viewValue.text = endpoint;
                viewValue.color = TextDim;
                return;
            }

            string status = RemoteView.ReceiverStatus ?? "no receiver";
            viewValue.text = $"{endpoint}\n<size=80%><color=#B4B8C0>{status}</color></size>";
            viewValue.color = TextMain;
        }

        // ------------------------------------------------------------- keypad

        private void ToggleKeypad()
        {
            if (keypad.gameObject.activeSelf) CloseKeypad();
            else OpenKeypad();
        }

        private void OpenKeypad()
        {
            entry = RemoteView.Endpoint.ToString();
            SetHint(HintText, false);
            RefreshEntry();
            keypad.gameObject.SetActive(true);
        }

        private void CloseKeypad()
        {
            keypad.gameObject.SetActive(false);
        }

        private void OnKey(string key)
        {
            switch (key)
            {
                case "DEL":
                    if (entry.Length > 0) entry = entry.Substring(0, entry.Length - 1);
                    break;
                case "CLR":
                    entry = "";
                    break;
                case "OK":
                    if (RemoteViewEndpoint.TryParse(entry, RemoteView.DefaultPort,
                                                    out RemoteViewEndpoint endpoint, out string error))
                    {
                        RemoteView.SetEndpoint(endpoint);
                        CloseKeypad();
                        RefreshViewValue();
                    }
                    else
                    {
                        SetHint(error, true);
                    }
                    return;
                default:
                    if (entry.Length < MaxEntryLength) entry += key;
                    break;
            }
            SetHint(HintText, false);
            RefreshEntry();
        }

        private void RefreshEntry()
        {
            entryText.text = entry + "<color=#6CB4FF>|</color>";
        }

        private void SetHint(string text, bool isError)
        {
            hintText.text = text;
            hintText.color = isError ? TextError : TextDim;
        }

        // ----------------------------------------------------------- teardown

        private void OnDisable()
        {
            // Never leave the user in the dark: if the panel goes away with the
            // 3D view on, passthrough comes back.
            if (viewToggle != null && viewToggle.On) viewToggle.Set(false);
            pointer?.HideRays();
        }

        private void OnDestroy()
        {
            RemoteView.StatusChanged -= RefreshViewValue;
            RemoteView.Changed -= RefreshViewValue;
        }
    }
}
