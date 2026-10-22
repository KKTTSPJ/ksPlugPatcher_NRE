# ksPlug issues found while making ksPlugPatcher_NRE

This document is for the author of ksPlug / ksPlugVR (Katarsys).
It lists the problems that ksPlugPatcher_NRE works around, with the exception stacks, the cause, and a suggested fix inside ksPlug.
The goal is to make it easy to fix them in ksPlug itself, so that this patch is no longer needed.

- No ksPlug code is included here or in the patch. Types, methods and fields are referred to by name only. The suggested fixes are short sketches written for this document.
- Names are given for ksPlug (`ksPlug.*`). ksPlugVR (`ksPlugVR.*`) has the same code unless noted.
- Stack traces and counts come from real play logs with ksPlug 1.7.0 (normal mode). IL offsets are for that build.
- The patched members are unchanged in ksPlug / ksPlugVR 1.6.7, 1.6.8, 1.7.0, 1.7.1, 1.7.2 and 1.7.3, so every issue below is expected to still exist in 1.7.3.
  The patch itself was confirmed in game with ksPlug 1.7.0 (normal mode) and ksPlugVR 1.7.3 (patches applied, invite list).
- Game: Koikatsu Sunshine with BepInEx 5. The test setup has about 375 character cards and many mods.

## Summary

| # | Where | What the player sees | Type | Patch file / config key |
|---|---|---|---|---|
| 1 | `Character.ChaTools+ColDefsManager.LateUpdate` | NRE every frame in H | ksPlug, stale reference | `KsPlugStaleRefFix.cs` / `EnableColDefsManagerStaleRefFix` |
| 2 | `Character.BellyCtrl.LateUpdate` | NRE every frame, belly inflation stops | ksPlug, stale reference | `KsPlugStaleRefFix.cs` / `EnableBellyCtrlStaleRefFix` |
| 3 | `Character.CtrlBones.LateUpdate`, `Character.AI.IsCloseFromMBoy` | NRE every frame, bone offsets (e.g. `OffsetOversized`) stop | ksPlug, stale reference | `CtrlBonesStaleRefFix.cs` / `EnableCtrlBonesStaleRefFix` |
| 4 | `Character.FaceExpr.SetExpression` and 3 more | NRE on facial expressions for some characters | ksPlug, missing null check | `MaterialNullGuard.cs` / `EnableMaterialNullGuard` |
| 5 | Character swap (`Character.AIGuest`) | After a swap, faces do not match the lines | ksPlug, behavior | `FaceListReloadOnSwap.cs` / `EnableFaceListReloadOnSwap` |
| 6 | Invite menu (`Character.ChaList`, `Menu.MenuInvite`) | Several GB of memory per free H | ksPlug, memory | `LazyInviteCardList.cs` / `EnableLazyInviteCardList` |
| 7 | Many invited characters | Frame rate drops fast (7 girls: 137 ms per frame) | game, made worse by many characters | `DynamicBonePerf.cs` / `DisableGuestDynamicBones`, `LimitDynamicBoneIterations` |
| 8 | Game `ParentObjectCtrl` | Orgasm repeats while the gauge is empty ("orgasm loop") | game, triggered by body reloads in H | `ParentObjectCtrlStaleRefFix.cs` / `EnableParentObjectCtrlStaleRefFix` |
| 9 | Smaller NREs seen only a few times (`Binder.StoreUserPref`, `Events.SearchMissingSave`, ksPlugVR `Dealer.Update`, ksPlugVR `GuestAction.QueryInfoFromMain`) | One exception each time | ksPlug, missing null check | not patched |
| 10 | `Dealer.Ctx` after "Return to title" from the config screen during H (normal mode) | Invited girls stay in the story and in the next H, NRE every frame on the title screen, the next H cannot be ended | ksPlug, behavior | `HContextResetOnTitle.cs` / `EnableHContextResetOnTitle`, `EnableEndHGuard` |
| 11 | Game `HSprite.OnSpeedUpClick` in service (houshi) positions | All H buttons (finish, end, position menu) stay disabled in the loop / before the finish | game, made more likely by slow frames and long voice lines | `HoushiVoiceWaitFix.cs` / `EnableSpeedUpVoiceWaitFix`, `EnableVoiceWaitWatchdog` |
| 12 | Game VRTK (`VRTK_UIGraphicRaycaster`), ksPlugVR menus | VR title screen: a ksPlugVR menu cannot be clicked where it overlaps the title UI; the ray goes through it | game, affects ksPlugVR | `VRMenuRaycastPriority.cs` / `FixRayForKsPlugMenus` |
| 13 | Game (KKS) `Manager.Character.enableCharaLoadGCClear` | In H, old outfit textures stay in memory after each outfit change until H ends (VRAM can overflow) | game, made worse by many characters; IllusionFixes is involved; fixing it is optional | `HOutfitCleanup.cs` / `EnableHOutfitCleanup` (optional) |

