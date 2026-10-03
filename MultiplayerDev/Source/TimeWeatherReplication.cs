using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Nivalis;
using Nivalis.Weather;

namespace NivalisTogether.Dedicated
{
    // These DTOs contain only ordinary managed values so a server does not need Unity to deserialize them.
    public sealed class TimeWeatherSnapshot
    {
        public bool HasTime { get; set; }
        public int TotalGameSeconds { get; set; }
        public bool HasForecast { get; set; }
        public string[] ForecastGuids { get; set; } = Array.Empty<string>();
        public bool BadWeather { get; set; }
        public float RainIntensity { get; set; }
        public float PuddleAmount { get; set; }
        public float WetnessAmount { get; set; }
        public float SnowAmount { get; set; }
        public WeatherSceneSnapshot[] Scenes { get; set; } = Array.Empty<WeatherSceneSnapshot>();
    }

    public sealed class WeatherSceneSnapshot
    {
        public string SceneName { get; set; } = "";
        public int SceneIndex { get; set; } = -1;
        public string LightCycleForecastGuid { get; set; } = "";
        public float WindIntensity { get; set; }
        public float WindRotation { get; set; }
        public float WindWavesIntensity { get; set; }
        public float WindTurbulence { get; set; }
        public float Thunder { get; set; }
        public float Fog { get; set; }
        public float Dust { get; set; }
        public float Wetness { get; set; }
        public float Puddles { get; set; }
        public float Rain { get; set; }
        public bool IsSnowing { get; set; }
        public float Snow { get; set; }
        public float Blizzard { get; set; }
    }

    /// <summary>The native accumulation rule, kept separately for each map instead of the worker camera's singleton climate.</summary>
    internal sealed class SceneWeatherAccumulation
    {
        public int LastTime { get; private set; } = -1;
        public float Puddles { get; private set; }
        public float Wetness { get; private set; }
        public float Snow { get; private set; }
        public void Advance(int time, float rain, bool snowing, float blizzard, float puddleSpeed, float wetnessSpeed, float snowSpeed)
        {
            float puddles = Math.Clamp(rain > .2f ? 1 : rain * 3, 0, 1);
            float wetness = Math.Clamp(rain > .1f ? 1 : rain * 3, 0, 1);
            float snow = Math.Clamp(blizzard > .1f ? 1 : blizzard * 3, 0, 1);
            int delta = time - LastTime;
            if (LastTime < 0 || delta < 0 || delta > 60) { Puddles = puddles; Wetness = wetness; Snow = snow; }
            else
            {
                Puddles = Move(Puddles, puddles, delta * puddleSpeed);
                Wetness = Move(Wetness, wetness, delta * wetnessSpeed);
                if ((snow > 0 && snowing) || (snow == 0 && !snowing)) Snow = Move(Snow, snow, delta * snowSpeed);
            }
            LastTime = time;
        }
        private static float Move(float current, float target, float maximumDelta) =>
            Math.Max(0, Math.Abs(target - current) <= maximumDelta ? target : current + Math.Sign(target - current) * maximumDelta);
    }

    internal static class SceneWeatherPolicy
    {
        public static WeatherSceneSnapshot Find(WeatherSceneSnapshot[] scenes, int index, string name)
        {
            if (index >= 0)
                foreach (var scene in scenes ?? Array.Empty<WeatherSceneSnapshot>()) if (scene.SceneIndex == index) return scene;
            foreach (var scene in scenes ?? Array.Empty<WeatherSceneSnapshot>())
                if (scene.SceneIndex < 0 && String.Equals(scene.SceneName, name, StringComparison.Ordinal)) return scene;
            return null;
        }
    }

