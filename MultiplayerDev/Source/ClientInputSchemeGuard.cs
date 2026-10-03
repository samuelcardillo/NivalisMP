using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine.InputSystem;
using PlayerInputManager = Nivalis.PlayerInputManager;

namespace NivalisTogether
{
    // Native SwitchControlScheme unpairs all devices before finding a matching scheme.
    // Ignore only proven unsupported devices, without repairing pairing or choosing a scheme.
    internal static class ClientInputSchemeGuard
    {
        private static bool installed, enabled;
        private static Action<string> diagnostic;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static int reports, suppressed;
        private static double nextReport;

        internal static void SetEnabled(bool value, Action<string> log = null)
        {
            enabled = false;
            diagnostic = log;
            reports = suppressed = 0; nextReport = 0;
            if (!value || Plugin.Worker || !DedicatedWorldRuntime.IsIsolated) return;
            if (!installed)
            {
                var method = typeof(PlayerInputManager).GetMethod("SwitchControlScheme",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null, new[] { typeof(InputDevice) }, null);
                if (method == null || method.ReturnType != typeof(void))
                    throw new MissingMethodException("Expected void PlayerInputManager.SwitchControlScheme(InputDevice).");
                new Harmony("local.nivalis.together.client-input-scheme").Patch(method,
                    prefix: new HarmonyMethod(typeof(ClientInputSchemeGuard), nameof(BeforeSwitch)),
                    postfix: new HarmonyMethod(typeof(ClientInputSchemeGuard), nameof(AfterSwitch)));
                installed = true;
            }
            enabled = true;
        }

        private static bool BeforeSwitch(PlayerInputManager __instance, InputDevice __0, out int __state)
        {
            __state = 0;
            if (!enabled || Plugin.Worker) return true;
            bool?[] support = null;
            string unavailable = "";
            try
            {
                var schemes = __instance?.input?.asset?.m_ControlSchemes;
                if (__0 != null && schemes != null && schemes.Length > 0 && schemes.Length <= ClientInputSchemePolicy.MaximumSchemes)
                {
                    support = new bool?[schemes.Length];
                    for (int i = 0; i < support.Length; i++)
                    {
                        var scheme = schemes[i];
                        if (!ReferenceEquals(scheme, null)) support[i] = scheme.SupportsDevice(__0);
                    }
                }
            }
            catch (Exception ex) { support = null; unavailable = ex.GetType().Name; }
            bool block = ClientInputSchemePolicy.ShouldBlock(enabled, Plugin.Worker, __0 != null, support);
            if (ShouldReport())
            {
                __state = block ? 1 : 2;
                Write("CLIENT_INPUT_SWITCH before decision=" + (block ? "ignoreUnsupported" : "native") +
                    " device={" + Device(__0) + "} schemes=" + (support?.Length.ToString() ?? "unavailable") +
                    " unreadable=" + unavailable + " paired=" + Paired(__instance) + " suppressed=" + suppressed);
                suppressed = 0;
            }
            return !block;
        }

        private static void AfterSwitch(PlayerInputManager __instance, InputDevice __0, int __state)
        {
            if (__state == 0) return;
            Write("CLIENT_INPUT_SWITCH after decision=" + (__state == 1 ? "ignoreUnsupported" : "native") +
                " device={" + Device(__0) + "} paired=" + Paired(__instance));
        }

        private static bool ShouldReport()
        {
            double now = Clock.Elapsed.TotalSeconds;
            if (reports >= 8 && now < nextReport) { suppressed++; return false; }
            reports++; nextReport = now + 10; return true;
        }
        private static string Device(InputDevice device)
        {
            try { return device == null ? "null" : "id=" + device.deviceId + ",type=" + Cut(device.GetIl2CppType()?.FullName, 100) + ",name=" + Cut(device.displayName, 64); }
            catch (Exception ex) { return "unavailable:" + ex.GetType().Name; }
        }
        private static string Paired(PlayerInputManager manager)
        {
            try
            {
                if (manager == null) return "missing";
                var user = manager.user;
                return user.valid ? user.pairedDevices.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "invalidUser";
            }
            catch (Exception ex) { return "unavailable:" + ex.GetType().Name; }
        }
        private static void Write(string message) { try { diagnostic?.Invoke(message); } catch { } }
        private static string Cut(string value, int limit)
        { value = (value ?? "").Replace('\r', ' ').Replace('\n', ' '); return value.Length <= limit ? value : value.Substring(0, limit); }
    }

    internal static class ClientInputSchemePolicy
    {
        internal const int MaximumSchemes = 64;
        internal static bool ShouldBlock(bool sessionEnabled, bool worker, bool hasDevice, bool?[] supports)
        {
            if (!sessionEnabled || worker || !hasDevice || supports == null || supports.Length == 0 || supports.Length > MaximumSchemes) return false;
            foreach (bool? supported in supports) if (supported != false) return false;
            return true;
        }
    }
}
