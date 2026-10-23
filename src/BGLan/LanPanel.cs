using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace BGLan
{
    /// <summary>
    /// The host/join panel on the login screen, drawn with IMGUI but styled like the game:
    /// the game's own font, dark navy panel with a blue border, green buttons like "Connect".
    /// </summary>
    public class LanPanel
    {
        // Layout is designed for 1920x1080 and scaled to the real screen height.
        private const float RefHeight = 1080f;
        private const float Width = 860f;
        private const float Height = 150f;

        private static readonly Color Navy = new Color32(13, 19, 30, 230);
        private static readonly Color Blue = new Color32(52, 152, 230, 255);
        private static readonly Color Green = new Color32(86, 150, 40, 255);
        private static readonly Color GreenLight = new Color32(132, 200, 70, 255);
        private static readonly Color Slate = new Color32(30, 41, 58, 255);
        private static readonly Color SlateLight = new Color32(62, 80, 104, 255);
        private static readonly Color Grey = new Color32(150, 162, 180, 255);
        private static readonly Color OkGreen = new Color32(140, 230, 90, 255);
        private static readonly Color ErrorRed = new Color32(255, 110, 90, 255);

        private string ipField;
        private bool hostField;
        private bool applied;
        private bool appliedHost;
        private string appliedIp;

        private Font font;
        private bool stylesReady;
        private GUIStyle panel, title, version, selected, unselected, field, label, status;

        public LanPanel()
        {
            ipField = Config.HostAddress;
            hostField = Config.IsHost;
        }

        public void Draw()
        {
            if (!stylesReady) BuildStyles();

            float scale = Screen.height / RefHeight;
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            float screenWidth = Screen.width / scale;

            // Centered under the game's "Connect" button.
            var r = new Rect((screenWidth - Width) / 2f, 895f, Width, Height);
            GUI.Box(r, GUIContent.none, panel);

            GUI.Label(new Rect(r.x + 20, r.y + 10, 400, 32), "LAN MULTIPLAYER", title);
            GUI.Label(new Rect(r.xMax - 140, r.y + 12, 120, 30), "v" + Mod.Version, version);

            float y = r.y + 50, x = r.x + 20, h = 44;
            if (GUI.Button(new Rect(x, y, 150, h), "HOST", hostField ? selected : unselected)) hostField = true;
            x += 158;
            if (GUI.Button(new Rect(x, y, 150, h), "JOIN", hostField ? unselected : selected)) hostField = false;
            x += 166;
            if (!hostField)
            {
                GUI.Label(new Rect(x, y, 120, h), "SERVER IP", label);
                x += 122;
                ipField = GUI.TextField(new Rect(x, y, 200, h), ipField ?? "", 64, field);
            }
            if (GUI.Button(new Rect(r.xMax - 170, y, 150, h), "APPLY", selected)) Apply();

            // The confirmation disappears as soon as the settings are edited again.
            if (applied && (hostField != appliedHost || (!hostField && ipField != appliedIp))) applied = false;
            string text = applied ? "Settings applied! Now click CONNECT." : Mod.Status;
            status.normal.textColor = applied ? OkGreen : (text != null && text.StartsWith("ERROR") ? ErrorRed : Grey);
            GUI.Label(new Rect(r.x + 20, r.y + 104, Width - 40, 36), text ?? "", status);

            GUI.matrix = oldMatrix;
        }

        private void Apply()
        {
            Config.IsHost = hostField;
            if (!hostField) Config.HostAddress = (ipField ?? "").Trim();
            Config.Save();
            Mod.Apply();
            appliedHost = hostField;
            appliedIp = ipField;
            applied = !Mod.Status.StartsWith("ERROR");
        }

        private void BuildStyles()
        {
            font = FindGameFont();
            Log.Info("LAN panel font: " + (font != null ? font.name : "Unity default"));

            panel = new GUIStyle { normal = { background = Bordered(Navy, Blue) }, border = new RectOffset(3, 3, 3, 3) };
            title = Text(28, Blue, TextAnchor.MiddleLeft);
            version = Text(20, Grey, TextAnchor.MiddleRight);
            label = Text(22, Color.white, TextAnchor.MiddleLeft);
            status = Text(20, Grey, TextAnchor.MiddleLeft);
            status.wordWrap = true;

            selected = Button(Green, GreenLight, Color.Lerp(Green, GreenLight, 0.4f));
            unselected = Button(Slate, SlateLight, Color.Lerp(Slate, SlateLight, 0.5f));

            field = Text(24, Color.white, TextAnchor.MiddleLeft);
            field.normal.background = Bordered(new Color32(6, 10, 16, 255), SlateLight);
            field.focused.background = Bordered(new Color32(6, 10, 16, 255), Blue);
            field.focused.textColor = Color.white;
            field.hover.background = field.normal.background;
            field.hover.textColor = Color.white;
            field.border = new RectOffset(3, 3, 3, 3);
            field.padding = new RectOffset(10, 10, 4, 4);
            field.clipping = TextClipping.Clip;

            // The login screen may still be loading: keep looking for the game's font for a few seconds.
            stylesReady = font != null || Time.realtimeSinceStartup > 20f;
        }

        private GUIStyle Text(int size, Color color, TextAnchor anchor)
        {
            var s = new GUIStyle { font = font, fontSize = size, alignment = anchor };
            s.normal.textColor = color;
            return s;
        }

        private GUIStyle Button(Color fill, Color edge, Color hover)
        {
            GUIStyle s = Text(24, Color.white, TextAnchor.MiddleCenter);
            s.normal.background = Bordered(fill, edge);
            s.hover.background = Bordered(hover, edge);
            s.active.background = Bordered(edge, edge);
            s.hover.textColor = Color.white;
            s.active.textColor = Color.white;
            s.border = new RectOffset(3, 3, 3, 3);
            return s;
        }

        /// <summary>A 9-sliceable texture: fill color with a 2px edge.</summary>
        private static Texture2D Bordered(Color fill, Color edge)
        {
            const int size = 8;
            var tex = new Texture2D(size, size) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    bool isEdge = i < 2 || j < 2 || i >= size - 2 || j >= size - 2;
                    tex.SetPixel(i, j, isEdge ? edge : fill);
                }
            }
            tex.Apply();
            return tex;
        }

        /// <summary>The font of the game's own login screen text ("VERSION: 1.1.2"), or any UI font in the scene.</summary>
        private static Font FindGameFont()
        {
            SteamConnect login = Object.FindObjectOfType<SteamConnect>();
            if (login != null && login.versionNo != null && login.versionNo.font != null) return login.versionNo.font;
            Text any = Object.FindObjectsOfType<Text>().FirstOrDefault(t => t.font != null && t.font.name != "Arial");
            return any != null ? any.font : null;
        }
    }
}