    /// <summary>Call all methods on Unity's main thread. Transport/session authentication belongs to the caller.</summary>
    public static class TimeWeatherReplication
    {
        private static bool clientAuthority;
        private static Harmony harmony;
        private static WeatherForecastController frozenForecast;
        private static bool previousStopProgression;
        private static TimeWeatherSnapshot latest;
        private static IntPtr lastTimeManager;
        private static IntPtr lastWeatherManager;
        private static readonly Dictionary<IntPtr,long> displayedClocks = new Dictionary<IntPtr,long>();
        private static readonly Dictionary<int, SceneWeatherAccumulation> sceneAccumulations = new Dictionary<int, SceneWeatherAccumulation>();
        private static IntPtr accumulationOwner;
        private static string lastDisplayError = "";

        public static bool ClientAuthority => clientAuthority;

        public static TimeWeatherSnapshot Capture()
        {
            var snapshot = new TimeWeatherSnapshot();
            var time = TimeOfDayManager._instance;
            if (time != null && time.currentTime != null)
            {
                snapshot.HasTime = true;
                snapshot.TotalGameSeconds = TimeOfDayManager.CurrentTime.TotalGameSeconds;
            }
            var forecast = WeatherForecastController._instance;
            if (forecast != null)
            {
                snapshot.HasForecast = true;
                var calculated = forecast.calculatedForecasts;
                var guids = new List<string>();
                if (calculated != null)
                    for (int i = 0; i < calculated.Count; i++) guids.Add(calculated[i] == null ? "" : calculated[i].Guid);
                snapshot.ForecastGuids = guids.ToArray();
                snapshot.BadWeather = forecast.BadWeather;
                snapshot.RainIntensity = forecast.RainIntensity;
                snapshot.PuddleAmount = forecast._puddleAmount;
                snapshot.WetnessAmount = forecast._wetnessAmount;
                snapshot.SnowAmount = forecast._snowAmount;
            }
            var scenes = new List<WeatherSceneSnapshot>();
            var capturedScenes = new HashSet<int>();
            var game = GameSceneManager._instance;
            if (snapshot.HasTime && forecast != null && game != null && game.worldLocations != null)
            {
                if (accumulationOwner != forecast.Pointer) { sceneAccumulations.Clear(); accumulationOwner = forecast.Pointer; }
                int period = forecast.GetForecastIndex(TimeOfDayManager.CurrentTime);
                foreach (var location in game.worldLocations)
                {
                    if (location == null || location.SceneIndex < 0 || string.IsNullOrEmpty(location.SceneName) || !capturedScenes.Add(location.SceneIndex)) continue;
                    // Native configuration uses a scoped RNG seeded by period +
                    // sceneIndex * 419 and honors that map's weather overrides.
                    // It does not require the scene to be loaded on this worker.
                    var config = forecast.GetCurrentWeatherConfiguration(location.SceneIndex);
                    if (!sceneAccumulations.TryGetValue(location.SceneIndex, out var accumulation))
                    { accumulation = new SceneWeatherAccumulation(); sceneAccumulations.Add(location.SceneIndex, accumulation); }
                    accumulation.Advance(snapshot.TotalGameSeconds, config.globalRainAmount, config.isSnowing, config.globalBlizzard,
                        forecast.puddlesAccumulationSpeed, forecast.wetnessAccumulationSpeed, forecast.snowAccumulationSpeed);
                    config.globalPuddles = accumulation.Puddles; config.globalWetness = accumulation.Wetness; config.globalSnow = accumulation.Snow;
                    var preset = forecast.GetForecast(period, location.SceneIndex);
                    scenes.Add(SceneState(location.SceneName, location.SceneIndex, config, preset == null ? "" : preset.Guid));
                }
            }
            foreach (var manager in WeatherManagers())
            {
                int weatherScene = WeatherForecastController.GetWeatherSceneIndex(manager);
                if (capturedScenes.Contains(weatherScene)) continue;
                var config = WeatherManagerConfiguration.GetCurrent(manager);
                var scene = SceneState(manager.gameObject.scene.name, weatherScene, config, "");
                var light = manager.LightCycleManager;
                if (forecast != null && forecast.forecastTypes != null && light != null && light.CurrentSettings != null)
                    for (int i = 0; i < forecast.forecastTypes.Length; i++)
                    {
                        var preset = forecast.forecastTypes[i];
                        if (preset != null && preset.LightCycleSettings == light.CurrentSettings)
                        { scene.LightCycleForecastGuid = preset.Guid; break; }
                    }
                scenes.Add(scene);
            }
            snapshot.Scenes = scenes.ToArray();
            return snapshot;
        }

