using Jam.Player;
using UnityEngine;

namespace Jam.Gameplay
{
    /// <summary>
    /// Обучение акта 1 без инструкций. Механики показывает сама игра (обскура поднимается сама после письма,
    /// сцена проступает по мере приближения к точке, свеча клонится к Маргарет, в темноте выдержка дольше).
    /// Здесь — только мысли героя (Thoughts: субтитр + голос vo_&lt;id&gt;) и кнопки серой строкой.
    /// Подсказки «если застрял» звучат, только когда игрок долго не делает нужного шага.
    /// Тексты — те же, что в документе «Выдержка — текст для озвучки»; id совпадают с именами файлов.
    /// </summary>
    public class TutorialHints : MonoBehaviour
    {
        public IntroLetter letter;
        public HandObscura hand;
        public PortableCamera portable;
        public Darkroom darkroom;
        public MargaretAI margaret;
        public PlateInventory inventory;

        [Header("Начало: обскура поднимается сама")]
        public float demoDelay = 1.2f;
        public float demoSeconds = 2.5f;

        [Header("Обучение (один раз, по событию)")]
        public string tLetter = "Стекло помнит этот дом лучше, чем его хозяин.";
        public string tAligned = "Вот. Отсюда они смотрели в объектив.";
        public string tDark = "Темно. Пластине нужно больше времени… а мне — больше тишины.";
        public string tLit = "Так лучше. При свечах они и снимались.";
        public string tBlank = "Пусто.";

        [Header("Маргарет")]
        public string mFirstSeen = "Женщина… Она не из прошлого. Она здесь сейчас — и слушает.";
        public string mLightOut = "Она гасит свет. Она не хочет, чтобы дом снимали.";
        public string mDeath = "Холодно… Она идёт на шаги. Надо двигаться тише.";

        [Header("Сцена 5: посмертный портрет")]
        public string s5Seen = "У стойки — её тень. Она всё ещё ждёт, когда её снимут.";
        public string s5Posed = "Она пришла. Стоит, как тогда… не дышать.";
        public string s5Failed = "Смазана. Я закрыл слишком рано — а она стояла до конца.";

        [Header("Проявленные сцены 1–6")]
        public string[] developed =
        {
            "Все за столом. И один стул зачем-то повёрнут к двери.",
            "Такие спокойные. Словно ничего ещё не случилось.",
            "Они звали кого-то. И все смотрят на пустой стул.",
            "Он жжёт письма. Или снимки. Хотел что-то забыть.",
            "Она стоит слишком ровно. Так стоят только на последнем снимке.",
            "Штатив… сумка… Это же мой пиджак. Это я.",
        };

        [Header("Если застрял (секунды без нужного шага)")]
        public float stuckObscura = 60f;
        public string stuckObscuraText = "Дом не покажет себя просто так. Надо смотреть сквозь стекло.";
        public float stuckSpot = 90f;
        public string stuckSpotText = "Снимки в прихожей кто-то уже снимал. Надо встать там же, где стоял он.";
        public float stuckShutter = 30f;
        public string stuckShutterText = "Ставлю штатив и открываю заслонку. И не отпускаю, пока не кончится выдержка.";
        public float stuckDevelop = 120f;
        public string stuckDevelopText = "Пластина ничего не стоит, пока не проявлена. Красный свет — там проявочная.";
        public float stuckPose = 120f;
        public string stuckPoseText = "На посмертных портретах ждут, пока фотограф откроет заслонку. Она ждёт меня.";

        [Header("Кнопки — серой строкой, без слов")]
        public string keyObscura = "ПКМ";
        public string keyCamera = "F   ·   E";
        public string keyCandle = "E";

        bool _started, _raised, _aligned, _placed, _poseOpened, _posed;
        float _tStart, _tRaised, _tAligned, _tHasPlate = -1f, _tPoseOpened;
        int _deaths;
        CameraStation _station;
        StoryDirector _story;

        void Start()
        {
            if (!margaret) margaret = FindAnyObjectByType<MargaretAI>();
            if (!inventory && portable) inventory = portable.inventory;
            if (portable) _station = portable.GetComponent<CameraStation>();

            // камеры берут в проявочной: обучение начинается с них, а не сразу после письма
            if (GearPickup.Waiting) GearPickup.Taken += OnLetterClosed;
            else if (letter) letter.Closed += OnLetterClosed;
            else OnLetterClosed();

            if (hand)
            {
                hand.Raised += h => { if (!_raised) { _raised = true; _tRaised = Time.time; } };
                hand.SceneAligned += OnAligned;
            }
            if (portable) portable.Placed += p => _placed = true;
            if (darkroom) darkroom.PlatesDeveloped += OnDeveloped;
            _story = StoryDirector.Instance ? StoryDirector.Instance : FindAnyObjectByType<StoryDirector>();
            if (_story) _story.SceneDeveloped += OnSceneDeveloped;

            Candelabra.LitByPlayer += OnCandelabraLit;
            if (margaret)
            {
                margaret.Posed += m => { if (!_posed) { _posed = true; Thoughts.Once("s5_posed", s5Posed); } };
                margaret.DousedCandle += (m, c) => Thoughts.Once("m_light_out", mLightOut);
                margaret.CaughtPlayer += OnCaught;
            }
        }

        void OnDestroy()
        {
            GearPickup.Taken -= OnLetterClosed;
            Candelabra.LitByPlayer -= OnCandelabraLit;
            if (_story) _story.SceneDeveloped -= OnSceneDeveloped;
        }

