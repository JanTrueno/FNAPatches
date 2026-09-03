// Pure-managed replacement for Steamworks.NET.dll (One Shot: World Machine Edition).
//
// OneShotMG.Game1's constructor wraps `new SteamManager()` -- and SteamAPI.Init() is the
// very first call inside it -- in a try/catch that, on any exception, logs at
// LogManager.LogLevel.Error and calls Environment.Exit(0). Error-level logging calls
// System.Windows.Forms.MessageBox.Show(), which needs libgdiplus; on a handheld without it,
// that throws too, masking the real problem behind a GDI+ stack trace.
//
// The actual failure happens earlier: the real Steamworks.NET.dll P/Invokes into
// steam_api64 (a native Windows PE DLL) via Steamworks.NativeMethods, which Mono cannot
// load on Linux/ARM at all. That's what throws first.
//
// This assembly replaces Steamworks.NET.dll outright, so no native library is ever
// touched. SteamAPI.Init() returns false and every other call is an inert no-op, exactly
// mirroring how the game already treats "Steam absent" everywhere else in its own code:
//   - InputManager.Update() reads Game1.steamMan.IsSteamInputControllerConnected(), and
//     only calls into Steam Input when it's true; otherwise it falls back to MonoGame's
//     own GamePad.GetState(0)/Keyboard.GetState(). IsSteamInputControllerConnected() is
//     just numberOfConnectedControllers >= 1, and GetConnectedControllers() here always
//     reports 0 -- so the game uses standard controller/keyboard input throughout, the
//     same as it would if a real Steam client saw no Steam Input controllers connected.
//   - SteamUserStats achievement/stat calls are fire-and-forget already (no caller checks
//     their return value for control flow beyond the achievement UI itself).
//
// Assembly identity (name, version, public key) must match exactly what OneShotMG.exe
// references, or Mono's binder rejects it at load time -- verified against the AssemblyRef
// table of the shipped OneShotMG.exe and the type/field layout of the real
// Steamworks.NET.dll it ships with (20.2.0.0), via ikdasm, rather than assumed.
using System;
using System.Reflection;

[assembly: AssemblyVersion("20.2.0.0")]

namespace Steamworks
{
    public static class SteamAPI
    {
        public static bool Init() => false;
        public static void RunCallbacks() { }
        public static void Shutdown() { }
    }

    public static class SteamApps
    {
        // Real signature returns an ELanguage-like value in this game's Steamworks.NET
        // build; the game only ever ToStrings it for a language-code lookup, so an empty
        // string is a safe "no override" default matching an unset/failed lookup.
        public static string GetCurrentGameLanguage() => "";
    }

    public static class SteamFriends
    {
        public static string GetPersonaName() => "";
    }

    public static class SteamUtils
    {
        public static bool IsSteamRunningOnSteamDeck() => false;
    }

    public static class SteamUserStats
    {
        public static bool RequestCurrentStats() => false;

        public static bool GetAchievement(string pchName, out bool pbAchieved)
        {
            pbAchieved = false;
            return false;
        }

        public static bool SetAchievement(string pchName) => false;
        public static bool StoreStats() => false;
    }

    // ---- Steam Input --------------------------------------------------------------
    //
    // GetConnectedControllers always reporting 0 is what makes every other Steam Input
    // call in the game unreachable in practice (see IsSteamInputControllerConnected above);
    // the rest are stubbed out only so SteamManager's constructor and glyph-lookup helpers,
    // which call them unconditionally regardless of controller count, compile and run
    // without touching a controller handle that was never actually populated.
    public static class SteamInput
    {
        public static bool Init(bool bExplicitlyCallRunFrame) => true;
        public static void EnableDeviceCallbacks() { }
        public static void RunFrame(bool bReservedValue = false) { }

        public static int GetConnectedControllers(InputHandle_t[] handlesOut) => 0;

        public static InputActionSetHandle_t GetActionSetHandle(string pszActionSetName) => new InputActionSetHandle_t(0);
        public static void ActivateActionSet(InputHandle_t inputHandle, InputActionSetHandle_t actionSetHandle) { }

        public static InputDigitalActionHandle_t GetDigitalActionHandle(string pszActionName) => new InputDigitalActionHandle_t(0);
        public static InputAnalogActionHandle_t GetAnalogActionHandle(string pszActionName) => new InputAnalogActionHandle_t(0);

        public static int GetDigitalActionOrigins(InputHandle_t inputHandle, InputActionSetHandle_t actionSetHandle,
            InputDigitalActionHandle_t digitalActionHandle, EInputActionOrigin[] originsOut) => 0;

        public static int GetAnalogActionOrigins(InputHandle_t inputHandle, InputActionSetHandle_t actionSetHandle,
            InputAnalogActionHandle_t analogActionHandle, EInputActionOrigin[] originsOut) => 0;

        public static string GetGlyphPNGForActionOrigin(EInputActionOrigin eOrigin, ESteamInputGlyphSize eSize, uint unFlags) => "";