## Common cause of #1–#3 (and #8)

Several components look up Transforms or GameObjects of the character's body once, at initialization, and keep them for the whole H scene.
When the body is rebuilt (`ChaControl.Reload` / `ChangeCoordinateTypeAndReload`), Unity destroys those objects and creates new ones with the same names.
The cached C# references are not null, but they point to destroyed objects, so the next `SetActive`, `localPosition` or `position` call throws `NullReferenceException`.

In the logs the first exceptions come right after a coordinate reload (`CoordinateChanged` in the log) at the start of H, and after a character swap.
ksPlug's own swap `Character.ChaCore.SwapCharacter` also calls `Reload` and `ChangeCoordinateTypeAndReload` on both characters, so swaps rebuild bodies too.

Useful Unity facts for the fix:
- `obj == null` (Unity's overloaded operator) is `true` for destroyed objects. `obj is null` / `ReferenceEquals(obj, null)` is not.
- `UnityEngine.Object.Equals` treats all destroyed objects as equal, so do not use destroyed objects as dictionary keys without a reference comparer.

A general fix is either to check the cached object before use and find it again by name, or to refresh the cache when the character reloads.

## 1. `ChaTools+ColDefsManager.LateUpdate`

Stack (1,804 times in one H session, 2,929 in another):

```
NullReferenceException
  at (wrapper managed-to-native) UnityEngine.GameObject.SetActive(UnityEngine.GameObject,bool)
  at ksPlug.Character.ChaTools+ColDefsManager.LateUpdate () [0x00020]
```

**Cause.** `gos` only grows (`GetOrCreate`); nothing removes entries. After a body reload, some entries are destroyed GameObjects.
`LateUpdate` calls `SetActive(relatedCha.visibleAll)` on every entry, throws on the first destroyed one, and the entries after it are not updated either.
`relatedCha` is also used without a check.

**Suggested fix.**

```csharp
private void LateUpdate()
{
    if (relatedCha == null) return;
    gos.RemoveAll(go => go == null);          // Unity's == null also matches destroyed objects
    foreach (GameObject go in gos)
        go.SetActive(relatedCha.visibleAll);
}
```

**Patch.** A prefix removes destroyed entries from `gos` and skips the method when `relatedCha` is gone.

## 2. `BellyCtrl.LateUpdate`

Stack (same counts as #1, every frame):

```
NullReferenceException
  at (wrapper managed-to-native) UnityEngine.Transform.set_localPosition_Injected(UnityEngine.Transform,UnityEngine.Vector3&)
  at UnityEngine.Transform.set_localPosition (UnityEngine.Vector3 value)
  at ksPlug.Character.BellyCtrl.LerpInflation (BellyCtrl+BoneTweak b, Vector3 targPos, Vector3 targRot, Vector3 targScl, Single lerp) [0x0002c]
  at ksPlug.Character.BellyCtrl.LateUpdate () [0x00160]
```

**Cause.** `IE_Init` finds the bones `cf_s_waist02`, `cf_s_waist01`, `cf_s_spine01` and `cf_s_spine02` once and stores them in `BoneTweak.t` (`waist2`, `waist1`, `spine1`, `spine2`).
They are never looked up again, so after a body reload `LerpInflation` writes to destroyed Transforms.
Everything after that point in `LateUpdate` stops too (inflation growth, pregnancy check, settings changes).
Also, `GetBone` stores `null` when a bone is missing at init time, and `LateUpdate` does not check that either.

**Suggested fix.** At the start of `LateUpdate` (after the `isInit` check), check the four Transforms. If one is `null` or destroyed, find the bone by name in the character again and replace only `BoneTweak.t`.
Keep the original `locPos` / `locRot` / `locScl` from init. If the bone cannot be found yet (the body is still loading), skip this frame and try again later.

```csharp
foreach (BoneTweak b in new[] { waist2, waist1, spine1, spine2 })
{
    if (b != null && b.t != null) continue;
    // find the bone by name again (e.g. the same search as IE_Init uses);
    // if it is not there yet, return and retry in a later frame
}
```

**Patch.** A prefix does the above (retry every 30 frames while the bones are missing). A `null` `BoneTweak` is created with the same values `GetBone` would use.

## 3. `CtrlBones.LateUpdate` and `AI.IsCloseFromMBoy`

Stacks:

```
NullReferenceException                                   (6,919 times in one H session)
  at UnityEngine.Transform.get_localPosition ()
  at ksPlug.Character.CtrlBones.ApplyLerpOffset (CtrlBones+Offsets offsets, Single lerp) [0x00000]
  at ksPlug.Character.CtrlBones.LateUpdate () [0x000ea]

NullReferenceException                                   (538 times in one H session)
  at UnityEngine.Transform.get_position ()
  at ksPlug.Character.AI.get_IsCloseFromMBoy () [0x0003f]
  at ksPlug.Character.AIS.DoChoice (AI ai, AIS+Choice choice, Single chanceTrigger) [0x00315]
  at ksPlug.Character.AI.AutoThink () [0x0060c]
  at ksPlug.Character.AIGuest.LateUpdate () [0x00152]
```

**Cause.** `IE_Init` calls `CtrlBones.GetBones(cha, bc)` once. It stores the spine, arm and leg Transforms (`cf_j_root`, `cf_j_hips`, `cf_j_spine01`, …) in `Offsets.trs` of `Spine`, `Arm_L`, `Arm_R`, `Leg_L` and `Leg_R`.
The per-position offsets copy those `Offsets` into `touchOffsets`, `customOffsets`, `extraSizeOffsets` (e.g. `ExtraSizeOffset` from `SizeOffsets.SearchAndSubscribe` with `OffsetOversized`) and `motionIkSubstOffset`.
`LateUpdate` applies them every frame without a null check. After a body reload all of them point to destroyed Transforms.
`IsCloseFromMBoy` compares `MainBoy.BoneCtrl.Spine.spine1.trs.position` with its own `Spine.spine1.trs.position`, so it fails the same way.

**Suggested fix.**
- In `LateUpdate`, if `Spine.root/hips/spine1.trs` is `null` or destroyed, call `GetBones(cha, this)` again.
- Then re-point the entries in the offset lists to the new Transforms (same part, same field). Drop entries whose Transform cannot be found.
- If the bones are not there yet (body still loading), skip the frame.
- In `IsCloseFromMBoy`, return `false` when either Transform is missing.

**Patch.** A prefix on `LateUpdate` re-runs ksPlug's own `GetBones` and re-maps the offset lists by part and field name (destroyed Transforms cannot be read, even by name).
Entries that cannot be re-mapped are removed. A prefix on `IsCloseFromMBoy` repairs every registered `CtrlBones` first; a finalizer returns `false` if an NRE still happens.

## 4. Face material is used without a null check (`FaceExpr.SetExpression` and others)

Stack (142 times in one session; the player did not use ahegao):

```
NullReferenceException
  at ksPlug.Character.FaceExpr.SetExpression (ChaControl cha, FaceExpr+Expression expr) [0x000e5]
  at ksPlug.Character.CtrlFace.SetExpression (Voices+Type typ, Boolean inAhegao, Boolean forceTongueOut, Boolean isHiEx) [0x0015d]
  at ksPlug.Character.CtrlFace.Speak (Voices+Type typ, Boolean applyExpression) [0x00057]
  at ksPlug.Character.CtrlFace.ReactToEvent (Boolean forceChange) [0x000d4]
  at ksPlug.Character.CtrlFace.LateUpdate () [0x001ba]
```

**Cause.** At 0xe5, `cha.customMatFace.SetColor("_overcolor2", …)` (cheek color) runs while `customMatFace` is `null`.
This happens with characters whose `customMatFace` is `null`. Which card caused it in this session was not identified.
With kPlug on Koikatsu the same exception came from a head mod whose face material was not set up the way the game expects.
The rest of the expression code is skipped because of the exception.

The same unchecked `customMatFace` / body material `SetColor` / `SetTexture` / `GetTexture` calls are also in:
- `AIMain.FaceTweaks(bool)` (ahegao)
- `CtrlNpc.LateUpdate()` (not in ksPlugVR)
- `CtrlBodySiru.OnDestroy()` and the `CtrlBodySiru.IE_Init` coroutine

**Suggested fix.** Check the material before use: `if (cha.customMatFace != null) cha.customMatFace.SetColor(…);`. The same for the other places. Skip only that call, not the rest of the method.

**Patch.** A transpiler replaces `Material.SetColor(string, Color)`, `SetTexture(string, Texture)` and `GetTexture(string)` in these five methods with versions that do nothing (or return `null`) when the material is `null`.
It logs the character and the property when a call is skipped.

## 5. Character swap reloads the voice list but not the face list

**Symptom.** After a swap, the new character speaks the lines of her personality, but her facial expressions follow the personality of the girl at the start of H.
Face numbers mean different things per personality, so the faces do not match the lines.

**Cause.** At the start of H the game (`HSceneProc`) loads both lists per personality: `face.LoadText(name)` (and `face1` for the second girl), then `voice.Init(name1, name2)`.
ksPlug's swap in `AIGuest` only calls `hCtrl.HProc.voice.Init("c" + personality, …)` (three places), so the face lists (`FaceListCtrl`, `HVoiceCtrl.faceLists[0/1]`) stay on the old personality.
A timing log during swaps showed 0 face list loads for ksPlug's `Init` calls.

**Suggested fix.** Reload the face list of the same slot together with the voice list, e.g.

```csharp
yield return hCtrl.HProc.face.LoadText("c" + personalityId);      // slot 0
// for the second girl: hCtrl.HProc.face1.LoadText(...)
StartCoroutine(hCtrl.HProc.voice.Init(...));
```

`FaceListCtrl.LoadText(string)` is a coroutine (use it, not `LoadTextD`, which skips the fixed-character personality replacement).

**Patch.** After the `HVoiceCtrl.Init` coroutine finishes, it compares the personality of each slot with the face list loaded last, and reloads the face list if it differs.
If the new personality has no face list, it keeps the old one. This costs about 0.5 s per swap.

## 6. Invite menu loads every card fully and keeps it

**Symptom.** Each time a free H starts, Mono memory grows by several GB and stays until H ends. Sideloader also prints about 1,550 "missing mod" warnings at the start of H.

| Log | Before H | 1 minute into H |
|---|---|---|
| free H, 332 cards (6.9 GB on disk) | Mono 619 MB | Mono 8,196 MB (+7.6 GB) |
| free H, same cards | 615 MB | 7,471 MB (+6.9 GB) |

Mono does not give heap memory back to the OS, so the peak stays for the rest of the session.

**Cause.** Outside the story, `MenuInvite` builds the lists with `ChaList.GetGirlCardList()` / `GetMaleCardList()` (in ksPlugVR 1.7.2+, `MenuInvite.InitFileList()` at character select).
For every card, `ChaList.AddChaFileToList` calls `new ChaFileControl().LoadCharaFile(path, sex, false, true)`, which loads the whole card including all extended data, and keeps it in `FileInfo.File` until the menu is destroyed.
The list itself only needs the name / parameter and the face texture (`FileInfo.png`). The full card is only needed when the character is invited.

**Suggested fix.**
- Keep a light `ChaFileControl` in the list: only the parameter (`CopyParameter`), `facePngData` and the file name.
  Do not use `CopyAll` for this, because KKAPI hooks it and copies the extended data as well. Drop `pngData` after the face texture is made.
- In `MenuInvite.InviteSomeone`, load the full card from `FileInfo.cardPath` just before creating the character.
- Optional: read only the PNG header, the face image and the `Parameter` block of the card file instead of `LoadCharaFile`.
  This avoids extended-data loading and the Sideloader warnings for cards that are not invited.
- Make each card light right after it is read, not after the whole list is built. Otherwise all full cards are alive at the same time and the peak does not go down.

**Patch.** It replaces `AddChaFileToList` with the above (partial read of 【KoiKatuCharaSun】 cards; cards it cannot read are fully loaded and then made light).
A prefix on `InviteSomeone` reloads the full card from `cardPath`. Result with 375 cards: the list takes about 2.4 s and Mono grows by +77 to +117 MB instead of +7 GB.
Invited cards are written back by `CleanCreatedFileInfo` as before.

**Face textures.** The face textures of the list (`FileInfo.png`, made with `PngAssist.ChangeTextureFromByte`) keep the full card size, with mipmaps and a readable CPU copy, although they are only shown small (471 faces: about 486 MB on the GPU plus 486 MB on the CPU, kept even after returning to the title). Making them at the display size and calling `Apply(true, true)` (or `Apply(false, true)` when they are already small) frees the CPU copy; the patch does this (`ShrinkInviteMenuFaces`, on by default; GPU 486 MB → 212 MB and the 486 MB CPU copy freed in ksPlugVR 1.7.3, no visible difference).

## 7. Many invited characters: DynamicBone cost

**Symptom.** The frame time grows quickly with the number of invited girls.

| Girls shown (incl. main) | No workaround | Max 1 DynamicBone update per frame | + invited girls' DynamicBones stopped |
|---|---|---|---|
| 3 | 31–48 ms | 24–27 ms | 19 ms |
| 5 | 58 ms | 36 ms | 20 ms |
| 7 | 137 ms | 42 ms | 25 ms |
| 12 | — | 112 ms | 41 ms |

**Cause.** This is game code, not ksPlug: each invited girl adds about 15–100 `DynamicBone` components (hair, clothes, accessories).
`DynamicBone.UpdateDynamicBones` runs up to 3 iterations per frame when the frame rate is below its 60 Hz update rate, so slow frames cause more work (a feedback loop).
With 7 girls, `DynamicBone.LateUpdate` alone took 92 ms per frame. ksPlug adds the characters but has no option to reduce this.

**Suggestion.** An option in ksPlug to skip the `DynamicBone` / `DynamicBone_Ver01` update of invited characters (`AIGuest`).
Another option is to cap the iterations per frame (clamp `m_Time` before `UpdateDynamicBones`). With both, `DynamicBone.LateUpdate` stays at about 2 ms per frame regardless of the number of girls.

**Patch.** `DisableGuestDynamicBones` (skip `LateUpdate` of DynamicBones under a `ChaControl` that has `AIGuest`) and `LimitDynamicBoneIterations` / `MaxIterationsPerFrame` (clamp `m_Time`).
Both are on by default in the release. Stopped DynamicBones stay in their rest pose.

## 8. Orgasm loop: game `ParentObjectCtrl` after body reloads

Stack (23 times in one session: 18 from the game's `HSonyu.Proc`, 4 from `HSonyu.LoopProc`, 1 from ksPlug's swap shown here):

```
NullReferenceException
  at (wrapper managed-to-native) UnityEngine.GameObject.SetActive(UnityEngine.GameObject,bool)
  at ParentObjectCtrl.VisibleObi (System.Boolean _visible) [0x0001d]
  at ParentObjectCtrl.ProcObi (System.String _nameNextAnimation) [0x00049]
  at (wrapper dynamic-method) HActionBase.DMD<HActionBase::SetPlay>(HActionBase,string,bool)
  at ksPlug.HSession.HManage.PlayStateOnMains (System.String stateToPlay) [0x00043]
  at ksPlug.Character.AIGuest+<IE_PullForSwap>d__99.MoveNext () [0x001f8]
```

The other 22 have `HSceneProc.Update` → `HSonyu.Proc` (or `HSonyu.LoopProc`) → `HActionBase.SetPlay` → `ParentObjectCtrl.ProcObi` → `VisibleObi`.

**Cause.** This is game code. `ParentObjectCtrl` keeps the H items in `dicObject` and calls `SetActive` on them in `Visible`, `VisibleObi`, `Proc` and `ProcObi` without checking whether they were destroyed.
(`VisibleObi` / `ProcObi` are new in Koikatsu Sunshine.) After a body reload the items are destroyed.
In `HSonyu.Proc`, the exception happens in `SetPlay("WS_IN_A")` before `flags.gaugeFemale = 0f`. The gauge stays full and the orgasm repeats.

**Suggestion.** This is game code. ksPlugPatcher_NRE already fixes it with a Harmony patch on the game (below), so no change in ksPlug is needed. If you want ksPlug to handle it as well (ksPlug triggers the reloads in H, e.g. `ChaCore.SwapCharacter`), feel free to use the code of the patch (`ParentObjectCtrlStaleRefFix.cs`, MIT).
We have not checked whether rebuilding `ParentObjectCtrl` after the reload would be a better fix.

**Patch.** Prefixes replace the four methods with the same logic that skips destroyed items and missing keys.

## 9. Smaller NREs (not patched)

These were seen only a few times in all our logs, so the patch does not touch them. The location of each exception was checked against the IL of the build in the log.
The methods are the same in 1.7.0 and 1.7.3.

### 9.1 `HSession.Binders+Binder.StoreUserPref` when leaving H

Once, in ksPlug 1.7.3 (normal mode), while the H scene was being unloaded:

```
NullReferenceException
  at UnityEngine.Transform.get_localPosition ()
  at ksPlug.HSession.Binders+Binder.StoreUserPref () [0x00081]
  at ksPlug.HSession.Binders+Binder.OnDestroy () [0x00000]
```

**Cause.** `Binder.Create` makes `Root` a child of the main girl's `cf_n_height` and `Hook` a child of `Root`.
`OnDestroy` calls `StoreUserPref`, which reads `Hook.localPosition` / `localRotation`. When the whole hierarchy is destroyed (scene unload), `Hook` can already be destroyed when `Binder.OnDestroy` runs.
The position of that binder is then not stored, and this call does not reach `WaitAndSaveBinderUserPrefs`.

**Suggested fix.** In `StoreUserPref`, skip the position part when `Hook == null` (Unity null). Or store the preference when the user changes it, instead of in `OnDestroy`.

### 9.2 `Story.Events.SearchMissingSave` before `ActionScene` exists

Once, in ksPlug 1.7.0 (normal mode), at the first event of a new game (harbor, while a character was being loaded):

```
NullReferenceException: Object reference not set to an instance of an object
  at ksPlug.Story.Events.SearchMissingSave () [0x0004a]
  at ksPlug.Character.ChaCore+<IE_Init>d__77.MoveNext () [0x00818]
```

**Cause.** At 0x4a, `SingletonInitializer<ActionScene>.instance` is `null` and `.Cycle` is read from it. The exception ends `ChaCore.IE_Init`, so the rest of the init (`GrabCtrl`, `CtrlIK`, `CtrlBones`) is skipped for that character.

**Suggested fix.** Return early when `SingletonInitializer<ActionScene>.instance == null`. Also check `Player` the same way before reading `chaser`.

### 9.3 ksPlugVR `Dealer.Update` when the VR free H select scene starts

About 6 times per VR session in ksPlugVR 1.7.0 (IL 0x8c) and 1.7.3 (IL 0xda), right after the scene changes to `VRFreeHSelect`:

```
NullReferenceException: Object reference not set to an instance of an object
  at ksPlugVR.Dealer.Update () [0x000da]
```

**Cause.** `Dealer.Update` gets `VRFreeHSelect.member` by reflection and checks it for `null`. Then it reads `member.resultTimeZone.Value` (the offset in the stack is the start of this statement) and `member.resultStage1.Value`.
`member` is created by the field initializer of `VRFreeHSelect`, but its `IntReactiveProperty` fields are set later by the game. We did not find exactly where; because the exception only happens right after the scene starts, they are most likely still `null` at that moment.

**Suggested fix.** Also check `member.resultTimeZone != null` and `member.resultStage1 != null` before reading `.Value`.

### 9.4 ksPlugVR `GuestAction.QueryInfoFromMain` when leaving H

Once, in ksPlugVR 1.7.0, while leaving H (scene change from `HotelOutside` to `VRFreeHSelect`):

```
NullReferenceException
  at (wrapper managed-to-native) UnityEngine.Component.get_transform(UnityEngine.Component)
  at ksPlugVR.HSession.GuestAction.QueryInfoFromMain () [0x00027]
  at ksPlugVR.HSession.GuestActions.Piston.LateUpdate () [0x0001f]
```

**Cause.** At 0x27, `hFlag.lstHeroine[0].chaCtrl.transform` is read. While the H scene is being torn down, the main heroine's `ChaControl` is already destroyed, but an invited girl's `Piston` action still runs `LateUpdate`.
ksPlug (normal mode) has the same `QueryInfoFromMain`, but it was not seen there.

**Suggested fix.** In `QueryInfoFromMain` (or at the start of the guest actions' `LateUpdate`), return when `lstHeroine[0].chaCtrl == null` or `Ai.Cha == null` (Unity null). Or stop the guest actions before the H scene is unloaded.

## 10. H state is not reset by "Return to title" (normal mode)

Seen in ksPlug 1.7.3 (normal mode): a free H with 11 invited girls, then the config screen's "Return to title" (the game's `Utils.Scene.ReturnTitle`).
After that, the invited girls were still there in the next story scene (an empty room) and in the next free H. On the title screen and in the story, these ran every frame:

```
NullReferenceException: Object reference not set to an instance of an object
  at ksPlug.Character.BellyCtrl.get_IsMain () [0x00045]
  at ksPlug.Character.BellyCtrl.Update () [0x0003f]
```

(9,108 times, 11 per frame = one per invited girl), plus `HSession.GroundPlane.LateUpdate` [0x23], `AIMain.MainAiInDecision` [0x4f] ← `HAction.MainActionControl` [0xb3] ← `HCtrl.Update`,
and in the story `HCtrl.LateUpdate` [0x6b0] (`Core.dealer.StoryHInf` is null). In the next free H, ending the H failed every frame:

```
NullReferenceException: Object reference not set to an instance of an object
  at ksPlug.HSession.HManage.EndH () [0x00037]
  at ksPlug.Core.HSceneProc_EndProc () [0x0001f]
  at (wrapper dynamic-method) HSceneProc.DMD<HSceneProc::EndProc>(HSceneProc)
  at (wrapper dynamic-method) HSceneProc.DMD<HSceneProc::Update>(HSceneProc)
```

(940 times from this prefix and 940 times from `Dealer.Update` [0x56b]). Because the prefix throws, the game's `EndProc` never finishes, so the player cannot leave that H either.

**Cause.**
- "Return to title" only loads the `Title` scene. It does not go through `HSceneProc.EndProc`, and `HFlag.isHSceneEnd` is not set.
- ksPlug learns that H ended only from the `HSceneProc.EndProc` prefix (`HManage.EndH` → `Dealer.SetContext(Default)`) and from `HFlag.isHSceneEnd` in `Dealer.Update`.
  `Core.OnSceneLoad` resets `InStory` on `Title`, but not `Dealer.Ctx`. So `Ctx` stays `H`.
- `HCtrl` is a component on the `Dealer` object (DontDestroyOnLoad). It is destroyed only when `SetContext` changes away from `H`, and the next `SetContext(H)` adds a second one.
- Invited girls (`GuestRoot`, made a child of the `Dealer` by `ParentToDealer(..., destroyAfterScene: true)`) are destroyed by `DestroyAfterH` only after `Ctx != H`. So they survive the scene change.
- `Dealer.ChaCoresInH` is cleared only while `Ctx != H`, so the old entries are still there in the next H. `EndH` then fails at the first `item.Typ` of the loop (IL 0x37).

The VR version is not affected: ksPlugVR sets `Context.Title` when `VRTitle` is loaded (and cleans `HCtrl` / `ChaCoresInH` there).

**Suggested fix.** In `Core.OnSceneLoad`, when `Title` is loaded and `dealer.Ctx` is `H` (or `Esthetic` / `EstResult`), call `dealer.SetContext(Context.Default)` (and destroy any extra `HCtrl` on the dealer).
`EndH` itself cannot run at that point (the `HFlag` is gone), and the game has already discarded the H result. A null / destroyed check on the `ChaCoresInH` entries in `EndH` would also keep a stale state from blocking the next H.

**Patch.** On `Title` (single mode), if `Ctx` is still `H` / `Esthetic` / `EstResult` and there is no `HSceneProc`, the patch calls ksPlug's own `Dealer.SetContext(Default)` and destroys every `HCtrl` on the dealer.
The invited girls are then destroyed by ksPlug's `DestroyAfterH` as after a normal H (confirmed: 11 girls, none left 5 seconds later, no ksPlug exceptions afterwards).
As a safety net, a prefix on `HManage.EndH` removes null / destroyed entries from `ChaCoresInH`, and a finalizer on `Core.HSceneProc_EndProc` stops an exception and sets `Ctx` to `Default`, so the game's `EndProc` can finish.

## 11. Game: H buttons stay disabled in service positions (`voiceWait`)

Not a ksPlug bug, but ksPlug users with many invited girls hit it more often, and it looks like "the orgasm does not continue" (seen in a 3P service position).

**What happens.** In a service position, the finish buttons ("inside" / "outside"), the end button and the position menu stop responding. Clicking them does nothing.
The game disables all of them while `HFlag.voiceWait` is `true` (`HSprite.Houshi3PProc`: `categoryActionButton.SetEnable(!voiceWait)`, `btnEnd.interactable`).
Normally `voiceWait` is `true` only while the game waits for a voice line to end, and it is cleared afterwards.

**Cause.** `HSprite.OnSpeedUpClick` (left click on the speed pad) sets `voiceWait = true` in service positions when the girl's animator state is `Idle`, so that the `Idle` step waits for the voices before starting.
`HActionBase.SetPlay` changes `nowAnimStateName` at once, but the animator still reports `Idle` until its next update. If the pad is clicked in the same frame in which `Idle` switched to `WLoop`,
`voiceWait` becomes `true` after the `Idle` step has already finished. `WLoop`, `SLoop` and `OLoop` (before the finish is chosen) never clear `voiceWait`, so the buttons stay disabled.
Our diagnostic log recorded this twice (`WLoop` → `OLoop` with `voiceWait` true, both girls only breathing, buttons disabled, right after a speed-pad click while already in `WLoop`).
Some ksPlug inputs set `voiceWait = false` (for example a left click on the main girl's body in `HAction.MainActionControl`), so it can recover by chance; when invited girls stand in front of the main girls, those clicks hit the guests instead.

Long voice lines make the player click more while waiting: in 3P the game waits until both girls finish (`IsCheckVoicePlay(0) && IsCheckVoicePlay(1)`), and replacement voice packs can have lines of 10–20 seconds.

**Suggestion.** This is game code. ksPlugPatcher_NRE already fixes it with a Harmony patch on the game (below), so no change in ksPlug is needed. If you want ksPlug to handle it as well, feel free to use the code of the patch (`HoushiVoiceWaitFix.cs`, MIT).

**Patch.**
- After `HSprite.OnSpeedUpClick`: if it changed `voiceWait` from `false` to `true` while `nowAnimStateName` is no longer `Idle`, set it back to `false` (confirmed in game: it fired twice and the H continued normally).
- Safety net in `HSceneProc.Update`: in `houshi` / `houshi3P`, in `WLoop` / `SLoop` / `OLoop` before the finish (`rePlay` 0), if `voiceWait` has been `true` for 3 seconds with no pending position change, no click, and no voice to wait for (same rule as `IsCheckVoicePlay`), set it to `false`.

## 12. Game VRTK: ksPlugVR menus cannot be clicked where they overlap the title UI

Not a ksPlug bug, but it is what ksPlugVR users see on the VR title screen.

**Symptom.** On the VR title screen, where a ksPlugVR menu overlaps the area of the title UI canvas, the pointer ray changes color and goes through the ksPlug menu to the title menu behind it.
Clicks on the ksPlug menu are ignored in that area. Outside the area the menu works.

**Cause.** This is in the game's VRTK.
`VRTK_VRInputModule.CheckRaycasts` puts the ray origin and direction into `pointerEventData.pointerCurrentRaycast` (`worldPosition` / `worldNormal`) and calls `EventSystem.RaycastAll`.
Each `VRTK_UIGraphicRaycaster.Raycast` builds its ray from that value. When a raycaster hits something, `SetNearestRaycast` overwrites `pointerCurrentRaycast` with its own hit:
`worldPosition` becomes the hit point and `worldNormal` becomes zero (it is not set in the `RaycastResult`).
Every raycaster that runs after that builds its ray from the hit point with a zero direction, so it never hits anything.

The title `Canvas` is registered before the ksPlugVR menu canvases (made in `Menu.VRUI.CreateVRmenu`), and a `VRTK_UICANVAS_DRAGGABLE_PANEL` covers the whole title canvas.
So inside the title canvas area the ksPlug menus are always missed, even when they are nearer. Both canvases have `sortingOrder` 0, so the sort order is not the cause
(reordering the results, which we tried first, did not help: the ksPlug menu was never in the results there).

**Suggestion.** This is game code. ksPlugPatcher_NRE already fixes it with a Harmony patch (below), so no change in ksPlug is needed.
If you want ksPlugVR to handle it as well, the same idea works from ksPlugVR: give the raycaster of its menu canvases the original ray (the pointer's origin and direction) instead of `pointerCurrentRaycast`.
Feel free to use the code of the patch (`VRMenuRaycastPriority.cs`, MIT).

**Patch.** `FixRayForKsPlugMenus` (VR only, on by default):
- While `CheckRaycasts` runs, the first raycaster's input (the original origin and direction) is recorded.
- Just before a raycaster on a ksPlugVR menu canvas runs, the original ray is put back into `pointerCurrentRaycast`. The game's own canvases are raycast as before.
- When both are hit, the EventSystem sorts by `sortingOrder` and then distance, so the nearer ksPlug menu comes first.

Confirmed in game with ksPlugVR 1.7.3: the ksPlug menus can be clicked over the title UI, and the title UI still works where no ksPlug menu is in front of it.
The first attempt, `PreferKsPlugMenu` (move ksPlug hits to the front of the results), is kept as an option but is off by default.

## 13. Game (KKS): unused textures are not unloaded after outfit changes in H

Not a ksPlug bug, but ksPlug makes it more visible (many girls, many outfit changes in one H). IllusionFixes is also involved, and a fix is **not required**:
leaving H frees everything, and the cleanup itself makes the game stop for a moment. This patch therefore makes it a choice (on by default, can be switched off).

**Symptom.** In H, each outfit change leaves the textures of the old outfit (RenderTextures and others) in memory until H ends.
With 6 girls, repeated outfit changes added about +5.6 GB of VRAM; in VR the VRAM overflowed and SteamVR crashed. Leaving H frees it all.

**Cause.**
- `ChaControl.ReloadNoAsync` (used by `ChangeCoordinateTypeAndReload` and the other reloads) calls `Resources.UnloadUnusedAssets` only when `Manager.Character.enableCharaLoadGCClear` is `true`.
- In Koikatsu Sunshine, `Manager.Character.BeginLoadAssetBundle(_resources: true)` sets it to `false`. It is called from `ChaLoadingTask.Reserve` (loading characters asynchronously, as when H starts) and from `ActionScene`.
  `EndLoadAssetBundle` does not set it back, and `ChaLoadingTask.Loading` does not call `EndLoadAssetBundle` at all.
  Only `ActionScene`, `NPC`, `Cycle`, `CustomScene` and Studio set it back to `true`. So after H loads its characters, the value stays `false` until H ends.
  In our logs it changed to `false` when H loaded and stayed `false` for every outfit change in H. (In Koikatsu, `BeginLoadAssetBundle` does not change the value.)
- IllusionFixes (`KKS_Fix_ResourceUnloadOptimizations`) handles `UnloadUnusedAssets` and skips it while RAM use is below its threshold (75% by default, "Optimize Memory Threshold").
  It does not look at VRAM. So even if the value were set back to `true`, the cleanup would rarely run on a PC with a lot of RAM.

**Why a fix is optional.** One `UnloadUnusedAssets` stops the game for about 0.6–0.7 s in H (the time grows with the number of loaded objects, not with what is freed).
Koikatsu runs it on every outfit change. With few girls or enough VRAM, keeping the old textures until the end of H does no harm.

**Suggestion.** No change in ksPlug is needed. If you want ksPlug to free them, one `Resources.UnloadUnusedAssets()` after a group of outfit changes is enough (not one per change).
Feel free to use the code of the patch (`HOutfitCleanup.cs`, MIT).

**Patch.** `EnableHOutfitCleanup` (on by default) with `Timing`, `DelaySeconds`, `IdleFallbackSeconds` and `BypassIllusionFixesMemoryCheck`. It works only in H and only while the value is `false`, and it does not change the value.
- `Timing = WithNextChange` (default): at the next outfit change that comes at least `DelaySeconds` (5 s) after the previous one, it unloads once for all changes so far.
  The stop then overlaps the stop of the outfit change itself, and several quick changes in a row are cleaned up together. The textures of the last change stay until the next change or the end of H
  (`IdleFallbackSeconds` can clean them up after a quiet time; 0 = off by default).
- `Timing = AfterDelay`: `DelaySeconds` after the last outfit change. This shows up as a separate short stop after the change.
- `BypassIllusionFixesMemoryCheck` (on by default): when IllusionFixes is installed, its original unload is called directly, so the RAM check does not skip it.

Confirmed in game with ksPlugVR 1.7.3: textures no longer build up with repeated outfit changes in H (RenderTextures with one girl stayed at 141–240 instead of growing), and each cleanup took 0.60–0.66 s.

## License of this document and the patch

MIT. Feel free to use any part of the patch or of this document in ksPlug.
