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
    /// ksPlug が、顔（と体）のマテリアル（<c>ChaControl.customMatFace</c> / <c>customMatBody</c>）を null か確かめずに使い、
    /// マテリアルが無いキャラで NullReferenceException を出す問題の回避策（0.5.0）。
    /// KK の kPlugPatcher_InviteChrID の EnableAIMainFaceColorGuard（v1.8.1）・EnableGuestFaceColorGuard（v1.10.3）に当たる。
    ///
    /// 【問題】
    ///   実機ログで、招待キャラの表情の反応
    ///   （<c>CtrlFace.LateUpdate</c> → <c>ReactToEvent</c> / <c>Speak</c> → <c>CtrlFace.SetExpression</c> → <c>FaceExpr.SetExpression</c>）の
    ///   頬の色を戻す <c>cha.customMatFace.SetColor("_overcolor2", …)</c> で 142件。例外で表情の処理の後半が飛ぶ。
    ///   KK では、特定の頭の MOD のカード（MatAB=0）で顔のマテリアルが null だった。
    ///   同じく確かめずに使う所（ksPlugVR も同じ、CtrlNpc は VR に無い）:
    ///   - <c>Character.FaceExpr.SetExpression(ChaControl, Expression)</c>: 頬の色（SetColor）2か所 ← 実機で再現
    ///   - <c>Character.AIMain.FaceTweaks(bool)</c>: アヘ顔の頬の色（SetColor）
    ///   - <c>CtrlNpc.LateUpdate()</c>: 頬の色（SetColor）
    ///   - <c>CtrlBodySiru.IE_Init</c>（コルーチン）・<c>OnDestroy</c>: 汁のマスク（GetTexture / SetTexture。顔と体）
    ///
    /// 【回避策】上の関数の中の <c>Material.SetColor(string, Color)</c> / <c>SetTexture(string, Texture)</c> / <c>GetTexture(string)</c> の呼び出しを、
    ///   マテリアルが null（破棄済みを含む）なら何もしない（GetTexture は null を返す）同じ関数に置き換える（Transpiler）。
    ///   マテリアルがあるときは原型と同じ。例外で止まっていた残りの処理は、そのまま実行される。
    ///   どのキャラで起きたかを知るため、対象の関数の間だけキャラを覚え、飛ばした件数を 1・10・100・1000…回目にログに出す。
    /// </summary>
    internal static class MaterialNullGuard
    {
        private const string Tag = "[MaterialNullGuard]";

        private static readonly List<string> _sites = new List<string>();      // 置き換えた所（関数名）。番号で引く
        private static readonly Dictionary<string, int> _skipCounts = new Dictionary<string, int>();
        [ThreadStatic] private static ChaControl _currentCha;

        private static readonly MethodInfo _setColor = AccessTools.Method(typeof(Material), nameof(Material.SetColor), new[] { typeof(string), typeof(Color) });
        private static readonly MethodInfo _setTexture = AccessTools.Method(typeof(Material), nameof(Material.SetTexture), new[] { typeof(string), typeof(Texture) });
        private static readonly MethodInfo _getTexture = AccessTools.Method(typeof(Material), nameof(Material.GetTexture), new[] { typeof(string) });

        internal static void TryApply(Harmony harmony, Assembly asm, string root)
        {
            try
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                Type Find(string name) => types.FirstOrDefault(t => t.Name == name && t.Namespace != null && t.Namespace.StartsWith(root));

                var targets = new List<KeyValuePair<string, MethodInfo>>();
                void Add(string label, MethodInfo m) { if (m != null) targets.Add(new KeyValuePair<string, MethodInfo>(label, m)); }

                Type faceExpr = Find("FaceExpr");
                Add("FaceExpr.SetExpression", faceExpr != null ? AccessTools.Method(faceExpr, "SetExpression", new[] { typeof(ChaControl), faceExpr.GetNestedType("Expression", BindingFlags.Public | BindingFlags.NonPublic) }) : null);
                Type aiMain = Find("AIMain");
                Add("AIMain.FaceTweaks", aiMain != null ? AccessTools.Method(aiMain, "FaceTweaks", new[] { typeof(bool) }) : null);
                Type ctrlNpc = Find("CtrlNpc");
                Add("CtrlNpc.LateUpdate", ctrlNpc != null ? AccessTools.Method(ctrlNpc, "LateUpdate", Type.EmptyTypes) : null);
                Type siru = Find("CtrlBodySiru");
                Add("CtrlBodySiru.OnDestroy", siru != null ? AccessTools.Method(siru, "OnDestroy", Type.EmptyTypes) : null);
                MethodInfo siruInit = siru != null ? AccessTools.Method(siru, "IE_Init") : null;
                Add("CtrlBodySiru.IE_Init", siruInit != null ? AccessTools.EnumeratorMoveNext(siruInit) : null);

                var done = new List<string>();
                var transpiler = new HarmonyMethod(AccessTools.Method(typeof(MaterialNullGuard), nameof(Transpiler)));
                var prefix = new HarmonyMethod(AccessTools.Method(typeof(MaterialNullGuard), nameof(ContextPrefix)));
                var finalizer = new HarmonyMethod(AccessTools.Method(typeof(MaterialNullGuard), nameof(ContextFinalizer)));
                foreach (KeyValuePair<string, MethodInfo> t in targets)
                {
                    try
                    {
                        _currentReplaced = 0;
                        harmony.Patch(t.Value, prefix: prefix, transpiler: transpiler, finalizer: finalizer);
                        done.Add($"{t.Key} {_currentReplaced}か所");
                    }
                    catch (Exception ex)
                    {
                        PatcherPlugin.Logger.LogWarning($"{Tag} {t.Key} への適用に失敗しました: {ex.Message}");
                    }
                }
                PatcherPlugin.Logger.LogInfo($"{Tag} {root} のマテリアルを確かめずに使う所を、マテリアルが無ければ飛ばす処理に置き換えました（{string.Join("、", done.ToArray())}）。");
            }
            catch (Exception ex)
            {
                PatcherPlugin.Logger.LogError($"{Tag} 適用に失敗しました: {ex.Message}");
            }
        }

        // ===================== Transpiler =====================

        private static int _currentReplaced;

        /// <summary>Material の SetColor / SetTexture / GetTexture の呼び出しを、置き換えた所の番号を付けて安全な関数に差し替える。</summary>
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            // 名前は元の関数から作る（他のプラグインが後から同じ関数に当てると、Harmony が Transpiler を走らせ直すため）
            Type owner = __originalMethod.DeclaringType;
            string label = owner != null && owner.Name.StartsWith("<") && owner.DeclaringType != null
                ? owner.DeclaringType.Name + "." + owner.Name.Substring(1, Math.Max(0, owner.Name.IndexOf('>') - 1))
                : (owner != null ? owner.Name + "." : "") + __originalMethod.Name;
            int site = _sites.Count;
            _sites.Add(label);
            foreach (CodeInstruction code in instructions)
            {
                if ((code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call) && code.operand is MethodInfo mi)
                {
                    string safe = mi == _setColor ? nameof(SafeSetColor) : mi == _setTexture ? nameof(SafeSetTexture) : mi == _getTexture ? nameof(SafeGetTexture) : null;
                    if (safe != null)
                    {
                        // スタック [material, …引数] に番号を積んで、static の安全な関数を呼ぶ
                        var push = new CodeInstruction(OpCodes.Ldc_I4, site);
                        code.MoveLabelsTo(push);
                        code.MoveBlocksTo(push);
                        yield return push;
                        yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MaterialNullGuard), safe));
                        _currentReplaced++;
                        continue;
                    }
                }
                yield return code;
            }
        }

        public static void SafeSetColor(Material material, string name, Color color, int site)
        {
            if (material == null) { Skipped(site, $"SetColor({name})"); return; }
            material.SetColor(name, color);
        }

        public static void SafeSetTexture(Material material, string name, Texture texture, int site)
        {
            if (material == null) { Skipped(site, $"SetTexture({name})"); return; }
            material.SetTexture(name, texture);
        }

        public static Texture SafeGetTexture(Material material, string name, int site)
        {
            if (material == null) { Skipped(site, $"GetTexture({name})"); return null; }
            return material.GetTexture(name);
        }

        private static void Skipped(int site, string what)
        {
            try
            {
                string label = site >= 0 && site < _sites.Count ? _sites[site] : "?";
                string chaName = _currentCha != null ? _currentCha.fileParam?.fullname ?? _currentCha.name : "不明";
                string key = label + "|" + chaName;
                _skipCounts.TryGetValue(key, out int n);
                _skipCounts[key] = ++n;
                if (n == 1 || n == 10 || n == 100 || n == 1000 || n % 10000 == 0)
                    PatcherPlugin.Logger.LogInfo($"{Tag} {label}: {chaName} のマテリアルが無いため {what} を飛ばしました（このキャラ・この所で {n}回目）。");
            }
            catch { }
        }

        // ===================== どのキャラかを覚える =====================

        private static void ContextPrefix(object __instance, object[] __args, out ChaControl __state)
        {
            __state = _currentCha;
            try { _currentCha = FindCha(__instance, __args); } catch { _currentCha = null; }
        }

        private static Exception ContextFinalizer(Exception __exception, ChaControl __state)
        {
            _currentCha = __state;
            return __exception;
        }

        /// <summary>型ごとの「キャラの取り出し方」（cha / Cha のフィールド・プロパティ、コルーチンなら <>4__this の先）。毎フレーム呼ばれる所があるのでキャッシュする。</summary>
        private static readonly Dictionary<Type, Func<object, ChaControl>> _chaGetters = new Dictionary<Type, Func<object, ChaControl>>();

        private static ChaControl FindCha(object instance, object[] args)
        {
            if (args != null)
                foreach (object a in args)
                    if (a is ChaControl c) return c;
            if (instance == null) return null;
            Type type = instance.GetType();
            if (!_chaGetters.TryGetValue(type, out Func<object, ChaControl> getter))
                _chaGetters[type] = getter = MakeGetter(type, 0);
            return getter != null ? getter(instance) : null;
        }

        private static Func<object, ChaControl> MakeGetter(Type type, int depth)
        {
            foreach (string name in new[] { "cha", "Cha" })
            {
                FieldInfo f = AccessTools.Field(type, name);
                if (f != null && typeof(ChaControl).IsAssignableFrom(f.FieldType)) return o => f.GetValue(o) as ChaControl;
                PropertyInfo p = AccessTools.Property(type, name);
                if (p != null && typeof(ChaControl).IsAssignableFrom(p.PropertyType) && p.GetGetMethod(true) != null) return o => p.GetValue(o, null) as ChaControl;
            }
            FieldInfo owner = depth == 0 ? AccessTools.Field(type, "<>4__this") : null; // コルーチンなら持ち主へ
            Func<object, ChaControl> inner = owner != null ? MakeGetter(owner.FieldType, 1) : null;
            if (inner != null) return o => { object x = owner.GetValue(o); return x != null ? inner(x) : null; };
            return null;
        }
    }
}