        public static InputDigitalActionData_t GetDigitalActionData(InputHandle_t inputHandle, InputDigitalActionHandle_t digitalActionHandle)
            => new InputDigitalActionData_t();

        public static InputAnalogActionData_t GetAnalogActionData(InputHandle_t inputHandle, InputAnalogActionHandle_t analogActionHandle)
            => new InputAnalogActionData_t();

        public static bool ShowBindingPanel(InputHandle_t inputHandle) => false;
    }

    // ---- Value types ---------------------------------------------------------------
    // Field names and the ulong-handle shape match the real Steamworks.NET.dll exactly
    // (confirmed via ikdasm), since the game's IL reads these fields directly
    // (e.g. `value.m_InputDigitalActionHandle != 0L`).

    // All four handle structs implement IEquatable<T> in the real Steamworks.NET.dll and
    // the compiled game calls the == operator directly (e.g.
    // `if (actionSetHandle == emptyActionSetHandle)` in SteamManager.RunFrame) -- confirmed
    // the hard way, via a MissingMethodException for op_Equality at runtime the first time
    // this stub omitted it. All four get the same treatment now rather than fixing them
    // one crash at a time.

    public struct InputHandle_t : IEquatable<InputHandle_t>
    {
        public ulong m_InputHandle;
        public InputHandle_t(ulong value) { m_InputHandle = value; }
        public bool Equals(InputHandle_t other) => m_InputHandle == other.m_InputHandle;
        public override bool Equals(object obj) => obj is InputHandle_t other && Equals(other);
        public override int GetHashCode() => m_InputHandle.GetHashCode();
        public static bool operator ==(InputHandle_t a, InputHandle_t b) => a.Equals(b);
        public static bool operator !=(InputHandle_t a, InputHandle_t b) => !a.Equals(b);
    }

    public struct InputActionSetHandle_t : IEquatable<InputActionSetHandle_t>
    {
        public ulong m_InputActionSetHandle;
        public InputActionSetHandle_t(ulong value) { m_InputActionSetHandle = value; }
        public bool Equals(InputActionSetHandle_t other) => m_InputActionSetHandle == other.m_InputActionSetHandle;
        public override bool Equals(object obj) => obj is InputActionSetHandle_t other && Equals(other);
        public override int GetHashCode() => m_InputActionSetHandle.GetHashCode();
        public static bool operator ==(InputActionSetHandle_t a, InputActionSetHandle_t b) => a.Equals(b);
        public static bool operator !=(InputActionSetHandle_t a, InputActionSetHandle_t b) => !a.Equals(b);
    }

    public struct InputDigitalActionHandle_t : IEquatable<InputDigitalActionHandle_t>
    {
        public ulong m_InputDigitalActionHandle;
        public InputDigitalActionHandle_t(ulong value) { m_InputDigitalActionHandle = value; }
        public bool Equals(InputDigitalActionHandle_t other) => m_InputDigitalActionHandle == other.m_InputDigitalActionHandle;
        public override bool Equals(object obj) => obj is InputDigitalActionHandle_t other && Equals(other);
        public override int GetHashCode() => m_InputDigitalActionHandle.GetHashCode();
        public static bool operator ==(InputDigitalActionHandle_t a, InputDigitalActionHandle_t b) => a.Equals(b);
        public static bool operator !=(InputDigitalActionHandle_t a, InputDigitalActionHandle_t b) => !a.Equals(b);
    }

    public struct InputAnalogActionHandle_t : IEquatable<InputAnalogActionHandle_t>
    {
        public ulong m_InputAnalogActionHandle;
        public InputAnalogActionHandle_t(ulong value) { m_InputAnalogActionHandle = value; }
        public bool Equals(InputAnalogActionHandle_t other) => m_InputAnalogActionHandle == other.m_InputAnalogActionHandle;
        public override bool Equals(object obj) => obj is InputAnalogActionHandle_t other && Equals(other);
        public override int GetHashCode() => m_InputAnalogActionHandle.GetHashCode();
        public static bool operator ==(InputAnalogActionHandle_t a, InputAnalogActionHandle_t b) => a.Equals(b);
        public static bool operator !=(InputAnalogActionHandle_t a, InputAnalogActionHandle_t b) => !a.Equals(b);
    }

    public struct InputDigitalActionData_t
    {
        public byte bState;
        public byte bActive;
    }

    public enum EInputSourceMode
    {
        None = 0
    }

    public struct InputAnalogActionData_t
    {
        public EInputSourceMode eMode;
        public float x;
        public float y;
        public byte bActive;
    }

    public enum EInputActionOrigin
    {
        k_EInputActionOrigin_None = 0
    }

    public enum ESteamInputGlyphSize
    {
        k_ESteamInputGlyphSize_Small = 0,
        k_ESteamInputGlyphSize_Medium = 1,
        k_ESteamInputGlyphSize_Large = 2,
        k_ESteamInputGlyphSize_Count = 3
    }
}
