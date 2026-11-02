using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Il2Cpp;
using UnityEngine;
using DemoWeapon = Il2CppDemo.Scripts.Runtime.Item.Weapon;

using FPModOption;

namespace FPCustom
{
    // 調査用（autotest -Scenario sightlab のときだけ動く）。ゲームの状態は変えない。
    // 各武器のプレハブを地下に複製し、横から撮影して部品（描画物）の一覧・照準点の位置・上面の高さを書き出す。
    // 武器の見た目を横・後ろから撮る道具（調査・テスト用。FPRarity の SightLab から、サイトの調査以外を写したもの）
    public static class SightLab
    {
        static string ShotDir => Path.Combine(Main.Dir, "shots");
        public static readonly Vector3 Origin = new(0, -1000, 0);

        public static GameObject CloneStatic(GameObject prefab, out GameObject holder)
        {
            holder = new GameObject("FPR_LabHolder");
            holder.SetActive(false);
            holder.transform.position = Origin;
            var go = UnityEngine.Object.Instantiate(prefab, holder.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
            foreach (var an in go.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(an);
            return go;
        }

        public static string RelPath(Transform t, Transform root)
        {
            var a = new List<string>();
            for (; t != null && t != root; t = t.parent) a.Insert(0, t.name);
            return string.Join("/", a);
        }

        public static bool IsMesh(Renderer r) => r.TryCast<MeshRenderer>() != null || r.TryCast<SkinnedMeshRenderer>() != null;

        // サイトを載せる武器：上面の高さを測り、載せた状態を横と覗いた画で撮る
        public static Bounds LocalBounds(Renderer r, Transform root)
        {
            var wb = r.bounds;
            var c = wb.center; var e = wb.extents;
            var b = new Bounds(root.InverseTransformPoint(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
                b.Encapsulate(root.InverseTransformPoint(c + Vector3.Scale(e, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return b;
        }

        public static readonly Color Bg = new(0.55f, 0.6f, 0.65f, 1f);
        public const int W = 1600, H = 800;

        // 撮影結果。画素 → 武器の根元の座標への換算に使う
        public class Shot
        {
            public Texture2D Tex; public Vector3 Center; public float HalfH;
            public float ZAt(int px) => Center.z + (px - W / 2f) / (W / 2f) * HalfH * 2f;
            public float YAt(int py) => Center.y + (py - H / 2f) / (H / 2f) * HalfH; // py は下から
            public int PxOfZ(float z) => Mathf.RoundToInt(W / 2f + (z - Center.z) / (HalfH * 2f) * (W / 2f));
            public int PyOfY(float y) => Mathf.RoundToInt(H / 2f + (y - Center.y) / HalfH * (H / 2f));
            public void Dispose() { if (Tex != null) UnityEngine.Object.Destroy(Tex); }
        }

        public static Bounds VisibleBounds(GameObject go)
        {
            var root = go.transform;
            var all = new Bounds();
            bool first = true;
            foreach (var r in go.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy || !IsMesh(r)) continue;
                var b = LocalBounds(r, root);
                if (first) { all = b; first = false; } else all.Encapsulate(b);
            }
            return all;
        }

        // 横（武器の +X 側から、正射影）で撮影する。画の右が銃口側（+Z）
        public static Shot RenderSide(GameObject go, float halfH, Vector3 center, bool left = false)
        {
            var root = go.transform;
            var camGo = new GameObject("FPR_LabCam");
            var cam = camGo.AddComponent<Camera>();
            var lightGo = new GameObject("FPR_LabLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            var dir = left ? -root.right : root.right;
            camGo.transform.position = root.TransformPoint(center) + dir * 3f;
            camGo.transform.rotation = Quaternion.LookRotation(-dir, root.up);
            lightGo.transform.rotation = Quaternion.LookRotation(-dir - root.up * 0.5f, root.up);
            cam.orthographic = true;
            cam.orthographicSize = halfH;
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 10f;
            var tex = Capture(cam);
            UnityEngine.Object.Destroy(camGo); UnityEngine.Object.Destroy(lightGo);
            return new Shot { Tex = tex, Center = center, HalfH = halfH };
        }

        // 照準点の後ろから前を覗いた画（透視投影）
        // 真後ろから（正射影）。画の上が +Y、右が +X。台座やサイトの左右の傾きを見る
        public static Texture2D RenderBack(GameObject go, float halfH, Vector3 center)
        {
            var root = go.transform;
            var camGo = new GameObject("FPR_LabCam");
            var cam = camGo.AddComponent<Camera>();
            var lightGo = new GameObject("FPR_LabLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            camGo.transform.position = root.TransformPoint(center) - root.forward * 1.5f;
            camGo.transform.rotation = Quaternion.LookRotation(root.forward, root.up);
            lightGo.transform.rotation = Quaternion.LookRotation(root.forward - root.up * 0.5f + root.right * 0.3f, root.up);
            cam.orthographic = true;
            cam.orthographicSize = halfH;
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 3f;
            var tex = Capture(cam);
            UnityEngine.Object.Destroy(camGo); UnityEngine.Object.Destroy(lightGo);
            return tex;
        }

        // 真正面から（正射影）。画の上が +Y、右が -X。銃口の穴の高さを見る
        public static Shot RenderFront(GameObject go, float halfH, Vector3 center)
        {
            var root = go.transform;
            var camGo = new GameObject("FPR_LabCam");
            var cam = camGo.AddComponent<Camera>();
            var lightGo = new GameObject("FPR_LabLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            camGo.transform.position = root.TransformPoint(center) + root.forward * 1.5f;
            camGo.transform.rotation = Quaternion.LookRotation(-root.forward, root.up);
            lightGo.transform.rotation = Quaternion.LookRotation(-root.forward - root.up * 0.5f + root.right * 0.3f, root.up);
            cam.orthographic = true;
            cam.orthographicSize = halfH;
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 3f;
            var tex = Capture(cam);
            UnityEngine.Object.Destroy(camGo); UnityEngine.Object.Destroy(lightGo);
            return new Shot { Tex = tex, Center = center, HalfH = halfH };
        }

        // 真上から（正射影）。画の上が銃口側（+Z）、右が +X。mask を指定するとその層だけ写す（手に持った武器用）
        public static Texture2D RenderTop(GameObject go, float halfH, Vector3 center, int mask = -1)
        {
            var root = go.transform;
            var camGo = new GameObject("FPR_LabCam");
            var cam = camGo.AddComponent<Camera>();
            var lightGo = new GameObject("FPR_LabLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            camGo.transform.position = root.TransformPoint(center) + root.up * 2f;
            camGo.transform.rotation = Quaternion.LookRotation(-root.up, root.forward);
            lightGo.transform.rotation = Quaternion.LookRotation(-root.up + root.right * 0.3f, root.forward);
            cam.orthographic = true;
            cam.orthographicSize = halfH;
            cam.nearClipPlane = 0.01f; cam.farClipPlane = 4f;
            cam.cullingMask = mask;
            var tex = Capture(cam);
            UnityEngine.Object.Destroy(camGo); UnityEngine.Object.Destroy(lightGo);
            return tex;
        }

        public static Texture2D RenderEye(GameObject go, Vector3 eyeLocal, float fov)
        {
            var root = go.transform;
            var camGo = new GameObject("FPR_LabCam");
            var cam = camGo.AddComponent<Camera>();
            var lightGo = new GameObject("FPR_LabLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            camGo.transform.position = root.TransformPoint(eyeLocal);
            camGo.transform.rotation = Quaternion.LookRotation(root.forward, root.up);
            lightGo.transform.rotation = Quaternion.LookRotation(root.forward - root.up * 0.5f + root.right * 0.3f, root.up);
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.005f; cam.farClipPlane = 10f;
            var tex = Capture(cam);
            UnityEngine.Object.Destroy(camGo); UnityEngine.Object.Destroy(lightGo);
            return tex;
        }

        static Texture2D Capture(Camera cam)
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Bg;
            var rt = new RenderTexture(W, H, 24);
            cam.targetTexture = rt;
            cam.aspect = (float)W / H;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            UnityEngine.Object.Destroy(rt);
            return tex;
        }

        public static bool IsBg(Color c) => Mathf.Abs(c.r - Bg.r) + Mathf.Abs(c.g - Bg.g) + Mathf.Abs(c.b - Bg.b) < 0.05f;

        // その前後位置での一番上の描画位置（武器の上面の高さ）。何もなければ NaN
        public static float TopAt(Shot s, float z)
        {
            int px = s.PxOfZ(z);
            if (px < 0 || px >= W) return float.NaN;
            var col = s.Tex.GetPixels(px, 0, 1, H);
            for (int py = H - 1; py >= 0; py--)
                if (!IsBg(col[py])) return s.YAt(py);
            return float.NaN;
        }

        // 上から続いている部分の下端（ハンドガンのフレームの前側の下面＝ブリッジマウントのクランプの位置を決める）
        public static float RunBottomAt(Shot s, float z)
        {
            int px = s.PxOfZ(z);
            if (px < 0 || px >= W) return float.NaN;
            var col = s.Tex.GetPixels(px, 0, 1, H);
            int py = H - 1;
            while (py >= 0 && IsBg(col[py])) py--;
            if (py < 0) return float.NaN;
            while (py >= 0 && !IsBg(col[py])) py--;
            return s.YAt(py + 1);
        }

        // 1cm ごとに白い目盛り線、5cm ごとに黄色い線を描いて保存する
        public static void SaveWithGrid(Shot s, string name)
        {
            var tex = s.Tex;
            var minor = new Color(1f, 1f, 1f, 1f); var major = new Color(1f, 0.9f, 0f, 1f);
            for (int cm = -60; cm <= 60; cm++)
            {
                float v = cm / 100f;
                bool mj = cm % 5 == 0;
                int px = s.PxOfZ(v);
                if (px >= 0 && px < W) for (int py = 0; py < H; py += mj ? 1 : 3) Blend(tex, px, py, mj ? major : minor);
                int y = s.PyOfY(v);
                if (y >= 0 && y < H) for (int x = 0; x < W; x += mj ? 1 : 3) Blend(tex, x, y, mj ? major : minor);
            }
            tex.Apply();
            Save(tex, name);
        }

        static void Blend(Texture2D t, int x, int y, Color c) => t.SetPixel(x, y, Color.Lerp(t.GetPixel(x, y), c, 0.45f));

        public static void Save(Texture2D tex, string name)
        {
            File.WriteAllBytes(Path.Combine(ShotDir, name + ".png"), ImageConversion.EncodeToPNG(tex));
            Main.Log($"[lab] saved {name}");
        }

        // 全体を横から撮って保存
        public static void Shoot(GameObject go, string name, bool side, float zoom = 1f, Vector3? focus = null)
        {
            var all = VisibleBounds(go);
            var s = RenderSide(go, Mathf.Max(all.extents.z / 2f, all.extents.y) * 1.1f / zoom, focus ?? all.center);
            Save(s.Tex, name);
            s.Dispose();
        }
    }
}
