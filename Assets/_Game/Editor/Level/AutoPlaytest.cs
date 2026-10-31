using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Jam.Gameplay;
using Jam.Interaction;
using Jam.Player;
using UnityEditor;
using UnityEngine;

namespace Jam.EditorTools
{
    /// <summary>
    /// Автопрогон пунктов 4–8 из «Проверок перед билдом» без беготни мышью:
    /// обскура в проявочной → нулевая сцена → сцены 1–4 → смерть → акт 2.
    /// Всё делается через те же методы, что вызывает игрок (Interact, PlaceAt, TryPickUp), Маргарет на время прогона успокоена.
    /// Отчёт и снимки Game view — в Temp/Playtest (в проект и git не попадают).
    /// </summary>
    public static class AutoPlaytest
    {
        const string OutDir = "Temp/Playtest";
        const string PendingKey = "Jam.AutoPlaytest.Pending";

        static IEnumerator _run;
        static object _wait;
        static readonly StringBuilder Log = new();
        static int _ok, _fail;

        [MenuItem("Tools/Jam/Debug/Auto Playtest (checks 4-8)")]
        static void Launch()
        {
            if (EditorApplication.isPlaying) { Begin(); return; }
            SessionState.SetBool(PendingKey, true);
            EditorApplication.isPlaying = true;
        }

        [InitializeOnLoadMethod]
        static void Hook()
        {
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }

        static void OnPlayMode(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(PendingKey, false))
            {
                SessionState.SetBool(PendingKey, false);
                Begin();
            }
            if (s == PlayModeStateChange.ExitingPlayMode && _run != null) Finish("прогон прерван выходом из Play");
        }

        static void Begin()
        {
            Directory.CreateDirectory(OutDir);
            foreach (var f in Directory.GetFiles(OutDir)) File.Delete(f);
            Log.Clear();
            _ok = _fail = 0;
            _run = Run();
            _wait = null;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
            Debug.Log("[Playtest] Старт.");
        }

        // ---------------- маленький планировщик ----------------

        sealed class Until
        {
            public Func<bool> cond; public float deadline; public string what; public float started;
        }

        static object Sec(float s) => Time.time + s;
        static object Wait(Func<bool> cond, float timeout, string what) => new Until { cond = cond, deadline = Time.time + timeout, what = what, started = Time.time };

        static void Tick()
        {
            if (_run == null) return;
            if (!EditorApplication.isPlaying) { Finish("Play остановлен"); return; }
            try
            {
                if (_wait is float t && Time.time < t) return;
                if (_wait is Until u)
                {
                    bool done;
                    try { done = u.cond(); } catch (Exception e) { done = false; Note("ошибка в условии «" + u.what + "»: " + e.Message); }
                    if (!done && Time.time < u.deadline) return;
                    Check(u.what, done, $"{Time.time - u.started:F1} с");
                }
                _wait = null;
                if (!_run.MoveNext()) { Finish("готово"); return; }
                _wait = _run.Current;
            }
            catch (Exception e)
            {
                Note("ИСКЛЮЧЕНИЕ: " + e);
                Finish("упал с исключением");
            }
        }

        static void Finish(string why)
        {
            EditorApplication.update -= Tick;
            _run = null;
            Log.Insert(0, $"Автопрогон: {why}. OK {_ok}, FAIL {_fail}\n\n");
            Directory.CreateDirectory(OutDir);
            File.WriteAllText($"{OutDir}/report.txt", Log.ToString(), Encoding.UTF8);
            Debug.Log($"[Playtest] {why}. OK {_ok}, FAIL {_fail}. Отчёт: {Path.GetFullPath(OutDir)}/report.txt");
        }

        static void Section(string s) { Log.AppendLine(); Log.AppendLine("## " + s); }
        static void Note(string s) => Log.AppendLine("   · " + s);
        static void Check(string what, bool ok, string detail = "")
        {
            if (ok) _ok++; else _fail++;
            Log.AppendLine($"   {(ok ? "OK  " : "FAIL")} {what}{(string.IsNullOrEmpty(detail) ? "" : "  (" + detail + ")")}");
        }
        static void Shot(string name)
        {
            ScreenCapture.CaptureScreenshot(Path.GetFullPath($"{OutDir}/{name}.png"), 2);
            Note("снимок " + name);
        }