        void OnLetterClosed()
        {
            _started = true;
            _tStart = Time.time;
            // герой сам смотрит сквозь стекло — игрок видит, что делает обскура, до всяких слов про кнопку
            Invoke(nameof(Demo), GearPickup.Enabled ? 3.5f : demoDelay); // сначала «Shift — бег», потом обскура
        }

        void Demo()
        {
            if (hand) hand.Demo(demoSeconds);
            Thoughts.Once("t_letter", tLetter, keyObscura);
        }

        void OnAligned(HandObscura h, PastScene s)
        {
            if (!_aligned) { _aligned = true; _tAligned = Time.time; }
            if (s && s.requireMargaretSharp)
            {
                if (!_poseOpened) { _poseOpened = true; _tPoseOpened = Time.time; }
                Thoughts.Once("s5_seen", s5Seen);
            }
            else if (portable && portable.IsCarried) Thoughts.Once("t_aligned", tAligned);
        }

        void OnCandelabraLit(Candelabra c) => Thoughts.Once("t_lit", tLit);

        void OnDeveloped(Darkroom d, System.Collections.Generic.IReadOnlyList<Exposure.Plate> plates)
        {
            bool any = false, blank = false;
            foreach (var p in plates) { if (p.Blank) blank = true; else any = true; }
            if (blank && !any) Thoughts.Once("t_blank", tBlank);
            // сцена с Маргарет открыта, но ни одна пластина её не засчитала
            if (_posed && plates.Count > 0) StartCoroutine(CheckPoseLater());
        }

        void OnSceneDeveloped(PastScene s)
        {
            if (!s || s.index < 1 || s.index > developed.Length) return;
            Thoughts.Once($"s{s.index}_dev", developed[s.index - 1]);
        }

        void OnCaught(MargaretAI m)
        {
            // первые две смерти — мысль после возрождения; дальше игрок уже понял
            if (++_deaths > 2) return;
            Invoke(nameof(SayDeath), 3.5f);
        }

        void SayDeath() => Thoughts.Say(_deaths == 1 ? "m_death_1" : "m_death_2", mDeath);

        // сюжет засчитывает пластины по тому же событию — смотрим чуть позже, осталась ли сцена неснятой
        System.Collections.IEnumerator CheckPoseLater()
        {
            yield return new WaitForSeconds(1f);
            var gate = PastScene.All.Find(s => s && s.requireMargaretSharp && s.Current == PastScene.State.Visible);
            if (gate) Thoughts.Once("s5_failed", s5Failed);
        }

        void Update()
        {
            if (!_started) return;
            float now = Time.time;

            // свет: выдержка пошла в темноте
            if (_station && _station.InDark) Thoughts.Once("t_dark", tDark);

            // Маргарет впервые в обскуре
            if (hand && hand.IsRaised && margaret && !Thoughts.WasSaid("m_first_seen") && SeesMargaret())
                Thoughts.Once("m_first_seen", mFirstSeen);

            // ---- если застрял ----
            // без текста: герой ещё раз сам поднимает обскуру
            if (!_raised && now - _tStart > stuckObscura && Thoughts.Once("stuck_obscura", stuckObscuraText, keyObscura) && hand)
                hand.Demo(demoSeconds);
            // без текста: из комнаты с ближайшей сценой время от времени доносится щелчок затвора — эхо прошлого
            if (_raised && !_aligned && now - _tRaised > stuckSpot)
            {
                Thoughts.Once("stuck_spot", stuckSpotText);
                EchoEvery(ref _nextEcho, 9f, NearestViewPoint(), "scene_align");
            }
            if (_aligned && !_placed && now - _tAligned > stuckShutter) Thoughts.Once("stuck_shutter", stuckShutterText, keyCamera);

            bool hasPlate = inventory && inventory.Count > 0;
            if (hasPlate && _tHasPlate < 0f) _tHasPlate = now;
            else if (!hasPlate) _tHasPlate = -1f;
            if (_tHasPlate >= 0f && now - _tHasPlate > stuckDevelop) Thoughts.Once("stuck_develop", stuckDevelopText);

            // без текста: у стойки шепчет — она ждёт
            if (_poseOpened && !_posed && now - _tPoseOpened > stuckPose)
            {
                Thoughts.Once("stuck_s5", stuckPoseText);
                var gate = PastScene.All.Find(x => x && x.requireMargaretSharp && x.posePoint);
                if (gate) EchoEvery(ref _nextWhisper, 12f, gate.posePoint.position, "margaret_whisper");
            }
        }

        float _nextEcho, _nextWhisper;

        static void EchoEvery(ref float next, float period, Vector3? at, string sound)
        {
            if (at == null || Time.time < next) return;
            next = Time.time + period;
            Jam.Audio.JamAudio.Play(sound, at.Value + Vector3.up * 1.2f);
        }

        Vector3? NearestViewPoint()
        {
            if (!player) player = FindAnyObjectByType<FirstPersonController>();
            Vector3 me = player ? player.transform.position : transform.position;
            PastScene best = null;
            float bestD = float.MaxValue;
            foreach (var x in PastScene.All)
            {
                if (!x || !x.IsActive || !x.viewPoint) continue;
                float d = (x.viewPoint.position - me).sqrMagnitude;
                if (d < bestD) { bestD = d; best = x; }
            }
            return best ? best.viewPoint.position : null;
        }

        FirstPersonController player;

        bool SeesMargaret()
        {
            var eyes = hand.eyes ? hand.eyes.transform : null;
            if (!eyes) return false;
            Vector3 to = margaret.transform.position + Vector3.up * 1.4f - eyes.position;
            if (to.magnitude > 9f || Vector3.Angle(eyes.forward, to) > 25f) return false;
            return Candelabra.SameRoom(eyes.position, eyes.position + to);
        }
    }
}
