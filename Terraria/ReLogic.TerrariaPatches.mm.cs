using System;
using MonoMod;
using SDL2;

// ReLogic's Linux window and clipboard services call SDL3 directly. The methods themselves stay as they are (replacing the
// clipboard overrides breaks their vtable slots); each SDL3 call is linked to an SDL2 implementation instead.
namespace ReLogic.Patches
{
	static class Sdl3ToSdl2
	{
		[MonoModLinkFrom("System.String SDL3.SDL::SDL_GetClipboardText()")]
		public static string SDL_GetClipboardText()
		{
			return SDL.SDL_GetClipboardText();
		}

		[MonoModLinkFrom("SDL3.SDL/SDLBool SDL3.SDL::SDL_SetClipboardText(System.String)")]
		public static global::SDL3.SDL.SDLBool SDL_SetClipboardText(string text)
		{
			return SDL.SDL_SetClipboardText(text) == 0;
		}

		[MonoModLinkFrom("SDL3.SDL/SDLBool SDL3.SDL::SDL_RaiseWindow(System.IntPtr)")]
		public static global::SDL3.SDL.SDLBool SDL_RaiseWindow(IntPtr window)
		{
			SDL.SDL_RaiseWindow(window);
			return true;
		}

		// ReLogic only tests RESIZABLE (0x20) and BORDERLESS (0x10), which have the same values in SDL2 and SDL3
		[MonoModLinkFrom("SDL3.SDL/SDL_WindowFlags SDL3.SDL::SDL_GetWindowFlags(System.IntPtr)")]
		public static global::SDL3.SDL.SDL_WindowFlags SDL_GetWindowFlags(IntPtr window)
		{
			return (global::SDL3.SDL.SDL_WindowFlags)(ulong)SDL.SDL_GetWindowFlags(window);
		}

		[MonoModLinkFrom("SDL3.SDL/SDLBool SDL3.SDL::SDL_SetWindowPosition(System.IntPtr,System.Int32,System.Int32)")]
		public static global::SDL3.SDL.SDLBool SDL_SetWindowPosition(IntPtr window, int x, int y)
		{
			SDL.SDL_SetWindowPosition(window, x, y);
			return true;
		}

		[MonoModLinkFrom("SDL3.SDL/SDLBool SDL3.SDL::SDL_GetWindowPosition(System.IntPtr,System.Int32&,System.Int32&)")]
		public static global::SDL3.SDL.SDLBool SDL_GetWindowPosition(IntPtr window, out int x, out int y)
		{
			SDL.SDL_GetWindowPosition(window, out x, out y);
			return true;
		}

		[MonoModLinkFrom("SDL3.SDL/SDLBool SDL3.SDL::SDL_GetWindowSize(System.IntPtr,System.Int32&,System.Int32&)")]
		public static global::SDL3.SDL.SDLBool SDL_GetWindowSize(IntPtr window, out int w, out int h)
		{
			SDL.SDL_GetWindowSize(window, out w, out h);
			return true;
		}
	}
}