        private static WeatherSceneSnapshot SceneState(string name, int index, WeatherManagerConfiguration config, string forecastGuid) => new WeatherSceneSnapshot
        {
            SceneName = name, SceneIndex = index, LightCycleForecastGuid = forecastGuid,
            WindIntensity = config.globalWindIntensity, WindRotation = config.windRotation,
            WindWavesIntensity = config.windWavesIntensity, WindTurbulence = config.windTurbulance,
            Thunder = config.globalThunder, Fog = config.globalFogAmount, Dust = config.globalDustAmount,
            Wetness = config.globalWetness, Puddles = config.globalPuddles, Rain = config.globalRainAmount,
            IsSnowing = config.isSnowing, Snow = config.globalSnow, Blizzard = config.globalBlizzard
        };

        /// <summary>Enable on a client receiving authority from the server; disable before leaving that session.</summary>
        public static void SetClientAuthority(bool enabled)
        {
            if (enabled)
            {
                EnsurePatches();
                clientAuthority = true;
                FreezeForecast();
                return;
            }
            clientAuthority = false;
            latest = null;
            lastTimeManager = lastWeatherManager = IntPtr.Zero;
            displayedClocks.Clear(); lastDisplayError = "";
            sceneAccumulations.Clear(); accumulationOwner = IntPtr.Zero;
            RestoreForecast();
        }

        public static void Apply(TimeWeatherSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            // Reject malformed input before altering live game state.
            Validate(snapshot);
            if (!clientAuthority) return;
            latest = snapshot;
            FreezeForecast();
            ApplyState(snapshot);
        }

        public static void Tick()
        {
            if (!clientAuthority) return;
            bool forecastChanged = FreezeForecast();
            var time = TimeOfDayManager._instance;
            var weather = WeatherManager._instance;
            var timePointer = time == null ? IntPtr.Zero : time.Pointer;
            var weatherPointer = weather == null ? IntPtr.Zero : weather.Pointer;
            if (latest != null && (forecastChanged || timePointer != lastTimeManager || weatherPointer != lastWeatherManager))
                ApplyState(latest);
        }

