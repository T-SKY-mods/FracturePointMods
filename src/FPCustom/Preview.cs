using System;
using FPModOption;
using UnityEngine;

namespace FPCustom
{
    // カスタマイズ画面の「横から見た武器」。武器のプレハブを複製して今の装着を付け（Mount.Apply）、横から正射影で RenderTexture に描く。
    // 複製は描いたらすぐ消す（画面に出すのは RenderTexture だけ）。付け外し・武器の選択のたびに描き直す
    public static class Preview
    {
        public const int W = 1490, H = 320;   // 表示の枠（CustomUi の 745x160）の 2 倍。枠と同じ縦横比にする（違うと伸びて見える）
        static readonly Vector3 Origin = new(0f, -2000f, 0f);
        static readonly Color Bg = new(0.13f, 0.13f, 0.13f, 1f);
        static RenderTexture _rt;
        // テスト用：最後に描いた絵の中心（武器の根元の座標の z, y）と 1 画素の大きさ
        public static Vector2 LastCenter; public static float LastUnit;

        public static RenderTexture Texture
        {
            get
            {
                if (_rt == null || !_rt.IsCreated()) { _rt = new RenderTexture(W, H, 24) { name = "FPC_PreviewRT" }; _rt.Create(); }
                return _rt;
            }
        }

        public static void Render(int id, bool top = false)
        {
            var prefab = Tracker.ItemById(id)?.Weapon?.gameObject;
            if (prefab == null) return;
            // 非アクティブの親の下に複製（スクリプトは動かない）。装着を付けてからスクリプトを消して表示する
            var holder = new GameObject("FPC_PreviewHolder");
            holder.SetActive(false);
            holder.transform.position = Origin;
            GameObject camGo = null, lightGo = null;
            try
            {
                var go = UnityEngine.Object.Instantiate(prefab, holder.transform, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                try { Mount.Apply(go, prefab, id); } catch (Exception e) { Main.Log("[preview] mount failed: " + e.Message); }
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
                foreach (var an in go.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(an);
                Lab.HideNonMesh(go);
                // 骨を差し替えた本体（MPS-5 のグリップ）は描画の範囲が古いままで写らないので、毎回範囲を計算させる
                foreach (var sk in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) sk.updateWhenOffscreen = true;
                holder.SetActive(true);

                // 上から見るとき（確認用）は銃を横に倒して、上面をカメラに向ける
                if (top) go.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);
                var root = holder.transform;
                var b = SightLab.VisibleBounds(go);
                if (top) b = new Bounds(new Vector3(0f, 0f, b.center.z), new Vector3(0.1f, 0.1f, b.size.z));
                float aspect = (float)W / H;
                camGo = new GameObject("FPC_PreviewCam");
                var cam = camGo.AddComponent<Camera>();
                camGo.transform.rotation = Quaternion.LookRotation(-root.right, root.up);   // 画の右が銃口
                cam.orthographic = true;
                cam.nearClipPlane = 0.01f; cam.farClipPlane = 10f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = Bg;
                cam.aspect = aspect;
                lightGo = new GameObject("FPC_PreviewLight");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 2f;
                lightGo.transform.rotation = Quaternion.LookRotation(-root.right - root.up * 0.5f, root.up);
                // 1 回目：描画の範囲（見えない部品の範囲も入る）で広めに小さく描き、実際に描かれた範囲を測る
                var c = new Vector2(b.center.z, b.center.y);
                float halfH = Mathf.Max(b.extents.y, b.extents.z / aspect) * 1.3f;
                if (Measure(cam, root, c, halfH, aspect) is Rect r)
                {
                    c = r.center;
                    halfH = Mathf.Max(r.height / 2f, r.width / 2f / aspect) * 1.08f;
                }
                // 2 回目：測った範囲に合わせて表示用に描く
                camGo.transform.position = root.TransformPoint(new Vector3(0f, c.y, c.x)) + root.right * 3f;
                cam.orthographicSize = halfH;
                LastCenter = c; LastUnit = halfH * 2f / H;
                cam.targetTexture = Texture;
                cam.Render();
                cam.targetTexture = null;
            }
            catch (Exception e) { Main.Log("[preview] render failed: " + e); }
            finally
            {
                if (camGo != null) UnityEngine.Object.Destroy(camGo);
                if (lightGo != null) UnityEngine.Object.Destroy(lightGo);
                UnityEngine.Object.Destroy(holder);
            }
        }

        // 小さく描いて、背景でない画素の範囲を武器の根元の座標（x = 前後 z、y = 高さ）で返す
        static Rect? Measure(Camera cam, Transform root, Vector2 c, float halfH, float aspect)
        {
            const int w = 447, h = 150;   // 表示と同じ縦横比
            var rt = new RenderTexture(w, h, 24);
            cam.transform.position = root.TransformPoint(new Vector3(0f, c.y, c.x)) + root.right * 3f;
            cam.orthographicSize = halfH;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            var px = tex.GetPixels();
            UnityEngine.Object.Destroy(tex); UnityEngine.Object.Destroy(rt);
            int x0 = w, x1 = -1, y0 = h, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = px[y * w + x];
                    if (Mathf.Abs(p.r - Bg.r) + Mathf.Abs(p.g - Bg.g) + Mathf.Abs(p.b - Bg.b) < 0.03f) continue;
                    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
            if (x1 < 0) return null;
            float unit = halfH * 2f / h;   // 1 画素の大きさ
            float zl = c.x + (x0 - w / 2f) * unit, zr = c.x + (x1 + 1 - w / 2f) * unit;
            float yb = c.y + (y0 - h / 2f) * unit, yt = c.y + (y1 + 1 - h / 2f) * unit;
            return Rect.MinMaxRect(zl, yb, zr, yt);
        }

        // テスト用：今の絵を PNG に
        public static void Save(string path)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = Texture;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            System.IO.File.WriteAllBytes(path, ImageConversion.EncodeToPNG(tex));
            UnityEngine.Object.Destroy(tex);
        }
    }
}
