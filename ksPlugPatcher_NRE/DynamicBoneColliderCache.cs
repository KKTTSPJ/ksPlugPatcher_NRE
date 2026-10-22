using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ksPlugPatcher_NRE
{
    /// <summary>
    /// 揺れもの（ゲーム本体の DynamicBone）の当たり判定を、結果を変えずに軽くする（0.8.0）。
    /// KK の kPlugPatcher_InviteChrID v1.16.0〜v1.16.1 の DynamicBoneColliderCache の KKS 版（中身は同じ）。
    /// KKS の DynamicBoneCollider は transform を覚えているが、Collide のたびに lossyScale と TransformPoint を呼ぶのは KK と同じ。
    /// UpdateParticles2 のコライダーのループの形（DynamicBone は for 文、Ver01・Ver02 は foreach）も KK と同じ。
    /// 背景（KK で測った値）:
    ///   揺れもの1つの時間は「粒の数 × コライダーの数」でほぼ決まり、1組あたり約0.63µs かかっていた
    ///   （ラオーラの髪 233粒 × コライダー41 で 5.9ms/f）。
    ///   ゲーム本体の UpdateParticles2 は、粒ごとに全部のコライダーについて
    ///   「collider != null」「collider.enabled」「collider.Collide(ref 位置, 半径)」を呼び、
    ///   Collide は呼ばれるたびに transform.lossyScale と transform.TransformPoint（1〜2回）を呼ぶ。
    ///   1組あたり Unity のネイティブ呼び出しが5〜6回あり、計算そのもの（球・カプセルとの距離）は軽い。
    ///
    /// 方法:
    ///   UpdateParticles2 の間は、どのボーンも動かない（揺れものがボーンに書くのは、その後の ApplyParticlesToTransforms）。
    ///   そこで UpdateParticles2 の中で最初にコライダーの一覧を読んだときに、各コライダーの
    ///   「生きているか・有効か（Ver02 は activeInHierarchy も）・ワールドでの中心（カプセルは両端）・半径」を1回だけ求めておき、
    ///   粒ごとの呼び出しは、覚えた値で DynamicBoneCollider の元の距離の計算（private static の OutsideSphere など）を呼ぶ。
    ///   使う値と計算は元の Collide と同じなので、結果は変わらない見込み。
    ///
    /// 当てる場所（Transpiler。コライダーの一覧のループの中の呼び出しだけを差し替える）:
    ///   DynamicBone.UpdateParticles2（for 文: get_Item）、DynamicBone_Ver01 / DynamicBone_Ver02.UpdateParticles2（foreach: GetEnumerator・get_Current）。
    ///   ループの形が想定と違うメソッドは書き換えない。差し替えた呼び出しは、覚えた一覧と違うコライダーが来たら元の処理をそのまま呼ぶ。
    ///
    /// 設定 [DynamicBoneColliderCache] EnableColliderCache はゲーム中に切り替えられる（次の UpdateParticles2 から効く）。
    /// 無効のときは、差し替えた呼び出しが元の処理をそのまま呼ぶ。
    /// 確かめる用の設定 VerifyColliderCache（既定 false、ゲーム中に切り替え可）: 前計算の結果と、元の Collide の結果を毎回比べて、
    /// 違った回数と最大の差を集計のログに出す（元の処理も呼ぶので、その間は重くなる）。
    ///
    /// KK の v1.16.1: ループ全体をまとめる（設定 MergeColliderLoop、既定 true、ゲーム中に切り替え可）。
    ///   v1.16.0 の形では、粒とコライダーの1組ごとに、差し替えた呼び出し（一覧の取り出し・null・enabled・Collide）を4回呼んでいて、
    ///   1組あたり約0.1µs 残っていた。コライダーのループの直前に「CollideAll(一覧, ref 粒の位置, 半径)」の呼び出しを足し、
    ///   前計算が使えるときは、覚えた一覧を順に回して距離の計算を呼び、元のループを飛ばす（使えないときは false を返し、元のループがそのまま動く）。
    ///   コライダーを回る順番と、計算を呼ぶ条件（生きている・有効・Ver02 は activeInHierarchy）は元のループと同じ。
    ///   比べる設定が有効なときは、元のループと同じ処理（本物の Collide）を写しの位置で行って、粒ごとに結果を比べる。
    /// </summary>
    internal static class DynamicBoneColliderCache
    {
        private const string Tag = "[DynamicBoneColliderCache]";
        private const float SummarySeconds = 60f;

        private delegate void SphereFn(ref Vector3 particlePosition, float particleRadius, Vector3 sphereCenter, float sphereRadius);
        private delegate void CapsuleFn(ref Vector3 particlePosition, float particleRadius, Vector3 capsuleP0, Vector3 capsuleP1, float capsuleRadius);

        private static SphereFn _outsideSphere, _insideSphere;
        private static CapsuleFn _outsideCapsule, _insideCapsule;

        private const byte KindOutsideSphere = 0, KindInsideSphere = 1, KindOutsideCapsule = 2, KindInsideCapsule = 3;

        private static bool _applied;
        private static volatile bool _setting;     // 設定の値（SettingChanged で更新）
        private static volatile bool _verifySetting;
        private static volatile bool _mergeSetting;
        private static bool _collidePatchedByOthers;

        // ---- UpdateParticles2 の1回の呼び出しの間だけ使う状態 ----
        private static bool _fast;                 // この呼び出しで前計算を使うか
        private static bool _verify;               // この呼び出しで元の処理と比べるか
        private static bool _merge;                // この呼び出しでループ全体をまとめるか
        private static bool _needActive;           // Ver02（activeInHierarchy も見る）
        private static int _token, _builtToken;
        private static List<DynamicBoneCollider> _list;
        private static int _count;
        private static int _cur = -1;              // 今のコライダーの番号（前計算が使えないときは -1）
        private static int _pos = -1;              // foreach の何番目か

        private static DynamicBoneCollider[] _items = new DynamicBoneCollider[64];
        private static bool[] _alive = new bool[64], _enabled = new bool[64], _active = new bool[64];
        private static byte[] _kind = new byte[64];
        private static Vector3[] _p0 = new Vector3[64], _p1 = new Vector3[64];
        private static float[] _radius = new float[64];

        // ---- 集計 ----
        private static long _pairs, _fallbacks;
        private static long _verified, _mismatches;
        private static float _maxDiff;
        private static bool _mismatchLogged;
        private static int _builds, _calls;
        private static long _loops;
        private static float _windowStart = -1f;

        private static readonly List<string> _patchedNames = new List<string>();
        private static string _transpileResult;

        internal static void TryApply(Harmony harmony)
        {
            if (_applied) return;
            _applied = true;

            try
            {
                var colType = typeof(DynamicBoneCollider);
                _outsideSphere = MakeDelegate<SphereFn>(colType, "OutsideSphere");
                _insideSphere = MakeDelegate<SphereFn>(colType, "InsideSphere");
                _outsideCapsule = MakeDelegate<CapsuleFn>(colType, "OutsideCapsule");
                _insideCapsule = MakeDelegate<CapsuleFn>(colType, "InsideCapsule");
                if (_outsideSphere == null || _insideSphere == null || _outsideCapsule == null || _insideCapsule == null)
                {
                    PatcherPlugin.Logger.LogWarning($"{Tag} DynamicBoneCollider の距離の計算（OutsideSphere など）が見つからないため、適用しません。");
                    return;
                }

                // 他のプラグインが Collide 自体に処理を当てていたら、前計算はそれを飛ばしてしまうので使わない
                var collide = AccessTools.Method(colType, "Collide");
                var info = collide != null ? Harmony.GetPatchInfo(collide) : null;
                if (info != null && (info.Prefixes.Count + info.Postfixes.Count + info.Transpilers.Count + info.Finalizers.Count) > 0)
                {
                    _collidePatchedByOthers = true;
                    PatcherPlugin.Logger.LogWarning($"{Tag} DynamicBoneCollider.Collide に他のプラグインのパッチがあります（{string.Join(", ", info.Owners.ToArray())}）。前計算は使いません。");
                }

                _setting = PatcherPlugin.EnableDynamicBoneColliderCache.Value;
                PatcherPlugin.EnableDynamicBoneColliderCache.SettingChanged += (s, e) =>
                {
                    _setting = PatcherPlugin.EnableDynamicBoneColliderCache.Value;
                    PatcherPlugin.Logger.LogInfo($"{Tag} 設定を変更しました: 前計算={(_setting ? "有効" : "無効")}");
                };
                _verifySetting = PatcherPlugin.VerifyDynamicBoneColliderCache.Value;
                PatcherPlugin.VerifyDynamicBoneColliderCache.SettingChanged += (s, e) =>
                {
                    _verifySetting = PatcherPlugin.VerifyDynamicBoneColliderCache.Value;
                    PatcherPlugin.Logger.LogInfo($"{Tag} 設定を変更しました: 元の処理との比較={(_verifySetting ? "有効" : "無効")}");
                };
                _mergeSetting = PatcherPlugin.MergeDynamicBoneColliderLoop.Value;
                PatcherPlugin.MergeDynamicBoneColliderLoop.SettingChanged += (s, e) =>
                {
                    _mergeSetting = PatcherPlugin.MergeDynamicBoneColliderLoop.Value;
                    PatcherPlugin.Logger.LogInfo($"{Tag} 設定を変更しました: ループ全体をまとめる={(_mergeSetting ? "有効" : "無効")}");
                };

                var transpiler = new HarmonyMethod(AccessTools.Method(typeof(DynamicBoneColliderCache), nameof(Transpiler)));
                PatchOne(harmony, typeof(DynamicBone), nameof(BeginPrefix), transpiler);
                PatchOne(harmony, typeof(DynamicBone_Ver01), nameof(BeginPrefix), transpiler);
                PatchOne(harmony, typeof(DynamicBone_Ver02), nameof(BeginPrefixVer02), transpiler);

                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} 適用しました: {(_patchedNames.Count > 0 ? string.Join("、", _patchedNames.ToArray()) : "なし")} / " +
                    $"前計算={(_setting ? "有効" : "無効")}・ループ全体をまとめる={(_mergeSetting ? "有効" : "無効")}" +
                    $"{(_collidePatchedByOthers ? "（Collide に他のパッチがあるため使わない）" : "")}");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗: {Describe(ex)}");
            }
        }

        private static string Describe(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
                parts.Add($"{e.GetType().Name}: {e.Message}");
            return string.Join(" ← ", parts.ToArray());
        }

        private static T MakeDelegate<T>(Type type, string name) where T : class
        {
            var mi = AccessTools.Method(type, name);
            if (mi == null || !mi.IsStatic) return null;
            try { return Delegate.CreateDelegate(typeof(T), mi) as T; }
            catch { return null; }
        }

        private static void PatchOne(Harmony harmony, Type type, string prefixName, HarmonyMethod transpiler)
        {
            var target = AccessTools.Method(type, "UpdateParticles2");
            if (target == null)
            {
                PatcherPlugin.Logger.LogInfo($"{Tag} {type.Name}.UpdateParticles2 が見つかりません（対象外）。");
                return;
            }
            _transpileResult = null;
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(AccessTools.Method(typeof(DynamicBoneColliderCache), prefixName)), transpiler: transpiler);
            }
            catch (Exception ex)
            {
                // 1つが失敗しても他は当てる。原因が分かるように内側の例外まで出す
                PatcherPlugin.Logger.LogError($"{Tag} {type.Name}.UpdateParticles2 への適用に失敗（{_transpileResult ?? "書き換え前"}）: {Describe(ex)}");
                return;
            }
            if (_transpileResult == null || _transpileResult.StartsWith("ok"))
            {
                _patchedNames.Add(type.Name + (_transpileResult != null && _transpileResult.Contains("ループをまとめた") ? "" : "（ループはまとめない）"));
                if (_transpileResult != null && !_transpileResult.Contains("ループをまとめた"))
                    PatcherPlugin.Logger.LogWarning($"{Tag} {type.Name}.UpdateParticles2: {_transpileResult}");
            }
            else
            {
                PatcherPlugin.Logger.LogWarning($"{Tag} {type.Name}.UpdateParticles2 は、コライダーのループの形が想定と違うため書き換えませんでした（{_transpileResult}）。");
            }

            var others = Harmony.GetPatchInfo(target);
            if (others != null)
            {
                var owners = others.Owners.Where(o => o != harmony.Id).ToArray();
                if (owners.Length > 0)
                    PatcherPlugin.Logger.LogInfo($"{Tag} {type.Name}.UpdateParticles2 には他のプラグインのパッチもあります: {string.Join(", ", owners)}");
            }
        }

        // ===================== Transpiler =====================

        // 型と名前で比べる（ジェネリック型のメソッドは、取り方で MethodInfo の実体が違うことがあるため）
        private static bool Is(MethodInfo mi, Type type, string name) => mi.DeclaringType == type && mi.Name == name;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var original = instructions.ToList();
            // 写しはコピーコンストラクタで作る（Clone() は分岐先のラベルと try/finally の印を消すので、ループが壊れる）
            var codes = original.Select(c => new CodeInstruction(c)).ToList();
            int getItem = 0, getEnum = 0, getCurrent = 0, ineq = 0, enabled = 0, go = 0, active = 0, collide = 0;
            bool inLoop = false;
            var listType = typeof(List<DynamicBoneCollider>);
            var enumType = typeof(List<DynamicBoneCollider>.Enumerator);

            foreach (var c in codes)
            {
                if (c.opcode != OpCodes.Call && c.opcode != OpCodes.Callvirt) continue;
                var mi = c.operand as MethodInfo;
                if (mi == null) continue;

                if (Is(mi, listType, "get_Item")) { Replace(c, nameof(GetItem)); getItem++; inLoop = true; }
                else if (Is(mi, listType, "GetEnumerator")) { Replace(c, nameof(GetEnumerator)); getEnum++; }
                else if (Is(mi, enumType, "get_Current")) { Replace(c, nameof(Current)); getCurrent++; inLoop = true; }
                else if (inLoop && Is(mi, typeof(UnityEngine.Object), "op_Inequality")) { Replace(c, nameof(NotNull)); ineq++; }
                else if (inLoop && Is(mi, typeof(Behaviour), "get_enabled")) { Replace(c, nameof(Enabled)); enabled++; }
                else if (inLoop && Is(mi, typeof(Component), "get_gameObject")) { Replace(c, nameof(GameObjectOf)); go++; }
                else if (inLoop && Is(mi, typeof(GameObject), "get_activeInHierarchy")) { Replace(c, nameof(ActiveInHierarchy)); active++; }
                else if (Is(mi, typeof(DynamicBoneCollider), "Collide")) { Replace(c, nameof(Collide)); collide++; inLoop = false; }
            }

            // 想定の形: for 文（get_Item 1・null 1・enabled 1・Collide 1）か、foreach（GetEnumerator 1・Current 1・null 1・enabled 1・Collide 1。Ver02 は gameObject・activeInHierarchy も1つずつ）
            bool forLoop = getItem == 1 && getEnum == 0 && getCurrent == 0;
            bool foreachLoop = getItem == 0 && getEnum == 1 && getCurrent == 1;
            bool ok = (forLoop || foreachLoop) && ineq == 1 && enabled == 1 && collide == 1 && go == active && go <= 1;
            _transpileResult = (ok ? "ok" : "ng") +
                               $": get_Item {getItem}・GetEnumerator {getEnum}・Current {getCurrent}・null {ineq}・enabled {enabled}・gameObject {go}・activeInHierarchy {active}・Collide {collide}";
            if (!ok) return original;

            // v1.16.1: ループの直前に CollideAll を足す（形が想定と違えば足さず、1組ごとの差し替えだけで動く）
            string why;
            if (InjectWholeLoop(codes, il, forLoop, out why)) _transpileResult += "・ループをまとめた";
            else _transpileResult += $"・ループはまとめない（{why}）";
            return codes;
        }

        private static bool IsOurs(CodeInstruction c, string name)
        {
            var mi = c.operand as MethodInfo;
            return c.opcode == OpCodes.Call && mi != null && mi.DeclaringType == typeof(DynamicBoneColliderCache) && mi.Name == name;
        }

        /// <summary>ldloc / stloc の変数の番号（ldloc.0〜3・ldloc.s・ldloc、LocalBuilder・数値のどちらの operand でも）。違う命令なら -1。</summary>
        private static int LocalIndex(CodeInstruction c, bool store)
        {
            var op = c.opcode;
            if (!store)
            {
                if (op == OpCodes.Ldloc_0) return 0;
                if (op == OpCodes.Ldloc_1) return 1;
                if (op == OpCodes.Ldloc_2) return 2;
                if (op == OpCodes.Ldloc_3) return 3;
                if (op != OpCodes.Ldloc_S && op != OpCodes.Ldloc) return -1;
            }
            else
            {
                if (op == OpCodes.Stloc_0) return 0;
                if (op == OpCodes.Stloc_1) return 1;
                if (op == OpCodes.Stloc_2) return 2;
                if (op == OpCodes.Stloc_3) return 3;
                if (op != OpCodes.Stloc_S && op != OpCodes.Stloc) return -1;
            }
            var lb = c.operand as LocalBuilder;
            if (lb != null) return lb.LocalIndex;
            if (c.operand is byte) return (byte)c.operand;
            if (c.operand is short) return (short)c.operand;
            if (c.operand is int) return (int)c.operand;
            return -1;
        }

        /// <summary>
        /// コライダーのループの直前に「一覧・粒の位置（ref）・半径を積んで CollideAll を呼び、true ならループの後へ飛ぶ」命令を足す。
        /// 一覧・位置・半径を積む命令は、元のループの中のもの（一覧の取り出しの前の ldarg.0・ldfld、Collide の前の3命令）の写しを使う。
        /// </summary>
        private static bool InjectWholeLoop(List<CodeInstruction> codes, ILGenerator il, bool forLoop, out string why)
        {
            why = "";
            int collideIdx = codes.FindIndex(c => IsOurs(c, nameof(Collide)));
            if (collideIdx < 3) { why = "Collide が見つからない"; return false; }
            var posA = codes[collideIdx - 3];
            var posB = codes[collideIdx - 2];
            var rad = codes[collideIdx - 1];
            if (LocalIndex(posA, false) < 0 || posB.opcode != OpCodes.Ldflda || LocalIndex(rad, false) < 0)
            {
                why = "Collide の引数の積み方が想定と違う";
                return false;
            }

            int listIdx, insertAt;
            Label exitLabel;
            if (forLoop)
            {
                int itemIdx = codes.FindIndex(c => IsOurs(c, nameof(GetItem)));
                listIdx = itemIdx - 3;
                if (listIdx < 0 || codes[listIdx].opcode != OpCodes.Ldarg_0 || codes[listIdx + 1].opcode != OpCodes.Ldfld) { why = "一覧の読み方が想定と違う"; return false; }
                int j = LocalIndex(codes[itemIdx - 1], false);
                // ループの初期化「ldc.i4.0 ; stloc j ; br 条件」を探す
                insertAt = -1;
                for (int k = listIdx - 1; k >= 0 && k + 2 < codes.Count; k--)
                {
                    if (codes[k].opcode == OpCodes.Ldc_I4_0 && LocalIndex(codes[k + 1], true) == j && j >= 0 &&
                        (codes[k + 2].opcode == OpCodes.Br || codes[k + 2].opcode == OpCodes.Br_S))
                    {
                        insertAt = k;
                        break;
                    }
                }
                if (insertAt < 0) { why = "ループの初期化が見つからない"; return false; }
                // ループの終わり: Collide の後で最初の blt（条件の判定から本体へ戻る分岐）の次の命令
                int bltIdx = -1;
                for (int k = collideIdx + 1; k < codes.Count; k++)
                    if (codes[k].opcode == OpCodes.Blt || codes[k].opcode == OpCodes.Blt_S) { bltIdx = k; break; }
                if (bltIdx < 0 || bltIdx + 1 >= codes.Count) { why = "ループの終わりが見つからない"; return false; }
                exitLabel = il.DefineLabel();
                codes[bltIdx + 1].labels.Add(exitLabel);
            }
            else
            {
                int enumIdx = codes.FindIndex(c => IsOurs(c, nameof(GetEnumerator)));
                listIdx = enumIdx - 2;
                if (listIdx < 0 || codes[listIdx].opcode != OpCodes.Ldarg_0 || codes[listIdx + 1].opcode != OpCodes.Ldfld) { why = "一覧の読み方が想定と違う"; return false; }
                insertAt = listIdx;
                // ループの終わり: try を抜ける leave の行き先（finally の後）
                int leaveIdx = -1;
                for (int k = collideIdx + 1; k < codes.Count; k++)
                    if (codes[k].opcode == OpCodes.Leave || codes[k].opcode == OpCodes.Leave_S) { leaveIdx = k; break; }
                if (leaveIdx < 0 || !(codes[leaveIdx].operand is Label)) { why = "try を抜ける leave が見つからない"; return false; }
                exitLabel = (Label)codes[leaveIdx].operand;
            }

            var seq = new List<CodeInstruction>
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, codes[listIdx + 1].operand),
                new CodeInstruction(posA.opcode, posA.operand),
                new CodeInstruction(posB.opcode, posB.operand),
                new CodeInstruction(rad.opcode, rad.operand),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DynamicBoneColliderCache), nameof(CollideAll))),
                new CodeInstruction(OpCodes.Brtrue, exitLabel),
            };
            // 元の命令に付いていた分岐先のラベルは、足した命令の先頭へ移す（そこへ飛んできたときも CollideAll を通る）
            codes[insertAt].MoveLabelsTo(seq[0]);
            codes.InsertRange(insertAt, seq);
            return true;
        }

        private static void Replace(CodeInstruction c, string name)
        {
            c.opcode = OpCodes.Call;
            c.operand = AccessTools.Method(typeof(DynamicBoneColliderCache), name);
        }

        // ===================== UpdateParticles2 の最初 =====================

        private static void BeginPrefix() => Begin(false);
        private static void BeginPrefixVer02() => Begin(true);

        private static void Begin(bool needActive)
        {
            _token++;
            _fast = _setting && !_collidePatchedByOthers;
            _verify = _fast && _verifySetting;
            _merge = _fast && _mergeSetting;
            _needActive = needActive;
            _list = null;
            _cur = -1;
            _pos = -1;
            _calls++;
            if ((_calls & 255) == 0) MaybeLogSummary();
        }

        private static void EnsureList(List<DynamicBoneCollider> list)
        {
            if (ReferenceEquals(list, _list) && _builtToken == _token) return;
            _list = list;
            _builtToken = _token;
            int n = list.Count;
            if (_items.Length < n)
            {
                int size = Math.Max(n, _items.Length * 2);
                _items = new DynamicBoneCollider[size];
                _alive = new bool[size]; _enabled = new bool[size]; _active = new bool[size];
                _kind = new byte[size];
                _p0 = new Vector3[size]; _p1 = new Vector3[size];
                _radius = new float[size];
            }
            for (int k = 0; k < n; k++)
            {
                var c = list[k];
                _items[k] = c;
                bool alive = c != null;
                bool en = alive && c.enabled;
                _alive[k] = alive;
                _enabled[k] = en;
                _active[k] = en && (!_needActive || c.gameObject.activeInHierarchy);
                if (en) Compute(c, k);
            }
            _count = n;
            _builds++;
        }

        /// <summary>DynamicBoneCollider.Collide が粒ごとに求めている値（半径・中心・カプセルの両端）を、同じ式で1回だけ求める。</summary>
        private static void Compute(DynamicBoneCollider c, int k)
        {
            Transform t = c.transform;
            _radius[k] = c.m_Radius * Mathf.Abs(t.lossyScale.z);
            float half = (c.m_Height - c.m_Radius) * 0.5f;
            bool outside = c.m_Bound == DynamicBoneCollider.Bound.Outside;
            if (half <= 0f)
            {
                _kind[k] = outside ? KindOutsideSphere : KindInsideSphere;
                _p0[k] = t.TransformPoint(c.m_Center);
                return;
            }
            Vector3 a = c.m_Center, b = c.m_Center;
            switch (c.m_Direction)
            {
                case DynamicBoneCollider.Direction.X: a.x -= half; b.x += half; break;
                case DynamicBoneCollider.Direction.Y: a.y -= half; b.y += half; break;
                case DynamicBoneCollider.Direction.Z: a.z -= half; b.z += half; break;
            }
            _kind[k] = outside ? KindOutsideCapsule : KindInsideCapsule;
            _p0[k] = t.TransformPoint(a);
            _p1[k] = t.TransformPoint(b);
        }

        // ===================== 差し替えた呼び出し =====================

        /// <summary>
        /// ループ全体の代わり（v1.16.1）。前計算が使えるときは、覚えた一覧を元のループと同じ順番・同じ条件で回して true を返す（元のループは飛ばす）。
        /// 使えないときは false を返し、元のループ（1組ごとの差し替え）がそのまま動く。
        /// </summary>
        private static bool CollideAll(List<DynamicBoneCollider> list, ref Vector3 particlePosition, float particleRadius)
        {
            if (!_merge || list == null) return false;
            EnsureList(list);
            int n = _count;
            if (_verify)
            {
                // 元のループと同じ処理を、写しの位置で行う
                Vector3 before = particlePosition, byOriginal = particlePosition;
                for (int k = 0; k < list.Count; k++)
                {
                    var c = list[k];
                    if (c != null && c.enabled && (!_needActive || c.gameObject.activeInHierarchy))
                        c.Collide(ref byOriginal, particleRadius);
                }
                for (int k = 0; k < n; k++)
                    if (_active[k]) CollideCached(k, ref particlePosition, particleRadius);
                _verified++;
                if (byOriginal.x != particlePosition.x || byOriginal.y != particlePosition.y || byOriginal.z != particlePosition.z)
                {
                    _mismatches++;
                    float d = (byOriginal - particlePosition).magnitude;
                    if (d > _maxDiff) _maxDiff = d;
                    if (!_mismatchLogged)
                    {
                        _mismatchLogged = true;
                        PatcherPlugin.Logger.LogWarning($"{Tag} 元の処理と結果が違いました（最初の1回、ループ全体）: コライダー {n}個 / 粒の位置 {V(before)} → 元 {V(byOriginal)}・前計算 {V(particlePosition)}（差 {d:G6}）");
                    }
                }
            }
            else
            {
                for (int k = 0; k < n; k++)
                    if (_active[k]) CollideCached(k, ref particlePosition, particleRadius);
            }
            _pairs += n;
            _loops++;
            return true;
        }

        // for 文: m_Colliders[j]
        private static DynamicBoneCollider GetItem(List<DynamicBoneCollider> list, int index)
        {
            var c = list[index];
            if (!_fast) { _cur = -1; return c; }
            EnsureList(list);
            _cur = index < _count && ReferenceEquals(c, _items[index]) ? index : -1;
            return c;
        }

        // foreach: m_Colliders.GetEnumerator()
        private static List<DynamicBoneCollider>.Enumerator GetEnumerator(List<DynamicBoneCollider> list)
        {
            _pos = -1;
            _cur = -1;
            if (_fast) EnsureList(list);
            return list.GetEnumerator();
        }

        // foreach: enumerator.Current
        private static DynamicBoneCollider Current(ref List<DynamicBoneCollider>.Enumerator e)
        {
            var c = e.Current;
            if (!_fast || _list == null) { _cur = -1; return c; }
            _pos++;
            _cur = _pos < _count && ReferenceEquals(c, _items[_pos]) ? _pos : -1;
            return c;
        }

        // collider != null
        private static bool NotNull(UnityEngine.Object a, UnityEngine.Object b)
        {
            if (_cur >= 0 && (object)b == null && ReferenceEquals(a, _items[_cur])) return _alive[_cur];
            return a != b;
        }

        // collider.enabled
        private static bool Enabled(Behaviour b)
        {
            if (_cur >= 0 && ReferenceEquals(b, _items[_cur])) return _enabled[_cur];
            return b.enabled;
        }

        // collider.gameObject（Ver02）。前計算を使うときは null を返し、続く ActiveInHierarchy が覚えた値を返す
        private static GameObject GameObjectOf(Component c)
        {
            if (_cur >= 0 && _needActive && ReferenceEquals(c, _items[_cur])) return null;
            return c.gameObject;
        }

        // gameObject.activeInHierarchy（Ver02）
        private static bool ActiveInHierarchy(GameObject go)
        {
            if ((object)go == null && _cur >= 0 && _needActive) return _active[_cur];
            return go.activeInHierarchy;
        }

        // collider.Collide(ref 位置, 半径)
        private static void Collide(DynamicBoneCollider c, ref Vector3 particlePosition, float particleRadius)
        {
            int k = _cur;
            if (k < 0 || !ReferenceEquals(c, _items[k]))
            {
                if (_fast) _fallbacks++;
                c.Collide(ref particlePosition, particleRadius);
                return;
            }
            _pairs++;
            if (_verify)
            {
                Vector3 before = particlePosition, byOriginal = particlePosition;
                c.Collide(ref byOriginal, particleRadius);
                CollideCached(k, ref particlePosition, particleRadius);
                _verified++;
                // 完全に同じ値かを比べる（Vector3 の == は誤差を許すので使わない）
                if (byOriginal.x != particlePosition.x || byOriginal.y != particlePosition.y || byOriginal.z != particlePosition.z)
                {
                    _mismatches++;
                    float d = (byOriginal - particlePosition).magnitude;
                    if (d > _maxDiff) _maxDiff = d;
                    if (!_mismatchLogged)
                    {
                        _mismatchLogged = true;
                        PatcherPlugin.Logger.LogWarning($"{Tag} 元の処理と結果が違いました（最初の1回）: コライダー {c.name}・種類 {_kind[k]} / 粒の位置 {V(before)} → 元 {V(byOriginal)}・前計算 {V(particlePosition)}（差 {d:G6}）");
                    }
                }
                return;
            }
            CollideCached(k, ref particlePosition, particleRadius);
        }

        private static void CollideCached(int k, ref Vector3 particlePosition, float particleRadius)
        {
            switch (_kind[k])
            {
                case KindOutsideSphere: _outsideSphere(ref particlePosition, particleRadius, _p0[k], _radius[k]); break;
                case KindInsideSphere: _insideSphere(ref particlePosition, particleRadius, _p0[k], _radius[k]); break;
                case KindOutsideCapsule: _outsideCapsule(ref particlePosition, particleRadius, _p0[k], _p1[k], _radius[k]); break;
                default: _insideCapsule(ref particlePosition, particleRadius, _p0[k], _p1[k], _radius[k]); break;
            }
        }

        private static string V(Vector3 v) => $"({v.x:R}, {v.y:R}, {v.z:R})";

        // ===================== 集計 =====================

        private static void MaybeLogSummary()
        {
            float now = Time.realtimeSinceStartup;
            if (_windowStart < 0f) { _windowStart = now; return; }
            float secs = now - _windowStart;
            if (secs < SummarySeconds) return;
            if (_pairs > 0 || _fallbacks > 0)
            {
                PatcherPlugin.Logger.LogInfo(
                    $"{Tag} 直近 {secs:F0}秒: 前計算 {_builds}回・前計算の値で当たり判定 {_pairs}組（{_pairs / Math.Max(1f, secs):F0}組/秒）・" +
                    $"元の処理で当たり判定 {_fallbacks}組・まとめたループ {_loops}回 / 前計算={(_fast ? "有効" : "無効")}・ループ全体をまとめる={(_merge ? "有効" : "無効")}" +
                    (_verified > 0 ? $" / 元の処理との比較 {_verified}回・違った {_mismatches}回（最大の差 {_maxDiff:G6}。ループをまとめているときは粒ごと、まとめていないときは1組ごとに数える）" : ""));
            }
            _windowStart = now;
            _pairs = _fallbacks = 0;
            _loops = 0;
            _verified = _mismatches = 0;
            _maxDiff = 0f;
            _builds = 0;
        }
    }
}