        private static void ApplyState(TimeWeatherSnapshot snapshot)
        {
            var time = TimeOfDayManager._instance;
            if (snapshot.HasTime && time != null && time.currentTime != null)
            {
                var stamp = new TimeOfDayManager.TimeStamp(snapshot.TotalGameSeconds);
                // Direct native field writes avoid replaying economy, schedules, and quest time callbacks.
                time.currentTime._runtimeValue = stamp;
                time.currentTime._isRuntimeValueInitialized = true;
                time._subsecondCounter = 0f;
                TimeOfDayManager.CurrentTime = stamp;
                TimeOfDayManager.ClockHour = stamp.ClockHour;
                TimeOfDayManager.ClockHourFloat = stamp.ClockHourFloat;
                TimeOfDayManager.ClockMinute = stamp.ClockMinute;
                TimeOfDayManager.GameplayGameDay = stamp.GameplayGameDay;
                TimeOfDayManager.TotalGameSeconds = stamp.TotalGameSeconds;
                TimeOfDayManager.TotalMinutes = stamp.TotalMinutes;
                TimeOfDayManager.TotalHours = stamp.TotalHours;
                TimeOfDayManager.TotalDaySeconds = stamp.TotalDaySeconds;
                TimeOfDayManager.CurrentDayOfWeek = stamp.CurrentDayOfWeek;
                TimeOfDayManager.PreviousDayOfWeek = stamp.PreviousDayOfWeek;
            }
            var forecast = WeatherForecastController._instance;
            if (snapshot.HasForecast && forecast != null)
            {
                // Keep the previous complete list if any referenced asset is absent in this game build.
                var list = new Il2CppSystem.Collections.Generic.List<WeatherForecastType>();
                bool complete = true;
                foreach (var guid in snapshot.ForecastGuids ?? Array.Empty<string>())
                {
                    var preset = FindPreset(forecast, guid);
                    if (preset == null) { complete = false; break; }
                    list.Add(preset);
                }
                if (complete && list.Count > 0) forecast.calculatedForecasts = list;
                var active = WeatherManager._instance;
                var localWeather = active == null ? null : FindScene(snapshot, active);
                // The controller's scalar climate describes its local camera,
                // not the world. Never copy the worker's indoor/outdoor override
                // into a client whose own weather manager is still loading.
                if (localWeather != null)
                {
                    forecast.BadWeather = localWeather.Rain > .35f || localWeather.IsSnowing;
                    forecast.RainIntensity = Math.Max(localWeather.Rain, localWeather.Snow);
                    forecast._puddleAmount = localWeather.Puddles;
                    forecast._wetnessAmount = localWeather.Wetness;
                    forecast._snowAmount = localWeather.Snow;
                }
                if (snapshot.HasTime) forecast._lastUpdateTime = snapshot.TotalGameSeconds;
            }
            foreach (var manager in WeatherManagers())
            {
                var scene = FindScene(snapshot, manager);
                if (scene != null)
                {
                    var config = new WeatherManagerConfiguration
                    {
                        globalWindIntensity = scene.WindIntensity,
                        windRotation = scene.WindRotation,
                        windWavesIntensity = scene.WindWavesIntensity,
                        windTurbulance = scene.WindTurbulence,
                        globalThunder = scene.Thunder,
                        globalFogAmount = scene.Fog,
                        globalDustAmount = scene.Dust,
                        globalWetness = scene.Wetness,
                        globalPuddles = scene.Puddles,
                        globalRainAmount = scene.Rain,
                        isSnowing = scene.IsSnowing,
                        globalSnow = scene.Snow,
                        globalBlizzard = scene.Blizzard
                    };
                    config.Apply(manager);
                    var light = manager.LightCycleManager;
                    var preset = FindPreset(forecast, scene.LightCycleForecastGuid);
                    if (light != null && preset != null && preset.LightCycleSettings != null && light.CurrentSettings != preset.LightCycleSettings)
                        light.ApplySkySettings(preset.LightCycleSettings, 0.01f);
                    manager.UpdateAllWeather();
                }
                if (snapshot.HasTime && manager.LightCycleManager != null)
                    manager.LightCycleManager.SetTime(new TimeOfDayManager.TimeStamp(snapshot.TotalGameSeconds).DayTime01);
            }
            lastTimeManager = time == null ? IntPtr.Zero : time.Pointer;
            var activeWeather = WeatherManager._instance;
            lastWeatherManager = activeWeather == null ? IntPtr.Zero : activeWeather.Pointer;
            if (snapshot.HasTime) RefreshClientClockDisplay();
        }

        private static WeatherSceneSnapshot FindScene(TimeWeatherSnapshot snapshot, WeatherManager manager)
        {
            int index = WeatherForecastController.GetWeatherSceneIndex(manager);
            string name = manager.gameObject.scene.name;
            return SceneWeatherPolicy.Find(snapshot.Scenes, index, name);
        }

