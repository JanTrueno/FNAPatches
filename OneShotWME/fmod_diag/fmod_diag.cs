// Standalone FMOD init diagnostic. Not part of the game or any patch -- OneShotMG's own
// FmodManager constructor calls fSystem.init(32, INITFLAGS.NORMAL, (IntPtr)0) but never
// checks the returned FMOD_RESULT, so a failure there is completely silent: no crash, no
// log line, just no audio. This calls the exact same Core API entry points directly against
// libfmod.so and prints the real result codes, with zero changes to the game.
//
// Signatures and entry point names (FMOD5_-prefixed, not the standard FMOD_-prefixed ones)
// are copied verbatim from OneShotMG's own decompiled P/Invoke declarations, not guessed --
// this wrapper's naming convention differs from Firelight's official C# binding.
//
// Uses the bare name "fmod" (Mono's Unix fallback naming tries "libfmod.so" among other
// candidates) rather than a dllmap, since Mono reads an assembly's .config file at load
// time -- before Main() runs -- so generating one at runtime from a command-line argument
// doesn't work. Point LD_LIBRARY_PATH at the directory holding a file literally named
// libfmod.so before running this (run.sh does that for you).
//
// Usage: LD_LIBRARY_PATH=<dir containing libfmod.so> mono fmod_diag.exe
using System;
using System.Runtime.InteropServices;

internal static class Program
{
    private const string Lib = "fmod";

    [DllImport(Lib, EntryPoint = "FMOD5_System_Create")]
    private static extern int FMOD5_System_Create(out IntPtr system);

    [DllImport(Lib, EntryPoint = "FMOD5_System_GetNumDrivers")]
    private static extern int FMOD5_System_GetNumDrivers(IntPtr system, out int numdrivers);

    [DllImport(Lib, EntryPoint = "FMOD5_System_Init")]
    private static extern int FMOD5_System_Init(IntPtr system, int maxchannels, int flags, IntPtr extradriverdata);

    [DllImport(Lib, EntryPoint = "FMOD5_System_Release")]
    private static extern int FMOD5_System_Release(IntPtr system);

    private const int FMOD_INIT_NORMAL = 0x00000000;

    private static int Main(string[] args)
    {
        Console.WriteLine("LD_LIBRARY_PATH=" + Environment.GetEnvironmentVariable("LD_LIBRARY_PATH"));

        IntPtr system;
        int result = FMOD5_System_Create(out system);
        Console.WriteLine("FMOD5_System_Create -> " + ResultName(result) + " (" + result + ")");
        if (result != 0)
        {
            Console.WriteLine("Stopping here -- System_Create must succeed before Init can be attempted.");
            return 1;
        }

        int numDrivers;
        result = FMOD5_System_GetNumDrivers(system, out numDrivers);
        Console.WriteLine("FMOD5_System_GetNumDrivers -> " + ResultName(result) + " (" + result + "), numDrivers=" + numDrivers);

        result = FMOD5_System_Init(system, 32, FMOD_INIT_NORMAL, IntPtr.Zero);
        Console.WriteLine("FMOD5_System_Init -> " + ResultName(result) + " (" + result + ")");

        FMOD5_System_Release(system);
        return result;
    }

    // A handful of the FMOD_RESULT values actually likely here; anything else just prints
    // as a bare number, which is still enough to look up against FMOD's own header.
    private static string ResultName(int r)
    {
        switch (r)
        {
            case 0: return "FMOD_OK";
            case 5: return "FMOD_ERR_BADCOMMAND";
            case 30: return "FMOD_ERR_HEADER_MISMATCH";
            case 39: return "FMOD_ERR_INVALID_PARAM";
            case 41: return "FMOD_ERR_MEMORY";
            case 53: return "FMOD_ERR_NEEDS3D";
            case 54: return "FMOD_ERR_NEEDSHARDWARE";
            case 55: return "FMOD_ERR_NET_CONNECT";
            case 60: return "FMOD_ERR_OUTPUT_ALLOCATED";
            case 61: return "FMOD_ERR_OUTPUT_CREATEBUFFER";
            case 62: return "FMOD_ERR_OUTPUT_DRIVERCALL";
            case 63: return "FMOD_ERR_OUTPUT_FORMAT";
            case 64: return "FMOD_ERR_OUTPUT_INIT";
            case 65: return "FMOD_ERR_OUTPUT_NODRIVERS";
            case 67: return "FMOD_ERR_PLUGIN_MISSING";
            case 74: return "FMOD_ERR_UNINITIALIZED";
            case 75: return "FMOD_ERR_UNSUPPORTED";
            case 80: return "FMOD_ERR_VERSION";
            case 88: return "FMOD_ERR_OUTPUT_INIT_MISSING";
            default: return "FMOD_ERR_#" + r;
        }
    }
}