        // ---------------- помощники ----------------

        static T Find<T>() where T : UnityEngine.Object => UnityEngine.Object.FindAnyObjectByType<T>(FindObjectsInactive.Include);

        static void SetField(object o, string name, object value)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) f.SetValue(o, value); else Note($"нет поля {o.GetType().Name}.{name}");
        }

        static void Call(object o, string name)
        {
            var m = o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (m != null) m.Invoke(o, null); else Note($"нет метода {o.GetType().Name}.{name}");
        }

        static Vector3 Floor(Vector3 p)
        {
            foreach (var h in Physics.RaycastAll(p + Vector3.up * 0.5f, Vector3.down, 6f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
                if (h.normal.y > 0.7f) return h.point;
            return new Vector3(p.x, 0f, p.z);
        }

        static void Teleport(FirstPersonController fpc, Vector3 pos, Vector3 look)
        {
            look.y = 0f;
            if (look.sqrMagnitude < 1e-4f) look = fpc.transform.forward;
            var cc = fpc.GetComponent<CharacterController>();
            if (cc) cc.enabled = false;
            fpc.transform.SetPositionAndRotation(Floor(pos) + Vector3.up * 0.05f, Quaternion.LookRotation(look.normalized, Vector3.up));
            if (cc) cc.enabled = true;
            SetField(fpc, "_pitch", 8f);
            if (fpc.cameraPivot) fpc.cameraPivot.localRotation = Quaternion.Euler(8f, 0f, 0f);
        }

        static string Variants(int n)
        {
            var all = Resources.FindObjectsOfTypeAll<Transform>().Where(t => t.gameObject.scene.IsValid() &&
                (t.name.StartsWith($"BEFORE_S{n}_") || t.name.StartsWith($"AFTER_S{n}_"))).ToList();
            if (all.Count == 0) return "нет объектов BEFORE/AFTER";
            return string.Join(", ", all.Select(t => $"{t.name}={(t.gameObject.activeInHierarchy ? "виден" : "скрыт")}"));
        }

        static bool VariantsSwitched(int n)
        {
            var all = Resources.FindObjectsOfTypeAll<Transform>().Where(t => t.gameObject.scene.IsValid()).ToList();
            var before = all.Where(t => t.name.StartsWith($"BEFORE_S{n}_")).ToList();
            var after = all.Where(t => t.name.StartsWith($"AFTER_S{n}_")).ToList();
            return before.All(t => !t.gameObject.activeSelf) && after.All(t => t.gameObject.activeSelf);
        }

        // ---------------- сам прогон ----------------

        static IEnumerator Run()
        {
            yield return Sec(1.5f);
            var fpc = Find<FirstPersonController>();
            var inter = Find<Interactor>();
            var inv = Find<PlateInventory>();
            var room = Find<Darkroom>();
            var story = StoryDirector.Instance;
            var margaret = Find<MargaretAI>();
            var portable = Find<PortableCamera>();
            var station = portable ? portable.GetComponent<CameraStation>() : null;
            var hand = Find<HandObscura>();
            var flash = Find<MagnesiumFlash>();
            var viewer = Find<PlateViewer>();
            var cp = Find<DeathAndCheckpoint>();
            Section("Окружение");
            Check("игрок, проявочная, штатив, сюжет, Маргарет на месте", fpc && inter && inv && room && story && margaret && portable && station && hand);
            if (!(fpc && inter && inv && room && story && portable && station)) yield break;

            void Calm() { if (margaret && margaret.isActiveAndEnabled) margaret.Calm(600f, fpc.transform.position); }
            void CloseViewer() { if (viewer && viewer.IsOpen) viewer.ForceClose(); }
            Vector3 stand = room.standPoint.position, standFwd = room.standPoint.forward;

            // письмо
            var intro = Find<IntroLetter>();
            if (intro) SetField(intro, "_skipped", true);
            yield return Wait(() => !fpc.InputLocked, 25f, "письмо закрывается, управление возвращается");
            yield return Sec(0.5f);
            Shot("00_start");

            // ---------- 4. Проявочная ----------
            Section("4. Проявочная: взять обскуру, появилось «Shift — бег»");
            var gear = Find<GearPickup>();
            Check("обскура ждёт на верстаке", gear && GearPickup.Waiting);
            Check("до обскуры бег закрыт (только шаг)", fpc.ForceSneak);
            Check("учебную камеру у штатива нельзя забрать до снимка", portable.PickupLocked);
            int darkLights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Count(l => l.enabled && !(room.roomBounds.Contains(l.transform.position)) && !l.GetComponentInParent<FirstPersonController>());
            Note($"вне проявочной горит источников: {darkLights} (ожидается 0–1: свет штатива)");
            Teleport(fpc, stand, standFwd);
            yield return Sec(1f);
            Shot("04a_darkroom");
            if (gear) gear.Interact(inter);
            yield return Sec(1.2f);
            Check("обскура взята", !GearPickup.Waiting);
            Check("бег открыт", !fpc.ForceSneak);
            Shot("04b_obscura_taken_hint");
            int litAfter = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Count(l => l.enabled);
            yield return Sec(3f);
            int litLater = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None).Count(l => l.enabled);
            Check("дом просыпается — огни зажигаются", litLater > litAfter || litLater > darkLights + 2, $"{litAfter} → {litLater}");

            // ---------- 5. Нулевая сцена ----------
            Section("5. Нулевая сцена: снять, проявить — в обскуре появились сцены и Маргарет");
            var zero = story.scenes.FirstOrDefault(s => s && s.index == 0);
            Check("нулевая сцена создана", zero);
            var sFwd = station.transform.forward; sFwd.y = 0f;
            Teleport(fpc, station.transform.position - sFwd.normalized * 0.9f, sFwd);
            Calm();
            yield return Sec(0.5f);
            Check("штатив: можно открыть заслонку", station.CanInteract(inter));
            station.Interact(inter);
            Check("выдержка пошла", station.Current == CameraStation.State.Exposing);
            yield return Wait(() => station.Current == CameraStation.State.PlateReady, 40f, "выдержка у штатива дошла до конца");
            Shot("05a_zero_plate_ready");
            Check("после снимка камеру можно забрать", !portable.PickupLocked);
            station.Interact(inter);
            Check("пластина в сумке", inv.Count == 1, $"в сумке {inv.Count}");
            Check("камеру забрали со штатива", portable.TryPickUp() && portable.IsCarried);
            Teleport(fpc, stand, standFwd);
            yield return Sec(0.5f);
            Check("верстак принимает пластину", room.CanInteract(inter));
            room.Interact(inter);
            yield return Wait(() => zero && zero.Current == PastScene.State.Developed, 10f, "нулевая сцена проявлена");
            yield return Sec(1.8f);
            Shot("05b_zero_developed_viewer");
            CloseViewer();
            for (int i = 1; i <= 4; i++)
            {
                var s = story.scenes.FirstOrDefault(x => x && x.index == i);
                Check($"сцена {i} открыта для обскуры", s && s.Current == PastScene.State.Visible, s ? s.Current.ToString() : "нет");
            }
            Check("Маргарет проснулась", margaret && margaret.isActiveAndEnabled, margaret ? margaret.Current.ToString() : "нет");
            var s1 = story.scenes.FirstOrDefault(x => x && x.index == 1);
            if (s1 && s1.viewPoint)
            {
                Teleport(fpc, s1.viewPoint.position, s1.viewPoint.forward);
                Calm();
                SetField(hand, "_demoUntil", Time.time + 5f);
                yield return Sec(2f);
                Shot("05c_obscura_scene1");
            }

            // ---------- 6. Сцены 1–4 ----------
            Section("6. Сцены 1–4: снята и проявлена, снимок в альбоме, дом поменялся");
            for (int i = 1; i <= 4; i++)
            {
                var s = story.scenes.FirstOrDefault(x => x && x.index == i);
                if (!s || !s.viewPoint) { Check($"сцена {i} есть", false); continue; }
                foreach (var sw in s.switches) if (sw) sw.Set(true);
                Note($"сцена {i}: переключателей {s.switches.Count}, условия {(s.ConditionsMet ? "выполнены" : "НЕ выполнены")}");
                Vector3 fwd = s.viewPoint.forward; fwd.y = 0f; fwd.Normalize();
                Vector3 floor = Floor(s.viewPoint.position);
                portable.PlaceAt(floor, fwd);
                Teleport(fpc, floor - fwd * 0.8f, fwd);
                Calm();
                yield return Sec(0.5f);
                station.Interact(inter);
                float t0 = Time.time;
                yield return Wait(() => station.Current == CameraStation.State.PlateReady, 45f, $"сцена {i}: выдержка дошла до конца");
                Note($"сцена {i}: выдержка {Time.time - t0:F1} с, темно: {station.InDark}");
                station.Interact(inter);
                portable.TryPickUp();
                Check($"сцена {i}: пластина в сумке", inv.Count >= 1);
                var album = Find<FamilyAlbum>();
                var albumBefore = album ? album.LastDeveloped : null;
                Teleport(fpc, stand, standFwd);
                Calm();
                yield return Sec(0.3f);
                room.Interact(inter);
                yield return Wait(() => s.Current == PastScene.State.Developed, 10f, $"сцена {i} проявлена");
                yield return Sec(1.8f);
                Shot($"06_s{i}_viewer");
                CloseViewer();
                yield return Sec(0.5f);
                Check($"сцена {i}: дом поменялся (BEFORE скрыт, AFTER виден)", VariantsSwitched(i), Variants(i));
                if (album) Check($"сцена {i}: снимок в альбоме", album.LastDeveloped && album.LastDeveloped != albumBefore);
                Teleport(fpc, s.viewPoint.position, s.viewPoint.forward);
                Calm();
                yield return Sec(0.8f);
                Shot($"06_s{i}_room_after");
            }
            Check("4 сцены проявлены — открыта сцена 5", story.scenes.Any(x => x && x.index == 5 && x.Current == PastScene.State.Visible));

            // ---------- 7. Смерть ----------
            Section("7. Смерть: чёрный экран, рестарт у верстака, +1 магний");
            if (cp && margaret)
            {
                Teleport(fpc, stand, standFwd);
                yield return Sec(0.3f);
                cp.SetCheckpoint(fpc.transform.position, fpc.transform.rotation); // как после проявки
                if (s1) Teleport(fpc, s1.viewPoint.position, s1.viewPoint.forward);
                yield return Sec(0.3f);
                int charges = flash ? flash.Charges : -1;
                Call(margaret, "Catch");
                yield return Sec(0.4f);
                Shot("07a_caught_black");
                var blackout = cp.blackout;
                Check("экран чёрный", blackout && blackout.alpha > 0.9f);
                yield return Wait(() => !fpc.InputLocked, 8f, "после смерти управление вернулось");
                float d = Vector3.Distance(fpc.transform.position, stand);
                Check("рестарт у верстака", d < 1.5f, $"{d:F2} м от верстака");
                if (flash) Check("+1 магний", flash.Charges == Mathf.Min(charges + flash.chargesPerDeath, flash.maxCharges), $"{charges} → {flash.Charges}");
                Check("камера снова в руках", portable.IsCarried);
                Shot("07b_respawn");
            }
            else Check("есть чекпойнт и Маргарет", false);

            // ---------- 8. Акт 2 ----------
            Section("8. Акт 2 (F9): фотограф входит, шкала испуга растёт");
            var a2 = Find<Act2Director>();
            if (a2)
            {
                a2.DebugJump();
                yield return Sec(3f);
                Check("акт 2 начался", a2.Active);
                Check("Маргарет — спутница", margaret && margaret.Companion);
                Shot("08a_act2_start");
                float e0 = a2.Exposure01;
                yield return Sec(12f);
                Shot("08b_act2_12s");
                Note($"шкала испуга: {e0:F2} → {a2.Exposure01:F2} (растёт только когда фотограф снимает игрока — без игрока может стоять)");
                if (a2.photographer) Note($"фотограф: {(a2.photographer.isActiveAndEnabled ? "в доме" : "нет")} в {a2.photographer.transform.position:F1}");
            }
            else Check("есть Act2Director", false);

            Section("Не проверяется автоматически");
            Note("шкаф падает без провала в стол, финал и титры — нужно доиграть акт 2 руками");
            Note("звук: в проекте пока нет записей, кроме музыки");
        }
    }
}