        internal static void RefreshClientClockDisplay()
        {
            if (!clientAuthority || !DedicatedWorldRuntime.IsWorldReady || TimeOfDayManager.TotalGameSeconds < 0) return;
            int seconds = TimeOfDayManager.TotalGameSeconds;
            bool curfew = CurfewManager.IsCurfewInEffect;
            long displayKey = ((long)(seconds / 60) << 1) | (curfew ? 1L : 0L);
            var cached = WorldStateDisplayUI.cachedTimeTexts;
            // Native UpdateTime indexes its initialized cache by minute of day.
            if (cached == null || cached.Length <= (seconds % 86400) / 60) return;
            foreach (var ui in ReplicatedUiRegistry.Snapshot<WorldStateDisplayUI>())
            {
                if (ui == null || !ui.isActiveAndEnabled || !ui.gameObject.activeInHierarchy ||
                    ui.timeHolder == null || ui.timeTMP == null || ui.curfewMoonImage == null || ui.dayTMP == null) continue;
                bool prior = displayedClocks.TryGetValue(ui.Pointer, out long previous);
                if (prior && previous == displayKey) continue;
                try
                {
                    // This no-argument native handler only refreshes clock/day/moon
                    // presentation from CurrentTime. Do not dispatch global time
                    // events: those would replay economy, schedules and quests.
                    ui.OnDayChange();
                    if (displayedClocks.Count > 32) displayedClocks.Clear();
                    displayedClocks[ui.Pointer] = displayKey;
                    lastDisplayError = "";
                    if (!prior || (previous & 1) != (displayKey & 1) || (previous >> 1) / 1440 != seconds / 86400)
                        UnityEngine.Debug.Log("[Nivalis Together] CLIENT_CLOCK_DISPLAY gameSeconds=" + seconds +
                            " curfew=" + curfew + " text=" + ui.timeTMP.text + " moon=" + ui.curfewMoonImage.enabled);
                }
                catch (Exception ex)
                {
                    // Optional HUD objects can disappear during an area transition.
                    // Retry the display with the next authoritative clock snapshot.
                    string error = ex.GetType().Name;
                    if (error != lastDisplayError)
                        UnityEngine.Debug.LogWarning("[Nivalis Together] Clock display refresh deferred: " + error);
                    lastDisplayError = error;
                }
            }
        }

        private static List<WeatherManager> WeatherManagers()
        {
            var result = new List<WeatherManager>();
            var seen = new HashSet<IntPtr>();
            var managers = WeatherManager._perSceneManagers;
            if (managers != null)
                foreach (var manager in managers.Values)
                    if (manager != null && seen.Add(manager.Pointer)) result.Add(manager);
            var active = WeatherManager._instance;
            if (active != null && seen.Add(active.Pointer)) result.Add(active);
            return result;
        }

        private static WeatherForecastType FindPreset(WeatherForecastController forecast, string guid)
        {
            if (forecast == null || forecast.forecastTypes == null || String.IsNullOrEmpty(guid)) return null;
            for (int i = 0; i < forecast.forecastTypes.Length; i++)
            {
                var preset = forecast.forecastTypes[i];
                if (preset != null && String.Equals(preset.Guid, guid, StringComparison.Ordinal)) return preset;
            }
            return null;
        }

        private static bool FreezeForecast()
        {
            var forecast = WeatherForecastController._instance;
            if (forecast == null) return false;
            if (frozenForecast != null && frozenForecast.Pointer == forecast.Pointer)
            {
                forecast.stopTimedWeatherProgression = true;
                return false;
            }
            RestoreForecast();
            frozenForecast = forecast;
            previousStopProgression = forecast.stopTimedWeatherProgression;
            forecast.stopTimedWeatherProgression = true;
            return true;
        }

        private static void RestoreForecast()
        {
            if (frozenForecast != null) frozenForecast.stopTimedWeatherProgression = previousStopProgression;
            frozenForecast = null;
        }

