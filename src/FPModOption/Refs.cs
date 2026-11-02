using System.Linq;
using Il2Cpp;
using UnityEngine;
using FPSController = Il2CppDemo.Scripts.Runtime.Character.FPSController;

namespace FPModOption
{
    // よく使うゲームオブジェクトの参照をキャッシュする。
    // Resources.FindObjectsOfTypeAll は重く（1回 約25ms。シーン外のプレハブ等も含めて全部調べるため）、
    // 毎フレーム呼ばれる処理で使うと FPS が大きく落ちる・カクつくため、見つかったら使い回す。
    // 見つからないときの再検索は、シーンを読み込んでから 20 秒は 1 秒ごと、その後は 30 秒ごとにする
    // （そのシーンに無い物を毎秒探し続けると、1 秒ごとにカクつくため）。
    public static class Refs
    {
        static Inventory _inv;
        static FPSController _fps;
        static SaveBag _bag;
        static UserInteractions _ui;
        static float _invAt, _fpsAt, _bagAt, _uiAt, _sceneAt;

        public static Inventory Inv => Get(ref _inv, ref _invAt, null);
        public static FPSController Fps => Get(ref _fps, ref _fpsAt, f => f.InstantiatedWeapons != null);
        // 保管庫は拠点にしかない。ランの間は探さない
        public static SaveBag Bag => Core.Scene == Core.HubScene ? Get(ref _bag, ref _bagAt, null) : null;
        public static UserInteractions Interactions => Get(ref _ui, ref _uiAt, null);

        public static void Clear()
        {
            _inv = null; _fps = null; _bag = null; _ui = null;
            _invAt = _fpsAt = _bagAt = _uiAt = 0;
            _sceneAt = Time.realtimeSinceStartup;
        }

        static T Get<T>(ref T cached, ref float lastSearch, System.Func<T, bool> filter) where T : Object
        {
            if (Alive(cached)) return cached;
            var now = Time.realtimeSinceStartup;
            float interval = now - _sceneAt < 20f ? 1f : 30f;
            if (now - lastSearch < interval) return null;
            lastSearch = now;
            cached = Core.FindAll<T>().FirstOrDefault(x => filter == null || filter(x));
            return cached;
        }

        public static bool Alive(Object o) => o != null && !o.WasCollected;
    }
}
