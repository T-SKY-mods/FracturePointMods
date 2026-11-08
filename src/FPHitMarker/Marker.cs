using UnityEngine;
using UnityEngine.UI;

namespace FPHitMarker
{
    // 画面の中央の斜めの十字（4 本。頭のときは各線が横に並んだ 2 本になる）。当たるたびに少し大きく出てすぐ消える
    public static class Marker
    {
        static readonly Color White = new(1f, 1f, 1f), Blue = new(0.35f, 0.65f, 1f), Red = new(1f, 0.18f, 0.18f);
        const float Gap = 7f, Len = 10f, Thick = 2f, Side = 1.75f;   // 1920x1080 での大きさ（px）
        const float HitTime = 0.3f, KillTime = 0.55f, PopTime = 0.08f, Pop = 1.3f;

        static GameObject _root;
        static RectTransform _box;
        static Image[] _inner, _outer;
        static float _shownAt = -10f, _life;
        public static Kind Last { get; private set; }
        public static int ShownForTest;
        public static bool HoldForTest;   // テスト用：消さずに出したまま（撮影のため）

        public static bool OffForTest;    // 測定用：出さない

        public static void Show(Kind k)
        {
            if (OffForTest) return;
            Build();
            Last = k;
            ShownForTest++;
            bool kill = k == Kind.Kill || k == Kind.HeadKill;
            bool head = k == Kind.Head || k == Kind.HeadKill;
            var c = kill ? Red : k == Kind.Weak ? Blue : White;
            for (int n = 0; n < 4; n++)
            {
                // 頭：同じ線を横（線と直角の向き）に ±Side ずらして 2 本並べる
                Place(_inner[n], n, head ? -Side : 0f);
                Place(_outer[n], n, Side);
                _inner[n].color = c;
                _outer[n].color = c;
                _outer[n].gameObject.SetActive(head);
            }
            _life = kill ? KillTime : HitTime;
            _shownAt = Time.unscaledTime;
            _root.SetActive(true);
            Tick();
        }

        public static void Tick()
        {
            if (_root == null || !_root.activeSelf) return;
            float t = HoldForTest ? PopTime : Time.unscaledTime - _shownAt;
            if (t >= _life) { _root.SetActive(false); return; }
            float s = t < PopTime ? Mathf.Lerp(Pop, 1f, t / PopTime) : 1f;
            _box.localScale = new Vector3(s, s, 1f);
            float a = t < _life * 0.5f ? 1f : 1f - (t - _life * 0.5f) / (_life * 0.5f);
            foreach (var i in _inner) { var c = i.color; c.a = a; i.color = c; }
            foreach (var o in _outer) { var c = o.color; c.a = a; o.color = c; }
        }

        public static bool VisibleForTest => _root != null && _root.activeSelf;
        public static bool OuterVisibleForTest => _outer != null && _outer[0].gameObject.activeSelf;
        public static Color ColorForTest => _inner?[0].color ?? Color.clear;

        static void Build()
        {
            if (_root != null) return;
            _root = new GameObject("FPHitMarker");
            Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;
            var box = new GameObject("Box");
            box.transform.SetParent(_root.transform, false);
            _box = box.AddComponent<RectTransform>();
            _box.anchorMin = _box.anchorMax = new Vector2(0.5f, 0.5f);
            _box.sizeDelta = Vector2.zero;
            _inner = new Image[4];
            _outer = new Image[4];
            for (int k = 0; k < 4; k++)
            {
                _inner[k] = Line();
                _outer[k] = Line();
            }
            _root.SetActive(false);
        }

        // n 番目の線（中心から 45°+90°×n の向き、Gap 離れたところから Len の長さ）を、線と直角の向きに side ずらして置く
        static void Place(Image img, int n, float side)
        {
            var rt = img.rectTransform;
            float deg = 45f + 90f * n, r = deg * Mathf.Deg2Rad;
            var along = new Vector2(Mathf.Cos(r), Mathf.Sin(r));
            rt.anchoredPosition = along * (Gap + Len * 0.5f) + new Vector2(-along.y, along.x) * side;
            rt.localRotation = Quaternion.Euler(0f, 0f, deg);
        }

        static Image Line()
        {
            var go = new GameObject("Line");
            go.transform.SetParent(_box, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(Len, Thick);
            var img = go.AddComponent<Image>();
            img.raycastTarget = false;
            var ol = go.AddComponent<Outline>();   // 明るい壁の前でも見えるように黒い縁
            ol.effectColor = new Color(0f, 0f, 0f, 0.55f);
            ol.effectDistance = new Vector2(1f, -1f);
            return img;
        }
    }
}