        private static void EnsurePatches()
        {
            if (harmony != null) return;
            var pending = new Harmony("local.nivalis.together.time-weather-authority");
            try
            {
                var guard = new HarmonyMethod(typeof(TimeWeatherReplication).GetMethod(nameof(AllowLocalMutation), BindingFlags.NonPublic | BindingFlags.Static));
                Patch(pending, typeof(TimeOfDayManager), "Update", guard, Type.EmptyTypes);
                Patch(pending, typeof(TimeOfDayManager), "AddTime", guard, typeof(float));
                Patch(pending, typeof(TimeOfDayManager), "Sleep", guard, typeof(TimeOfDayManager.TimeStamp));
                Patch(pending, typeof(TimeOfDayManager), "SetTime", guard, typeof(SerializableTimeStamp));
                Patch(pending, typeof(TimeOfDayManager), "Dev_SetTime", guard, typeof(float), typeof(bool));
                // Do not detour UpdateWeatherForCurrentTime(Change<TimeStamp>):
                // its generated eight-byte value input has an unsafe Harmony
                // argument ABI. Clock mutation is guarded above, ApplyState
                // writes fields without time events, and FreezeForecast stops
                // native forecast progression while server state is applied.
                Patch(pending, typeof(WeatherForecastController), "ForceUpdateLocalWeatherManager", guard, Type.EmptyTypes);
                Patch(pending, typeof(WeatherForecastController), "Dev_SetWeatherToPreset", guard, typeof(WeatherForecastType));
                Patch(pending, typeof(WeatherForecastController), "Dev_OverrideWeather", guard, typeof(WeatherManagerConfiguration), typeof(Nivalis.DayNightCycle.LightCycleSettings));
                Patch(pending, typeof(WeatherForecastController), "OverrideWeatherForecast", guard, typeof(string), typeof(int));
                var forecastGuard = new HarmonyMethod(typeof(TimeWeatherReplication).GetMethod(nameof(FreezeTransition), BindingFlags.NonPublic | BindingFlags.Static));
                Patch(pending, typeof(WeatherForecastController), "TransitionWeatherFrom", forecastGuard, typeof(WeatherForecastType));
                harmony = pending;
            }
            catch
            {
                pending.UnpatchSelf();
                throw;
            }
        }

        private static void Patch(Harmony target, Type type, string name, HarmonyMethod prefix, params Type[] parameters)
        {
            var method = type.GetMethod(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            target.Patch(method, prefix: prefix);
        }

        private static bool AllowLocalMutation() => !clientAuthority;

        private static bool FreezeTransition(WeatherForecastType current, ref WeatherForecastType __result)
        {
            if (!clientAuthority) return true;
            __result = current;
            return false;
        }

        private static void Validate(TimeWeatherSnapshot snapshot)
        {
            if (snapshot.HasTime && snapshot.TotalGameSeconds < 0) throw new ArgumentException("Invalid world clock.");
            if ((snapshot.ForecastGuids?.Length ?? 0) > 100000 || (snapshot.Scenes?.Length ?? 0) > 256)
                throw new ArgumentException("World weather snapshot exceeds limits.");
            CheckFinite(snapshot.RainIntensity, snapshot.PuddleAmount, snapshot.WetnessAmount, snapshot.SnowAmount);
            foreach (var guid in snapshot.ForecastGuids ?? Array.Empty<string>())
                if (guid == null || guid.Length > 256) throw new ArgumentException("Invalid forecast identifier.");
            foreach (var scene in snapshot.Scenes ?? Array.Empty<WeatherSceneSnapshot>())
            {
                if (scene == null || scene.SceneIndex < -1 || scene.SceneIndex > 100000 || scene.SceneName == null || scene.SceneName.Length > 256 || (scene.LightCycleForecastGuid?.Length ?? 0) > 256)
                    throw new ArgumentException("Invalid weather scene.");
                CheckFinite(scene.WindIntensity, scene.WindRotation, scene.WindWavesIntensity, scene.WindTurbulence,
                    scene.Thunder, scene.Fog, scene.Dust, scene.Wetness, scene.Puddles, scene.Rain, scene.Snow, scene.Blizzard);
            }
        }

        private static void CheckFinite(params float[] values)
        {
            foreach (var value in values)
                if (float.IsNaN(value) || float.IsInfinity(value) || Math.Abs(value) > 1000000f)
                    throw new ArgumentException("Invalid weather value.");
        }
    }
}
